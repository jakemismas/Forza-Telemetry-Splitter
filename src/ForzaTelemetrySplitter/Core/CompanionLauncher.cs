namespace ForzaTelemetrySplitter.Core;

/// <summary>
/// Pure, dependency-free decision logic for the companion launcher: which enabled companions
/// should be launched given a snapshot of running process names. Kept separate from the WinForms
/// launch site (MainForm calls <c>Process.Start</c>) so this logic compiles into the headless
/// EngineTest project, which targets plain net10.0 and cannot reference System.Windows.Forms.
///
/// Already-running detection is deliberately stateless and admin-free: it reads
/// <c>Process.ProcessName</c> (never MainModule.FileName, which needs elevation), exactly as the
/// Forza game watcher does. A registered <c>.exe</c> is matched by its file name (minus the
/// ".exe"); a <c>.bat</c> has no trackable image of its own — running it spawns a transient cmd
/// that exits, and the real tool's process name is unrelated — so a .bat is always treated as
/// eligible to launch. See issue #2 for the design rationale.
/// </summary>
public static class CompanionLauncher
{
    /// <summary>
    /// The process name a configured path would appear as in a running-process snapshot, or null
    /// when no reliable name can be derived (e.g. a .bat). For a .exe this is the file name with the
    /// ".exe" extension stripped, matching how <see cref="System.Diagnostics.Process.ProcessName"/>
    /// reports it.
    /// </summary>
    public static string? ProcessKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string fileName;
        try { fileName = System.IO.Path.GetFileName(path.Trim()); }
        catch { return null; } // invalid path characters — treat as not trackable

        if (string.IsNullOrEmpty(fileName)) return null;

        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return fileName[..^4];

        // .bat (or anything without a derivable image name) is not trackable.
        return null;
    }

    /// <summary>
    /// True when the companion's resolved process key matches (case-insensitively) one of the
    /// supplied running process names. Returns false for paths with no derivable key (e.g. .bat),
    /// so those are always considered eligible to launch.
    /// </summary>
    public static bool IsAlreadyRunning(string path, IEnumerable<string> runningProcessNames)
    {
        var key = ProcessKey(path);
        if (key is null) return false;

        foreach (var raw in runningProcessNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (string.Equals(raw.Trim(), key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The enabled companions that should be launched now: those with a non-empty path that are not
    /// already running. Order is preserved. This is the single decision point the launch hook and
    /// the EngineTest harness both drive.
    /// </summary>
    public static IReadOnlyList<Companion> SelectToLaunch(
        IEnumerable<Companion> companions, IEnumerable<string> runningProcessNames)
    {
        // Materialize once so the running-name list isn't re-enumerated per companion.
        var running = runningProcessNames as IReadOnlyList<string> ?? runningProcessNames.ToList();

        var result = new List<Companion>();
        foreach (var c in companions)
        {
            if (c is null || !c.Enabled) continue;
            if (string.IsNullOrWhiteSpace(c.Path)) continue;
            if (IsAlreadyRunning(c.Path, running)) continue;
            result.Add(c);
        }
        return result;
    }
}
