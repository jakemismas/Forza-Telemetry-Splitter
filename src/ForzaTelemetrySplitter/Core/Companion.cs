namespace ForzaTelemetrySplitter.Core;

/// <summary>
/// A downstream tool the user wants launched alongside the splitter (VirtualTCU, SimHub, a
/// custom .bat). On splitter start, each enabled companion that is not already running is
/// launched. The splitter starts these tools but never manages their lifetime — it does not stop
/// or kill them when the splitter stops.
/// </summary>
public sealed class Companion
{
    public string Name { get; set; } = "Companion";

    /// <summary>Full path to the .exe or .bat to launch.</summary>
    public string Path { get; set; } = "";

    /// <summary>Optional command-line arguments passed to the launched process.</summary>
    public string Arguments { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public override string ToString() => $"{Name} ({Path})";
}
