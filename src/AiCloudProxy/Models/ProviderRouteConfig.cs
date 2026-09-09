namespace AiCloudProxy.Models;

/// <summary>
/// One active provider route passed to the proxy on start. The proxy routes each
/// incoming request to the route whose model list contains the requested model
/// (or to the default route for unknown/ambiguous names).
/// </summary>
public sealed class ProviderRouteConfig
{
    /// <summary>Provider identity used in qualified "model@Provider" names.</summary>
    public string Key { get; set; } = "";

    public ProviderConfig Config { get; set; } = new();
}
