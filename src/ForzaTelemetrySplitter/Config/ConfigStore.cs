using System.Text.Json;

namespace ForzaTelemetrySplitter.Config;

/// <summary>
/// Loads and saves <see cref="AppConfig"/> as JSON under
/// %APPDATA%\ForzaTelemetrySplitter\config.json. On first run (or if the file is missing or
/// corrupt) returns sensible defaults so the app is usable immediately.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ConfigDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ForzaTelemetrySplitter");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var fresh = AppConfig.CreateDefault();
                Save(fresh);
                return fresh;
            }

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg is null) return AppConfig.CreateDefault();
            Sanitize(cfg);
            return cfg;
        }
        catch
        {
            // A malformed config should never block launch — fall back to defaults.
            return AppConfig.CreateDefault();
        }
    }

    /// <summary>
    /// Defensive clamping of values loaded from a (possibly hand-edited) config file, so an
    /// out-of-range port can't reach the socket layer. Ports are clamped to 1–65535.
    /// </summary>
    private static void Sanitize(AppConfig cfg)
    {
        cfg.ListenPort = Math.Clamp(cfg.ListenPort, 1, 65535);
        cfg.Destinations ??= new();
        foreach (var d in cfg.Destinations)
            d.Port = Math.Clamp(d.Port, 1, 65535);

        // An explicit "Companions": null in a hand-edited config would otherwise crash the launch
        // pass; normalize it to an empty list.
        cfg.Companions ??= new();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Saving is best-effort; a transient write failure shouldn't crash the app.
        }
    }
}
