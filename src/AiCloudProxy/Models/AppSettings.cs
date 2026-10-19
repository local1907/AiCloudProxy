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

    /// <summary>
    /// Model-ID keywords hidden from the advertised model lists (Options tab).
    /// <c>null</c> means "never configured" → <see cref="Services.ModelFilter.DefaultBannedKeywords"/>
    /// seeds it. An empty list means nothing is filtered by name.
    /// </summary>
    public List<string>? BannedModelKeywords { get; set; }

    /// <summary>Check GitHub for a newer release each time the app starts (Options tab).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Release version the user chose to skip, so its banner is not shown again.</summary>
    public string? SkippedUpdateVersion { get; set; }
}
