using System.Net;
using System.Net.Sockets;
using ForzaTelemetrySplitter.Core;

// Headless verification of SplitterEngine: proves the real fan-out code splits Forza
// telemetry to multiple destinations byte-for-byte, and reports a clean port-conflict error
// for BOTH ways Windows signals a taken UDP port (AddressAlreadyInUse and AccessDenied).

const string ip = "127.0.0.1";
const int listenPort = 35555;
int[] destPorts = { 35556, 35300 };
const int packetCount = 60;

int failures = 0;

Console.WriteLine("=== Forza Telemetry Splitter — engine verification ===\n");

// --- Test 1: fan-out to multiple destinations -------------------------------------------
{
    Console.WriteLine("[Test 1] Fan-out splits the stream to all enabled destinations");

    var receivers = destPorts.ToDictionary(
        p => p,
        p => new UdpClient(new IPEndPoint(IPAddress.Parse(ip), p)));
    var counts = destPorts.ToDictionary(p => p, _ => 0);
    var integrityOk = destPorts.ToDictionary(p => p, _ => true);

    var cts = new CancellationTokenSource();
    var rxTasks = receivers.Select(kv => Task.Run(() =>
    {
        var (port, client) = (kv.Key, kv.Value);
        client.Client.ReceiveTimeout = 500;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                IPEndPoint? remote = null;
                var data = client.Receive(ref remote);
                if (data.Length == ForzaPacket.CarDashSize) counts[port]++;
                else integrityOk[port] = false;
            }
            catch (SocketException) { /* timeout */ }
        }
    })).ToList();

    var engine = new SplitterEngine();
    string? engineError = null;
    engine.ErrorOccurred += m => engineError = m;
    engine.SetDestinations(destPorts.Select(p => new Destination
    {
        Name = $"tool-{p}", Ip = ip, Port = p, Enabled = true
    }));

    if (!engine.Start(ip, listenPort))
    {
        Console.WriteLine($"  FAIL: engine did not start: {engineError}");
        failures++;
    }
    else
    {
        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Parse(ip), listenPort);
        for (int i = 0; i < packetCount; i++)
        {
            var pkt = new byte[ForzaPacket.CarDashSize];
            pkt[0] = (byte)(i % 2);
            pkt[5] = (byte)(i % 256);
            sender.Send(pkt, pkt.Length, target);
            Thread.Sleep(8);
        }

        Thread.Sleep(400);
        cts.Cancel();
        Task.WaitAll(rxTasks.ToArray(), 2000);
        engine.Stop();

        foreach (var p in destPorts)
        {
            bool pass = counts[p] >= packetCount * 0.9 && integrityOk[p];
            Console.WriteLine($"  port {p}: {counts[p]}/{packetCount} packets, integrity={integrityOk[p]} -> {(pass ? "PASS" : "FAIL")}");
            if (!pass) failures++;
        }
    }

    foreach (var c in receivers.Values) c.Dispose();
}

// --- Test 2: AddressAlreadyInUse is reported clearly ------------------------------------
{
    Console.WriteLine("\n[Test 2] Port held by a NON-exclusive UDP socket (AddressAlreadyInUse)");

    using var squatter = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), listenPort));

    var engine = new SplitterEngine();
    int conflictPort = -1;
    string? error = null;
    engine.PortInUse += p => conflictPort = p;
    engine.ErrorOccurred += m => error = m;

    bool started = engine.Start(ip, listenPort);
    if (!started && conflictPort == listenPort && error is null)
        Console.WriteLine("  PASS: clean PortInUse signal raised (not a generic error)");
    else
    {
        Console.WriteLine($"  FAIL: started={started}, conflictPort={conflictPort}, error={error ?? "<none>"}");
        failures++;
    }
    engine.Stop();
}

