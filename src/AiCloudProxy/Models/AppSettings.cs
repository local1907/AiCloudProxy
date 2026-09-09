namespace AiCloudProxy.Models;

/// <summary>Persisted application settings.</summary>
public class AppSettings
{
    public int Port { get; set; } = 11435;

    /// <summary>Key of the currently selected provider (built-in enum name or custom provider name).</summary>
    public string Provider { get; set; } = "DeepSeek";

    public bool AutoStartProxy { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>True once the first-run quick tour has been shown.</summary>
    public bool HasSeenTour { get; set; }

    public List<CustomProvider> CustomProviders { get; set; } = new();

    /// <summary>Saved API key per provider key, so switching providers restores each key.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();

    /// <summary>Saved Base URL override per provider key (built-ins fall back to their default).</summary>
    public Dictionary<string, string> BaseUrls { get; set; } = new();

    /// <summary>Last used model per provider key.</summary>
    public Dictionary<string, string> Models { get; set; } = new();
}
