namespace AiCloudProxy.Models;

/// <summary>Metadata for each supported AI provider.</summary>
public sealed record ProviderOption(
    ProviderType Type,
    string Key,
    string DisplayName,
    string DefaultModel,
    string DefaultBaseUrl,
    string KeyPageUrl);

public static class ProviderCatalog
{
    public static readonly ProviderOption[] All =
    {
        new(ProviderType.DeepSeek, "DeepSeek", "DeepSeek",
            "deepseek-v4-flash",
            "https://api.deepseek.com",
            "https://platform.deepseek.com/api_keys"),
        new(ProviderType.OpenAI, "OpenAI", "OpenAI",
            "gpt-4o-mini",
            "https://api.openai.com/v1",
            "https://platform.openai.com/api-keys"),
        new(ProviderType.Gemini, "Gemini", "Gemini (Google)",
            "gemini-2.5-flash",
            "https://generativelanguage.googleapis.com",
            "https://aistudio.google.com/apikey"),
        new(ProviderType.Claude, "Claude", "Claude (Anthropic)",
            "claude-sonnet-4-5-20250929",
            "https://api.anthropic.com",
            "https://console.anthropic.com/settings/keys"),
        new(ProviderType.Meta, "Meta", "Meta (Muse)",
            "muse-spark-1.3",
            "https://api.meta.ai/v1",
            "https://dev.meta.ai/"),
    };
}
