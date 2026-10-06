namespace ForzaTelemetrySplitter.Config;

/// <summary>
/// A known telemetry-consuming tool the user might forward to. Only tools that actually
/// read Forza's UDP "Data Out" stream belong here — calculator tuners like ForzaTune do
/// not read telemetry and are intentionally excluded.
/// </summary>
public sealed record TunerPreset(string Name, int DefaultPort, string Note);

/// <summary>
/// Catalog used to populate the add/edit destination dropdown. Ports are sensible defaults;
/// users can override them, since any tool can be reconfigured to a different listen port.
/// </summary>
public static class TunerPresets
{
    // Ports below are each tool's OWN existing default listen port — the splitter forwards to
    // them as-is, so users don't reconfigure tools they already have working. (The splitter
    // itself listens on its own dedicated port; see AppConfig.ListenPort.)
    public static readonly IReadOnlyList<TunerPreset> All = new[]
    {
        new TunerPreset("VirtualTCU", 5555,
            "Auto-shifting transmission controller. Keep its normal listen port 5555 — no change needed."),
        new TunerPreset("ForzaDash", 1234,
            "Open-source FH6 telemetry dashboard. Listens on 1234 by default."),
        new TunerPreset("HorizonTuner", 8000,
            "Open-source FH6 telemetry analyzer, tuning workbench and HUD. Listens on 8000 by default."),
        new TunerPreset("Forza-data-tools", 9999,
            "Open-source CLI/dashboard for Data Out. Listens on 9999 by default."),
        new TunerPreset("SIM Dashboard", 5685,
            "Phone/tablet dashboard. Listens on 5685 by default — use the device's IP, not 127.0.0.1, if it's another device."),
        new TunerPreset("SimHub", 20777,
            "Dashboard/effects suite. Uses 20777 by default for Forza."),
        new TunerPreset("co-driver", 5300,
            "Open-source (MIT) dyno + tune workbench. Note: 5300 is at the edge of Forza's reserved 5200–5300 range; confirm it works."),
        new TunerPreset("Tune It Yourself", 5685,
            "Live-telemetry auto-tuner (paid). Often receives over Wi-Fi to a phone/tablet — use that device's IP instead of 127.0.0.1."),
        new TunerPreset("Custom…", 9000,
            "Any other tool. Enter its name, IP and the port IT listens on. Avoid Forza's reserved 5200–5300 range."),
    };

    /// <summary>The entry used for fully manual destinations.</summary>
    public static TunerPreset Custom => All[^1];
}
