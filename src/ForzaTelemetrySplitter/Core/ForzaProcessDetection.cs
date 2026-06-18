namespace ForzaTelemetrySplitter.Core;

/// <summary>
/// Pure, dependency-free detection logic for the Forza process watcher: the known-title map and a
/// debounce stepper. Kept separate from <c>ForzaProcessWatcher</c> (which owns the WinForms timer and
/// the actual process enumeration) so this logic compiles into the headless EngineTest project, which
/// targets plain net10.0 and cannot reference System.Windows.Forms.
/// </summary>
public static class ForzaProcessDetection
{
    /// <summary>
    /// Known Forza process names (without ".exe", lower-cased) mapped to a user-facing title. FH6 is
    /// the only name the issue confirms; FH4/FH5 follow the documented ForzaHorizonN.exe convention,
    /// FM7 is ForzaMotorsport7.exe, and the 2023 Forza Motorsport reboot ships as ForzaMotorsport.exe.
    /// Add a new title here as a single entry. Order matters only for the (unlikely) case of two Forza
    /// games running at once, where the first match by enumeration order wins.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownGames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["forzahorizon4"] = "Forza Horizon 4",
            ["forzahorizon5"] = "Forza Horizon 5",
            ["forzahorizon6"] = "Forza Horizon 6",
            ["forzamotorsport7"] = "Forza Motorsport 7",
            ["forzamotorsport"] = "Forza Motorsport",
        };

    /// <summary>
    /// Return the display name of the first running process that matches a known Forza title, or null
    /// if none match. Names are matched case-insensitively; a trailing ".exe" (if present) is ignored,
    /// so both raw process names (as from <c>Process.ProcessName</c>) and full file names work.
    /// </summary>
    public static string? MatchRunningGame(IEnumerable<string> processNames)
    {
        foreach (var raw in processNames)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            if (KnownGames.TryGetValue(name, out var display))
                return display;
        }
        return null;
    }

    /// <summary>
    /// Debounce for the watcher: only reports a Forza title as stably present after the same detection
    /// has held for <see cref="StablePolls"/> consecutive polls, and stably absent likewise. A brief
    /// process blip (detected then gone within the window, or vice versa) is suppressed, so it never
    /// thrashes start/stop. Drive it one poll at a time via <see cref="Step"/>; it holds no timers.
    /// </summary>
    public sealed class Debouncer
    {
        /// <summary>Consecutive polls a changed detection must hold before it is reported.</summary>
        public const int StablePolls = 2;

        private string? _stable;        // last reported stable game (null = stably idle)
        private string? _candidate;     // detection currently accumulating confirmations
        private int _candidateCount;

        /// <summary>The currently reported stable game, or null when stably idle.</summary>
        public string? Stable => _stable;

        /// <summary>
        /// Feed one poll's detection (<paramref name="detectedNow"/> = matched title this poll, or
        /// null). Returns true when the stable state changed on this step, in which case
        /// <see cref="Stable"/> holds the new value (a non-null game = just-started, null = just-stopped).
        /// </summary>
        public bool Step(string? detectedNow)
        {
            // Detection agrees with the stable state: nothing pending, reset any half-formed candidate.
            if (detectedNow == _stable)
            {
                _candidate = null;
                _candidateCount = 0;
                return false;
            }

            // A change is pending. Count consecutive polls reporting the same candidate.
            if (detectedNow == _candidate)
            {
                _candidateCount++;
            }
            else
            {
                _candidate = detectedNow;
                _candidateCount = 1;
            }

            if (_candidateCount >= StablePolls)
            {
                _stable = _candidate;
                _candidate = null;
                _candidateCount = 0;
                return true;
            }
            return false;
        }
    }
}
