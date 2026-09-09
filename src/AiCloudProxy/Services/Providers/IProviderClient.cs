using AiCloudProxy.Models;

namespace AiCloudProxy.Services.Providers;

public interface IProviderClient
{
    string Model { get; }
    Task<ProviderChatResult> ChatAsync(ProviderRequest request, CancellationToken ct);

    /// <summary>Queries the provider for the models available to the configured API key.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
