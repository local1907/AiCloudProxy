using System.IO;
using System.Text.Json;
using AiCloudProxy.Models;

namespace AiCloudProxy.Services;

/// <summary>Loads and saves app settings as JSON in %APPDATA%\AiCloudProxy\settings.json.</summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public AppSettings Settings { get; }

    public string SettingsPath => _path;

    public SettingsService()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AiCloudProxy",
            "settings.json");
        MigrateFromLegacyPath();
        Settings = Load();
    }

    /// <summary>
    /// Copies the old %APPDATA%\OllamaCloudProxy\settings.json over on the first run
    /// after the rename so saved providers/keys are not lost.
    /// </summary>
    private void MigrateFromLegacyPath()
    {
        try
        {
            if (File.Exists(_path)) return;
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OllamaCloudProxy",
                "settings.json");
            if (File.Exists(legacy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.Copy(legacy, _path);
            }
        }
        catch
        {
            // Non-fatal: the app just starts with defaults.
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Legacy files (pre per-provider keys) stored Provider as an enum number.
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("Provider", out var providerProp) &&
                    providerProp.ValueKind == JsonValueKind.Number)
                {
                    return MigrateLegacy(root);
                }

                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall through to defaults on a corrupt settings file.
        }
        return new AppSettings();
    }

    /// <summary>Maps the old single-key settings file to the per-provider schema.</summary>
    private static AppSettings MigrateLegacy(JsonElement root)
    {
        var s = new AppSettings();
        if (root.TryGetProperty("Port", out var port) && port.TryGetInt32(out var p)) s.Port = p;
        var type = root.TryGetProperty("Provider", out var prov) && prov.TryGetInt32(out var t)
            ? (ProviderType)t
            : ProviderType.DeepSeek;
        s.Provider = type.ToString();

        if (root.TryGetProperty("ApiKey", out var key) && key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()))
            s.ApiKeys[s.Provider] = key.GetString()!;
        if (root.TryGetProperty("BaseUrl", out var baseUrl) && baseUrl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(baseUrl.GetString()))
            s.BaseUrls[s.Provider] = baseUrl.GetString()!;
        if (root.TryGetProperty("Model", out var model) && model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString()))
            s.Models[s.Provider] = model.GetString()!;
        if (root.TryGetProperty("AutoStartProxy", out var autoStart) && autoStart.ValueKind is JsonValueKind.True or JsonValueKind.False) s.AutoStartProxy = autoStart.GetBoolean();
        if (root.TryGetProperty("MinimizeToTray", out var tray) && tray.ValueKind is JsonValueKind.True or JsonValueKind.False) s.MinimizeToTray = tray.GetBoolean();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch (Exception ex)
        {
            // Non-fatal: the app keeps working, settings just aren't persisted.
            System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
        }
    }
}
