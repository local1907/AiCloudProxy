namespace AiCloudProxy.Services.Providers;

/// <summary>Raised when the upstream provider rejects a request or cannot be reached.</summary>
public class ProviderException : Exception
{
    public ProviderException(string message) : base(message) { }
    public ProviderException(string message, Exception inner) : base(message, inner) { }
}
