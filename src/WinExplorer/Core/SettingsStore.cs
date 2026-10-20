using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinExplorer.Core;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Loading never throws: a corrupt
/// or missing file produces defaults (and the bad file is kept as <c>settings.bad.json</c>).
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null) => _path = path ?? AppPaths.SettingsFile;

    public string Path => _path;

    /// <summary>The current settings instance (never null).</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised after <see cref="Save"/> wrote a new file.</summary>
    public event EventHandler<AppSettings>? Saved;

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                        if (loaded is not null)
                        {
                            Current = Sanitize(loaded);
                            return Current;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                TryQuarantine();
            }

            Current = new AppSettings();
            return Current;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Current = settings;
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonSerializer.Serialize(settings, JsonOptions);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Persisting settings is best effort; the running instance keeps them in memory.
            }
        }

        Saved?.Invoke(this, Current);
    }

    private void TryQuarantine()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var bad = System.IO.Path.ChangeExtension(_path, ".bad.json");
            File.Move(_path, bad, overwrite: true);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Clamps values that could otherwise break the app (e.g. hand edited JSON).</summary>
    internal static AppSettings Sanitize(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.WhenAlreadyOpen))
            settings.WhenAlreadyOpen = AlreadyOpenBehavior.SwitchToExistingTab;
        if (!Enum.IsDefined(settings.TargetWindow))
            settings.TargetWindow = TargetWindowPolicy.ForegroundWindow;

        settings.ExcludedPaths ??= new List<string>();
        settings.ExcludedPaths = settings.ExcludedPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (settings.SchemaVersion <= 0)
            settings.SchemaVersion = 1;

        return settings;
    }
}