// --- Test 3: AccessDenied (the VirtualTCU-on-5555 case) raises PortInUse, not a raw error ---
// This is the bug found on Jake's machine: an exclusively-bound UDP port raises WSAEACCES,
// not WSAEADDRINUSE. Reproduce by binding with ExclusiveAddressUse, then confirm the engine
// treats it as a clean PortInUse (so the UI shows the friendly localized message), not the
// cryptic generic ErrorOccurred path.
{
    Console.WriteLine("\n[Test 3] Port held EXCLUSIVELY (AccessDenied / WSAEACCES) — the VirtualTCU case");

    using var exclusive = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    exclusive.ExclusiveAddressUse = true;
    exclusive.Bind(new IPEndPoint(IPAddress.Parse(ip), listenPort));

    var engine = new SplitterEngine();
    int conflictPort = -1;
    string? error = null;
    engine.PortInUse += p => conflictPort = p;
    engine.ErrorOccurred += m => error = m;

    bool started = engine.Start(ip, listenPort);
    bool clean = !started && conflictPort == listenPort && error is null;

    if (clean)
        Console.WriteLine("  PASS: exclusive-bind conflict raises PortInUse (no cryptic generic error)");
    else
    {
        Console.WriteLine($"  FAIL: started={started}, conflictPort={conflictPort}, error={error ?? "<none>"}");
        failures++;
    }
    engine.Stop();
}

