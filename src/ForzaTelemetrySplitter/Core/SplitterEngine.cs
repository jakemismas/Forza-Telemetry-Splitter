using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ForzaTelemetrySplitter.Core;

/// <summary>
/// Immutable snapshot of engine state, handed to the UI ~4x/sec.
/// </summary>
public readonly record struct EngineStatus(
    bool Running,
    bool Receiving,
    bool IsRaceOn,
    int PacketsPerSecond,
    int Gear,
    float SpeedMps,
    ForzaFormat Format);

/// <summary>
/// The fan-out relay. Binds the UDP port Forza's "Data Out" targets, then resends every
/// received datagram, byte-for-byte, to each enabled <see cref="Destination"/>.
///
/// Design notes (see plan):
///  - A single blocking receive loop on a dedicated thread. Volume is tiny (~60-120 pkt/s),
///    and a plain loop has lower, more predictable latency than async/threadpool fan-out.
///  - No parsing on the hot path: we forward raw bytes and only peek byte 0 for IsRaceOn.
///  - One source socket, many SendTo() calls per packet.
/// </summary>
public sealed class SplitterEngine
{
    private readonly object _gate = new();
    private Socket? _socket;       // receive socket (bound to the listen port)
    private Socket? _sendSocket;   // separate socket for forwarding — see Start() for why
    private Thread? _rxThread;
    private volatile bool _running;

    // Remembered listen endpoint so the watchdog can rebind after an unexpected socket failure.
    private string _listenIp = "127.0.0.1";
    private int _listenPort;

    // Session recorder (Part C). Null unless recording is active.
    private SessionRecorder? _recorder;

    // Rolling 1 Hz packets/sec history for the Activity graph. Sampled by a dedicated ~1 Hz timer
    // (see OnSampleTick), NOT by packet arrival, so a second with no packets still emits one
    // float.NaN "no data" sample. That is what lets the chart break the line into a gap the moment
    // the game closes, instead of holding the last value. The timer runs entirely off the forward
    // path (the rx thread only bumps a counter), so it adds no jitter to forwarding.
    private readonly ActivityHistory _history = new();
    private System.Threading.Timer? _sampleTimer;
    private const int SampleIntervalMs = 1000;

    /// <summary>Rolling last-hour packets/sec history (in memory only; never persisted).</summary>
    public ActivityHistory History => _history;

