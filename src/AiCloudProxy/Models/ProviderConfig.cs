namespace AiCloudProxy.Models;

/// <summary>Immutable snapshot of the settings used for a proxy session or a test run.</summary>
public class ProviderConfig
{
    public ProviderType Provider { get; set; }
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
}
