namespace AiCloudProxy.Services;

/// <summary>
/// Shared filter that removes models which are not usable for text/code chat —
/// i.e. image, audio/video, TTS, embedding, moderation and experimental-only
/// models — from the lists returned by the providers.
/// </summary>
public static class ModelFilter
{
    /// <summary>Model IDs containing any of these tokens are not text/chat capable.</summary>
    private static readonly string[] BannedKeywords =
    {
        // Embeddings / non-chat OpenAI & Co models
        "embedding", "embed", "dall-e", "dalle", "whisper", "tts", "speech",
        "realtime", "audio", "moderation", "transcri",
        // Gemini image / video / music / audio generators
        "imagen", "veo", "pali", "aqa", "nano-banana", "banana", "image",
        "music", "lyria", "sound", "jewels", "search", "tuning", "gemma-2b",
        // Gemini special-purpose / non-multiturn-chat previews. These models can
        // generate content but reject normal chat/agent conversations with
        // "Multiturn chat is not enabled for models/..." (e.g. antigravity,
        // computer-use, robotics) so they must not be advertised for Copilot BYOM.
        "antigravity", "computer-use", "robotics",
        // Explicitly non-text entries
        "vision-only",
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="modelId"/> looks like a text/code
    /// chat model. When provider capability metadata is available (e.g. Gemini's
    /// <c>supportedGenerationMethods</c>) it is used as a stricter check.
    /// </summary>
    public static bool IsTextChatModel(string modelId, IEnumerable<string>? supportedMethods = null)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        // 1) Name / ID blacklist — catches image, audio, embedding, TTS, ...
        if (BannedKeywords.Any(kw => modelId.Contains(kw, StringComparison.OrdinalIgnoreCase)))
            return false;

        // 2) Provider capability metadata (when available). A model that cannot
        //    generate content or chat (e.g. embedding-only) is filtered out.
        if (supportedMethods is not null && supportedMethods.Any())
        {
            var canGenerate = supportedMethods.Any(m =>
                m.Contains("generateContent", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("chat", StringComparison.OrdinalIgnoreCase));
            if (!canGenerate)
                return false;
        }

        return true;
    }
}