    // Windows IOCTL to stop a UDP socket from surfacing ICMP "port unreachable" as a
    // connection-reset exception. Without this, forwarding to a local port that nothing is
    // listening on can poison the socket and break the relay.
    private const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);

    private List<Destination> _destinations = new();

    // Status fields written on the rx thread, read by the UI timer.
    private long _lastPacketTicks;          // Stopwatch ticks of the last valid packet
    private volatile bool _lastIsRaceOn;
    private int _lastGear;                  // latest parsed gear
    private float _lastSpeedMps;            // latest parsed speed (m/s)
    private volatile ForzaFormat _lastFormat = ForzaFormat.Unknown; // latest detected game/format
    private int _packetsThisSecond;         // accumulator (Interlocked: rx thread adds, sample tick reads+clears)
    private int _packetsPerSecond;          // last completed 1s window (written by the sample tick)

    /// <summary>Raised when binding fails or the socket dies unexpectedly (already-formatted text).</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// Raised when the listen port is already in use (carries the port). Kept separate from
    /// ErrorOccurred so the UI can present a localized message — the engine stays free of any
    /// resource/UI dependency (the test harnesses compile this file standalone).
    /// </summary>
    public event Action<int>? PortInUse;

    public bool Running => _running;

    /// <summary>
    /// Replace the active destination list. Safe to call while running; the rx loop reads
    /// the reference atomically each packet, so swapping the list takes effect immediately.
    /// </summary>
    public void SetDestinations(IEnumerable<Destination> destinations)
    {
        var list = destinations.ToList();
        lock (_gate) { _destinations = list; }
    }

    /// <summary>
    /// Bind and start the receive loop. Returns true on success; on failure (e.g. the port is
    /// already taken by another running splitter or by VirtualTCU still on 5555) returns false
    /// and raises <see cref="ErrorOccurred"/> with a human-readable explanation.
    /// </summary>
    public bool Start(string listenIp, int listenPort)
    {
        lock (_gate)
        {
            if (_running) return true;

            _listenIp = listenIp;
            _listenPort = listenPort;

            try
            {
                (_socket, _sendSocket) = CreateBoundSockets(listenIp, listenPort);
                _running = true;
                Interlocked.Exchange(ref _packetsThisSecond, 0);

                _rxThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name = "ForzaSplitter-Rx",
                    Priority = ThreadPriority.AboveNormal,
                };
                _rxThread.Start();

                // Steady ~1 Hz sampler. Each tick records the past second's pkt count, or NaN when no
                // packets arrived, so idle and game-closed seconds become gaps on the chart.
                _sampleTimer = new System.Threading.Timer(OnSampleTick, null, SampleIntervalMs, SampleIntervalMs);
                return true;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode == SocketError.AddressAlreadyInUse ||   // WSAEADDRINUSE (10048)
                ex.SocketErrorCode == SocketError.AccessDenied)            // WSAEACCES   (10013)
            {
                // Both codes mean "something else already owns this port". Windows reports an
                // exclusively-bound UDP port (e.g. VirtualTCU on 5555) as AccessDenied, not
                // AddressAlreadyInUse — so we must treat them the same.
                _running = false;
                PortInUse?.Invoke(listenPort);
                return false;
            }
            catch (Exception ex)
            {
                _running = false;
                ErrorOccurred?.Invoke($"Could not start the splitter on {listenIp}:{listenPort}.\n\n{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Create the bound receive socket and the dedicated send socket for a listen endpoint.
    /// Throws SocketException on bind failure (caller decides how to surface it). Shared by
    /// <see cref="Start"/> and the watchdog rebind.
    /// </summary>
    private static (Socket recv, Socket send) CreateBoundSockets(string listenIp, int listenPort)
    {
        if (!IPAddress.TryParse(listenIp, out var bindAddr))
            bindAddr = IPAddress.Loopback;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveBufferSize = 1 << 20; // 1 MB, so a brief UI hiccup never drops Forza packets

        // Stop a dead forwarding target from poisoning this socket. Forwarding to a local port with
        // nothing listening yields an ICMP "port unreachable" that, by default, makes the NEXT receive
        // throw ConnectionReset (WSAECONNRESET). SIO_UDP_CONNRESET disables that. (Best-effort.)
        try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }

        socket.Bind(new IPEndPoint(bindAddr, listenPort));

        // Forward on a SEPARATE socket so a failing/"unreachable" send can never affect the receive
        // socket or deliveries to other destinations.
        var sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sendSocket.SendBufferSize = 1 << 20;
        try { sendSocket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); } catch { }

        return (socket, sendSocket);
    }

    /// <summary>Stop the loop and release the socket. Safe to call when already stopped.</summary>
    public void Stop()
    {
        Thread? rx;
        Socket? sock;
        Socket? send;
        System.Threading.Timer? timer;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            sock = _socket;
            _socket = null;
            send = _sendSocket;
            _sendSocket = null;
            rx = _rxThread;
            _rxThread = null;
            timer = _sampleTimer;
            _sampleTimer = null;
        }

        // Closing the socket unblocks the blocking Receive() in the loop.
        try { sock?.Close(); } catch { /* ignore */ }
        try { send?.Close(); } catch { /* ignore */ }
        try { rx?.Join(500); } catch { /* ignore */ }
        // Dispose the sampler. _running is already false, so OnSampleTick is a no-op even if one
        // callback is already in flight; disposal stops any future ticks.
        try { timer?.Dispose(); } catch { /* ignore */ }

        StopRecording(); // close any open capture file

        _packetsPerSecond = 0;
        Interlocked.Exchange(ref _packetsThisSecond, 0);
    }

    /// <summary>True while a session is being captured to disk.</summary>
    public bool IsRecording => _recorder is not null;

    /// <summary>Begin recording received packets to <paramref name="path"/> (.fts). Returns false if
    /// the file can't be created.</summary>
    public bool StartRecording(string path)
    {
        lock (_gate)
        {
            _recorder?.Dispose();
            try { _recorder = new SessionRecorder(path); return true; }
            catch (Exception ex)
            {
                _recorder = null;
                ErrorOccurred?.Invoke($"Could not start recording to {path}.\n\n{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Stop recording and close the file. Safe to call when not recording.</summary>
    public void StopRecording()
    {
        lock (_gate)
        {
            _recorder?.Dispose();
            _recorder = null;
        }
    }

    private void ReceiveLoop()
    {
        // UDP max datagram, so a normal Receive can never throw MessageSize (which a malicious
        // oversized packet would otherwise trigger). Forza packets are ≤331 bytes.
        var buffer = new byte[65535];
        int rebindFailures = 0;

        while (_running)
        {
            var socket = _socket;
            var sendSocket = _sendSocket;
            if (socket is null || sendSocket is null) break;

            int received;
            try
            {
                received = socket.Receive(buffer);
            }
            catch (SocketException) when (!_running)
            {
                break; // expected: socket closed by Stop()
            }
            catch (ObjectDisposedException)
            {
                if (!_running) break;
                // Socket disposed unexpectedly while running — try to recover (watchdog).
                if (TryRebind(ref rebindFailures)) continue; else break;
            }
            catch (SocketException ex)
            {
                if (!_running) break;
                // A single bad datagram (e.g. an oversized packet -> MessageSize, or a stray ICMP
                // reset) must NOT stop the relay. Ignore transient per-packet errors and keep going.
                if (IsTransientReceiveError(ex.SocketErrorCode)) continue;
                // Anything else means the socket itself is likely dead — attempt a watchdog rebind.
                if (TryRebind(ref rebindFailures)) continue; else break;
            }

            rebindFailures = 0; // a successful receive resets the watchdog backoff

            // Forward first (latency-critical), account for status second.
            var span = new ReadOnlySpan<byte>(buffer, 0, received);

            List<Destination> dests;
            lock (_gate) { dests = _destinations; }

            for (int i = 0; i < dests.Count; i++)
            {
                var d = dests[i];
                if (!d.Enabled) continue;
                var ep = d.GetEndPoint();
                if (ep is null) continue;
                try
                {
                    sendSocket.SendTo(buffer, 0, received, SocketFlags.None, ep);
                    Interlocked.Increment(ref d.ForwardedCount);
                    if (d.LastSendFailed) d.LastSendFailed = false;
                }
                catch (SocketException)
                {
                    // A downstream tool that isn't listening yet just yields a transient error
                    // on localhost. Ignore — it will start receiving once it binds its port.
                    // Forwarding happens on a dedicated send socket, so this never affects the
                    // receive socket or deliveries to other destinations.
                    d.LastSendFailed = true;
                }
            }

            // Record AFTER forwarding so capturing can never delay live tools.
            _recorder?.Write(buffer, received);

            UpdateStatus(span);
        }
    }

    /// <summary>Per-packet errors that should be ignored so the receive loop survives.</summary>
    private static bool IsTransientReceiveError(SocketError code) => code switch
    {
        SocketError.MessageSize => true,        // oversized datagram (the audit's DoS vector)
        SocketError.ConnectionReset => true,    // stray ICMP port-unreachable (belt-and-braces)
        SocketError.NetworkReset => true,
        SocketError.Interrupted => true,
        _ => false,
    };

    /// <summary>
    /// Watchdog: the receive socket died unexpectedly while running. Close both sockets, back off
    /// briefly, and rebind the listen endpoint. Returns true if the loop should continue, false to
    /// give up (after surfacing the reason). Bounded so it never hot-spins.
    /// </summary>
    private bool TryRebind(ref int failures)
    {
        const int maxFailures = 10;

        // Tear down the dead sockets.
        try { _socket?.Close(); } catch { }
        try { _sendSocket?.Close(); } catch { }
        _socket = null;
        _sendSocket = null;

        if (!_running) return false;

        failures++;
        if (failures > maxFailures)
        {
            ErrorOccurred?.Invoke(
                $"The splitter lost its connection on {_listenIp}:{_listenPort} and could not recover.");
            _running = false;
            return false;
        }

        Thread.Sleep(Math.Min(500 * failures, 3000)); // linear backoff, capped at 3s
        if (!_running) return false;

        try
        {
            (_socket, _sendSocket) = CreateBoundSockets(_listenIp, _listenPort);
            return true;
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode == SocketError.AddressAlreadyInUse ||
            ex.SocketErrorCode == SocketError.AccessDenied)
        {
            // Something else grabbed the port while we were down — report and stop.
            PortInUse?.Invoke(_listenPort);
            _running = false;
            return false;
        }
        catch
        {
            return true; // transient bind failure; back off and retry on the next loop turn
        }
    }

    private void UpdateStatus(ReadOnlySpan<byte> packet)
    {
        var format = ForzaPacket.Detect(packet.Length);
        if (format == ForzaFormat.Unknown)
            return; // ignore non-Forza traffic for status purposes (forwarding still happened)

        _lastFormat = format;
        _lastIsRaceOn = ForzaPacket.IsRaceOn(packet);
        _lastGear = ForzaPacket.Gear(packet, format);
        _lastSpeedMps = ForzaPacket.SpeedMetersPerSecond(packet, format);
        Volatile.Write(ref _lastPacketTicks, Stopwatch.GetTimestamp());

        // Hot path: just count. The 1 Hz OnSampleTick reads and clears this and writes the history,
        // so forwarding never waits on a lock or a history write.
        Interlocked.Increment(ref _packetsThisSecond);
    }

    /// <summary>
    /// Runs ~once per second on a timer thread, independent of packet arrival. Records the past
    /// second's packet count as the latest pkts/s, and appends it to the activity history — or
    /// float.NaN when no packets arrived, so idle and game-closed seconds render as gaps rather than
    /// a held flat line. A no-op once the engine has stopped, so a final queued callback can never
    /// write a stray sample after teardown.
    /// </summary>
    private void OnSampleTick(object? state)
    {
        if (!_running) return;
        int count = Interlocked.Exchange(ref _packetsThisSecond, 0);
        _packetsPerSecond = count;
        _history.Add(count > 0 ? count : float.NaN);
    }

    /// <summary>
    /// Current status snapshot for the UI. "Receiving" = a valid Forza packet seen within
    /// the last second.
    /// </summary>
    public EngineStatus GetStatus()
    {
        long last = Volatile.Read(ref _lastPacketTicks);
        bool receiving = false;
        if (last != 0)
        {
            double secondsSince = (Stopwatch.GetTimestamp() - last) / (double)Stopwatch.Frequency;
            receiving = secondsSince < 1.0;
        }

        // If we haven't seen a packet in over a second, pkts/s is effectively 0.
        int pps = receiving ? _packetsPerSecond : 0;

        return new EngineStatus(
            Running: _running,
            Receiving: receiving,
            IsRaceOn: receiving && _lastIsRaceOn,
            PacketsPerSecond: pps,
            Gear: receiving ? _lastGear : 0,
            SpeedMps: receiving ? _lastSpeedMps : 0f,
            Format: receiving ? _lastFormat : ForzaFormat.Unknown);
    }

}
