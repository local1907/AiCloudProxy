namespace AiCloudProxy.Models;

/// <summary>
/// A user-defined, OpenAI-compatible AI provider. The name is the identity used
/// in the provider dropdown and in <see cref="AppSettings"/> per-provider
/// ApiKeys / BaseUrls / Models dictionaries.
/// </summary>
public class CustomProvider
{
    public string Name { get; set; } = "";
}
