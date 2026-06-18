using System.Diagnostics;

namespace ForzaTelemetrySplitter.Core;

/// <summary>
/// Polls the running processes every couple of seconds for a known Forza title and raises
/// <see cref="GameDetected"/> / <see cref="GameClosed"/> as one starts or stops, debounced so a brief
/// process blip never thrashes. Enumerates only the current user's session via
/// <see cref="Process.GetProcesses()"/>, so it needs no administrator rights.
///
/// The detection logic (title map + debounce) lives in <see cref="ForzaProcessDetection"/> so it can be
/// unit-tested headlessly; this class is the thin live layer (timer + process enumeration). It runs on a
/// <see cref="System.Windows.Forms.Timer"/>, so its events fire on the UI thread and need no marshalling.
/// </summary>
public sealed class ForzaProcessWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly ForzaProcessDetection.Debouncer _debounce = new();

    /// <summary>Raised on the UI thread when a Forza title becomes stably present (carries its name).</summary>
    public event Action<string>? GameDetected;

    /// <summary>Raised on the UI thread when the previously-detected Forza title becomes stably absent.</summary>
    public event Action? GameClosed;

    public ForzaProcessWatcher(int pollIntervalMs = 2500)
    {
        _timer.Interval = pollIntervalMs;
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        string? detected;
        try
        {
            detected = ForzaProcessDetection.MatchRunningGame(EnumerateProcessNames());
        }
        catch
        {
            // Process enumeration can transiently throw (a process exiting mid-snapshot surfaces a
            // Win32Exception). A failed poll must never kill the watcher; just skip this tick.
            return;
        }

        if (!_debounce.Step(detected)) return;

        if (_debounce.Stable is { } game)
            GameDetected?.Invoke(game);
        else
            GameClosed?.Invoke();
    }

    /// <summary>
    /// Snapshot of current process names. Each <see cref="Process.ProcessName"/> read is guarded because
    /// a process can exit between the enumeration and the property read.
    /// </summary>
    private static IEnumerable<string> EnumerateProcessNames()
    {
        var procs = Process.GetProcesses();
        try
        {
            var names = new List<string>(procs.Length);
            foreach (var p in procs)
            {
                try { names.Add(p.ProcessName); }
                catch { /* process gone since the snapshot — skip it */ }
            }
            return names;
        }
        finally
        {
            foreach (var p in procs)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