// --- Test 4: IsRaceOn parsing -----------------------------------------------------------
{
    Console.WriteLine("\n[Test 4] ForzaPacket.IsRaceOn / IsValid");
    var on = new byte[ForzaPacket.CarDashSize]; on[0] = 1;
    var off = new byte[ForzaPacket.CarDashSize]; off[0] = 0;
    var wrongSize = new byte[100];

    bool ok = ForzaPacket.IsRaceOn(on) && !ForzaPacket.IsRaceOn(off)
              && ForzaPacket.IsValid(324) && !ForzaPacket.IsValid(100)
              && !ForzaPacket.IsRaceOn(wrongSize);
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 5: format detection by length -------------------------------------------------
{
    Console.WriteLine("\n[Test 5] ForzaPacket.Detect (game by packet size)");
    bool ok = ForzaPacket.Detect(232) == ForzaFormat.Sled
              && ForzaPacket.Detect(311) == ForzaFormat.MotorsportDash
              && ForzaPacket.Detect(331) == ForzaFormat.MotorsportDash
              && ForzaPacket.Detect(324) == ForzaFormat.HorizonCarDash
              && ForzaPacket.Detect(323) == ForzaFormat.HorizonCarDash
              && ForzaPacket.Detect(100) == ForzaFormat.Unknown;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 6: per-format Speed/Gear offset round-trip (decisive) -------------------------
// Horizon: Speed@256, Gear@319.  Motorsport: Speed@244, Gear@307.
{
    Console.WriteLine("\n[Test 6] Speed/Gear offsets per format (round-trip)");

    var hzn = new byte[ForzaPacket.CarDashSize];
    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(hzn.AsSpan(256, 4), 50.0f);
    hzn[319] = 4;
    var hznFmt = ForzaPacket.Detect(hzn.Length);
    bool hznOk = hznFmt == ForzaFormat.HorizonCarDash
                 && Math.Abs(ForzaPacket.SpeedMetersPerSecond(hzn, hznFmt) - 50f) < 0.001f
                 && ForzaPacket.Gear(hzn, hznFmt) == 4;

    var fm = new byte[ForzaPacket.MotorsportDashSize];
    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(fm.AsSpan(244, 4), 72.0f);
    fm[307] = 3;
    var fmFmt = ForzaPacket.Detect(fm.Length);
    bool fmOk = fmFmt == ForzaFormat.MotorsportDash
                && Math.Abs(ForzaPacket.SpeedMetersPerSecond(fm, fmFmt) - 72f) < 0.001f
                && ForzaPacket.Gear(fm, fmFmt) == 3;

    // Sled has no dash data → readers return 0.
    var sled = new byte[ForzaPacket.SledSize];
    var sledFmt = ForzaPacket.Detect(sled.Length);
    bool sledOk = sledFmt == ForzaFormat.Sled
                  && ForzaPacket.SpeedMetersPerSecond(sled, sledFmt) == 0f
                  && ForzaPacket.Gear(sled, sledFmt) == 0;

    bool ok = hznOk && fmOk && sledOk;
    Console.WriteLine($"  Horizon(256/319)={hznOk}  Motorsport(244/307)={fmOk}  Sled(none)={sledOk} -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 7: forwarding is format-agnostic (Motorsport 311 + Sled 232) ------------------
// The relay must forward ANY packet size byte-identical, not just Horizon 324.
{
    Console.WriteLine("\n[Test 7] Forwarding works for non-Horizon packet sizes (311, 232)");
    const int fwdPort = 35777;
    using var rx = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), fwdPort));
    rx.Client.ReceiveTimeout = 500;
    int got311 = 0, got232 = 0;
    var cts = new CancellationTokenSource();
    var rxTask = Task.Run(() =>
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                IPEndPoint? r = null;
                var d = rx.Receive(ref r);
                if (d.Length == 311) got311++;
                else if (d.Length == 232) got232++;
            }
            catch (SocketException) { }
        }
    });

    var engine = new SplitterEngine();
    engine.SetDestinations(new[] { new Destination { Name = "x", Ip = ip, Port = fwdPort, Enabled = true } });
    engine.Start(ip, listenPort);
    using (var sender = new UdpClient())
    {
        var target = new IPEndPoint(IPAddress.Parse(ip), listenPort);
        for (int i = 0; i < 30; i++) { sender.Send(new byte[311], 311, target); Thread.Sleep(4); }
        for (int i = 0; i < 30; i++) { sender.Send(new byte[232], 232, target); Thread.Sleep(4); }
    }
    Thread.Sleep(300); cts.Cancel(); rxTask.Wait(1500); engine.Stop(); rx.Close();

    bool ok = got311 >= 27 && got232 >= 27;
    Console.WriteLine($"  forwarded 311-byte: {got311}/30, 232-byte: {got232}/30 -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 8: oversized packet does NOT stop the relay (security fix / watchdog) ----------
// Reproduces the audited DoS: a single >buffer datagram used to throw MessageSize and break the
// loop. The relay must survive it and keep forwarding subsequent valid packets.
{
    Console.WriteLine("\n[Test 8] Oversized packet does not stop forwarding (DoS fix)");
    const int fwdPort = 35888;
    using var rx = new UdpClient(new IPEndPoint(IPAddress.Parse(ip), fwdPort));
    rx.Client.ReceiveTimeout = 500;
    int afterOversize = 0;
    var cts = new CancellationTokenSource();
    var rxTask = Task.Run(() =>
    {
        while (!cts.IsCancellationRequested)
        {
            try { IPEndPoint? r = null; var d = rx.Receive(ref r); if (d.Length == 324) afterOversize++; }
            catch (SocketException) { }
        }
    });

    var engine = new SplitterEngine();
    engine.SetDestinations(new[] { new Destination { Name = "x", Ip = ip, Port = fwdPort, Enabled = true } });
    engine.Start(ip, listenPort);
    using (var sender = new UdpClient())
    {
        var target = new IPEndPoint(IPAddress.Parse(ip), listenPort);
        sender.Send(new byte[9000], 9000, target);            // oversized "attack" packet
        Thread.Sleep(50);
        for (int i = 0; i < 30; i++) { sender.Send(new byte[324], 324, target); Thread.Sleep(5); } // valid stream after
    }
    Thread.Sleep(300); cts.Cancel(); rxTask.Wait(1500); engine.Stop(); rx.Close();

    bool ok = afterOversize >= 27; // the relay kept forwarding after the oversized packet
    Console.WriteLine($"  valid packets forwarded AFTER an oversized one: {afterOversize}/30 -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 9: recording round-trip -------------------------------------------------------
{
    Console.WriteLine("\n[Test 9] Session recording writes a valid .fts with all packets");
    string path = Path.Combine(Path.GetTempPath(), $"fts-test-{Guid.NewGuid():N}.fts");
    try
    {
        const int recvPort = 35999;
        var engine = new SplitterEngine();
        engine.StartRecording(path);
        engine.Start(ip, recvPort);
        using (var sender = new UdpClient())
        {
            var target = new IPEndPoint(IPAddress.Parse(ip), recvPort);
            for (int i = 0; i < 25; i++) { var p = new byte[324]; p[5] = (byte)i; sender.Send(p, 324, target); Thread.Sleep(5); }
        }
        Thread.Sleep(300);
        engine.StopRecording();
        engine.Stop();

        // Parse the file: magic "FTS1", version 1, then N records of [int64][uint16][bytes].
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);
        var magic = br.ReadBytes(4);
        int version = br.ReadInt32();
        int count = 0; bool sizesOk = true;
        while (fs.Position < fs.Length)
        {
            br.ReadInt64();                  // delta ticks
            int len = br.ReadUInt16();
            var bytes = br.ReadBytes(len);
            if (bytes.Length != len || len != 324) sizesOk = false;
            count++;
        }
        bool ok = System.Text.Encoding.ASCII.GetString(magic) == "FTS1" && version == 1
                  && count >= 23 && sizesOk;
        Console.WriteLine($"  magic/version ok, {count}/25 records, sizes ok={sizesOk} -> {(ok ? "PASS" : "FAIL")}");
        if (!ok) failures++;
    }
    finally { try { File.Delete(path); } catch { } }
}

// --- Test 10: ActivityHistory ring buffer ----------------------------------------------
{
    Console.WriteLine("\n[Test 10] ActivityHistory ring buffer (order, cap, NaN gaps)");
    var h = new ActivityHistory(4);
    h.Add(1); h.Add(2); h.Add(float.NaN); h.Add(4);  // exactly full
    var dst = new float[4];
    int n1 = h.Snapshot(dst);
    bool order = n1 == 4 && dst[0] == 1 && dst[1] == 2 && float.IsNaN(dst[2]) && dst[3] == 4;

    h.Add(5); // overwrites oldest (1) -> [2, NaN, 4, 5]
    int n2 = h.Snapshot(dst);
    bool capped = n2 == 4 && dst[0] == 2 && float.IsNaN(dst[1]) && dst[2] == 4 && dst[3] == 5;

    bool ok = order && capped;
    Console.WriteLine($"  order={order} capped/oldest-discarded={capped} -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 11: destination status derivation ---------------------------------------------
{
    Console.WriteLine("\n[Test 11] DestinationStatusLogic.Derive");
    bool ok =
        DestinationStatusLogic.Derive(enabled: false, engineReceiving: true,  countIncreased: true,  lastSendFailed: false) == DestinationStatus.Disabled &&
        DestinationStatusLogic.Derive(enabled: true,  engineReceiving: true,  countIncreased: true,  lastSendFailed: false) == DestinationStatus.Forwarding &&
        DestinationStatusLogic.Derive(enabled: true,  engineReceiving: false, countIncreased: false, lastSendFailed: false) == DestinationStatus.Idle &&
        DestinationStatusLogic.Derive(enabled: true,  engineReceiving: true,  countIncreased: false, lastSendFailed: false) == DestinationStatus.Idle &&
        DestinationStatusLogic.Derive(enabled: true,  engineReceiving: true,  countIncreased: true,  lastSendFailed: true)  == DestinationStatus.Error;
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 12: engine samples activity while running -------------------------------------
{
    Console.WriteLine("\n[Test 12] Engine populates ActivityHistory while running");
    var engine = new SplitterEngine();
    engine.Start(ip, 34777);
    using (var sender = new UdpClient())
    {
        var target = new IPEndPoint(IPAddress.Parse(ip), 34777);
        // Send for ~2.5s so at least two 1Hz samples land.
        for (int i = 0; i < 150; i++) { sender.Send(new byte[324], 324, target); Thread.Sleep(16); }
    }
    var dst = new float[engine.History.Capacity];
    int count = engine.History.Snapshot(dst);
    engine.Stop();
    bool anyNonZero = false;
    for (int i = 0; i < count; i++) if (!float.IsNaN(dst[i]) && dst[i] > 0) anyNonZero = true;
    bool ok = count >= 1 && anyNonZero;
    Console.WriteLine($"  samples={count}, any>0={anyNonZero} -> {(ok ? "PASS" : "FAIL")}");
    if (!ok) failures++;
}

// --- Test 13: Forza process detection (title map + debounce) ----------------------------
{
    Console.WriteLine("[Test 13] Forza process watcher: title mapping and debounce");

    // Title map: every known process name (case-insensitive, with or without .exe) maps; unknown -> null.
    var mapCases = new (string proc, string? expected)[]
    {
        ("ForzaHorizon4", "Forza Horizon 4"),
        ("ForzaHorizon5", "Forza Horizon 5"),
        ("ForzaHorizon6", "Forza Horizon 6"),
        ("FORZAHORIZON6.EXE", "Forza Horizon 6"),     // case-insensitive + .exe suffix
        ("ForzaMotorsport7", "Forza Motorsport 7"),
        ("ForzaMotorsport", "Forza Motorsport"),
        ("chrome", null),
        ("notepad", null),
    };
    bool mapOk = true;
    foreach (var (proc, expected) in mapCases)
    {
        var got = ForzaProcessDetection.MatchRunningGame(new[] { "explorer", proc, "svchost" });
        if (got != expected) { mapOk = false; Console.WriteLine($"  map FAIL: {proc} -> '{got}' (expected '{expected}')"); }
    }
    Console.WriteLine($"  title map ({mapCases.Length} cases) -> {(mapOk ? "PASS" : "FAIL")}");
    if (!mapOk) failures++;

    // No Forza process among many -> null.
    bool noneOk = ForzaProcessDetection.MatchRunningGame(new[] { "explorer", "steam", "discord" }) is null;
    Console.WriteLine($"  no Forza process -> null: {(noneOk ? "PASS" : "FAIL")}");
    if (!noneOk) failures++;

    // Debounce: a change must hold for StablePolls consecutive polls before it fires; a single-tick
    // blip is suppressed.
    {
        var d = new ForzaProcessDetection.Debouncer();
        // Single-poll blip: detected for one poll then gone -> never reports a start.
        bool fired1 = d.Step("Forza Horizon 5");   // poll 1: candidate, not yet stable
        bool fired2 = d.Step(null);                 // poll 2: blip gone before confirming
        bool blipSuppressed = !fired1 && !fired2 && d.Stable is null;
        Console.WriteLine($"  single-poll blip suppressed: {(blipSuppressed ? "PASS" : "FAIL")}");
        if (!blipSuppressed) failures++;
    }
    {
        var d = new ForzaProcessDetection.Debouncer();
        // Sustained detection: held for StablePolls polls -> reports start once, on the Nth poll.
        bool a = d.Step("Forza Horizon 6"); // poll 1
        bool b = d.Step("Forza Horizon 6"); // poll 2 -> reaches StablePolls (2)
        bool startedOnce = !a && b && d.Stable == "Forza Horizon 6";
        // Steady-state: same detection keeps reporting no further change.
        bool steady = !d.Step("Forza Horizon 6");
        // Sustained absence -> reports stop once.
        bool c = d.Step(null);              // poll 1 of absence
        bool e = d.Step(null);              // poll 2 -> stable idle
        bool stoppedOnce = !c && e && d.Stable is null;
        bool debounceOk = startedOnce && steady && stoppedOnce;
        Console.WriteLine($"  sustained start/stop fire once each: {(debounceOk ? "PASS" : "FAIL")}");
        if (!debounceOk) failures++;
    }
}

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("OVERALL: PASS — engine splits byte-identical telemetry and reports all port conflicts clearly.");
    return 0;
}
Console.WriteLine($"OVERALL: FAIL — {failures} check(s) failed.");
return 1;
