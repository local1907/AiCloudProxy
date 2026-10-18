namespace AiCloudProxy.Services;

/// <summary>
/// Shared filter that removes models the user does not want advertised — by default
/// image/audio/video/TTS/embedding and experimental-only models. The keyword list is
/// editable from the app's Options tab; see <see cref="SetBannedKeywords"/>.
/// </summary>
public static class ModelFilter
{
    /// <summary>
    /// Factory default keyword list, used to seed the Options tab the first time.
    /// Model IDs containing any of these tokens are hidden from the provider lists.
    /// </summary>
    public static readonly string[] DefaultBannedKeywords =
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

    private static string[] _bannedKeywords = DefaultBannedKeywords;

    /// <summary>
    /// The keyword list currently in effect. Empty means nothing is filtered by name.
    /// </summary>
    public static IReadOnlyList<string> BannedKeywords => _bannedKeywords;

    /// <summary>
    /// Replaces the active keyword list (driven by the Options tab / saved settings).
    /// A null or empty sequence disables name-based filtering entirely.
    /// </summary>
    public static void SetBannedKeywords(IEnumerable<string>? keywords) =>
        _bannedKeywords = Normalize(keywords);

    /// <summary>
    /// Parses user-entered keywords: one per line, commas and semicolons also separate.
    /// Blank entries and case-insensitive duplicates are dropped.
    /// </summary>
    public static string[] ParseKeywords(string? text) =>
        string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : Normalize(text.Split(new[] { '\r', '\n', ',', ';' },
                                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string[] Normalize(IEnumerable<string>? keywords)
    {
        if (keywords is null) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var keyword in keywords)
        {
            var trimmed = keyword?.Trim() ?? "";
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
        }
        return result.ToArray();
    }

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
        if (_bannedKeywords.Any(kw => modelId.Contains(kw, StringComparison.OrdinalIgnoreCase)))
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
