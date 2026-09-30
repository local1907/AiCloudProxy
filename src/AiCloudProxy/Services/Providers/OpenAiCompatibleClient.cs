using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCloudProxy.Models;
using AiCloudProxy.Services;
using AiCloudProxy.Services.Translation;

namespace AiCloudProxy.Services.Providers;

/// <summary>
/// Client for any OpenAI-compatible chat completion API (DeepSeek, OpenAI,
/// or a custom base URL supplied by the user).
/// </summary>
public class OpenAiCompatibleClient : IProviderClient
{
    private readonly HttpClient _http;
    private readonly ProviderConfig _cfg;
    private readonly LogService _log;

    public OpenAiCompatibleClient(HttpClient http, ProviderConfig cfg, LogService log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string Model => _cfg.Model;

    private string BaseUrl => string.IsNullOrWhiteSpace(_cfg.BaseUrl)
        ? _cfg.Provider switch
        {
            ProviderType.DeepSeek => "https://api.deepseek.com",
            ProviderType.Meta => "https://api.meta.ai/v1",
            _ => "https://api.openai.com/v1",
        }
        : _cfg.BaseUrl.TrimEnd('/');

    /// <summary>
    /// True for DeepSeek endpoints, which in thinking mode refuse a tool/agent round unless
    /// every assistant turn carries a non-empty reasoning_content.
    /// </summary>
    private bool NeedsReasoningEcho =>
        _cfg.Provider == ProviderType.DeepSeek ||
        BaseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Written when DeepSeek thinking mode needs a reasoning_content value but the original
    /// chain of thought is gone (the proxy was restarted, the cache evicted it, or the turn
    /// predates this session). DeepSeek only checks that the field is present and non-empty,
    /// so a short marker keeps the agent conversation alive instead of failing with HTTP 400
    /// "The reasoning_content in the thinking mode must be passed back to the API".
    /// </summary>
    private const string MissingReasoningPlaceholder = "(reasoning omitted)";

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);

        using var response = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProviderException($"{(int)response.StatusCode} {response.ReasonPhrase} — {Trim(errBody)}");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new ProviderException("Provider returned no model list.");

        var models = new List<string>();
        foreach (var m in data.EnumerateArray())
        {
            if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var name = id.GetString();
                // OpenAI-style lists contain image (dall-e, gpt-image), TTS,
                // whisper and embedding models — keep only chat/text capable ones.
                if (!string.IsNullOrWhiteSpace(name) && ModelFilter.IsTextChatModel(name))
                    models.Add(name);
            }
        }
        return models;
    }

    public async Task<ProviderChatResult> ChatAsync(ProviderRequest request, CancellationToken ct)
    {
        // Tool/agent rounds (request contains tools / tool_choice / tool results /
        // assistant tool_calls) are relayed verbatim: the proxy cannot fabricate or
        // translate function calls itself, so the raw request goes to the provider
        // and its raw response (JSON or SSE) comes back unchanged.
        if (!string.IsNullOrWhiteSpace(request.RawBody))
            return await RelayAsync(request, ct);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["stream"] = request.Stream,
        };
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) payload["max_tokens"] = request.MaxTokens.Value;
        if (request.StopSequences is { Count: > 0 }) payload["stop"] = request.StopSequences;

        var json = JsonSerializer.Serialize(payload);
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);

        var completion = request.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        var response = await _http.SendAsync(msg, completion, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new ProviderException($"{(int)response.StatusCode} {response.ReasonPhrase} — {Trim(errBody)}");
        }

        if (!request.Stream)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var content = ExtractMessageContent(root);
            var model = TryGetString(root, "model") ?? request.Model;
            var finish = ExtractFinishReason(root);
            _log.Info($"Non-stream response received ({content.Length} chars).");
            return new ProviderChatResult { IsStreaming = false, FullContent = content, Model = model, FinishReason = finish };
        }

        async IAsyncEnumerable<string> EnumerateTokens()
        {
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    var data = line.AsSpan(5).Trim();
                    if (data.IsEmpty) continue;
                    if (data.SequenceEqual("[DONE]")) yield break;

                    var delta = ParseDelta(data.ToString());
                    if (!string.IsNullOrEmpty(delta)) yield return delta;
                }
            }
            finally
            {
                response.Dispose();
            }
        }

        return new ProviderChatResult { IsStreaming = true, TokenStream = EnumerateTokens(), Model = request.Model };
    }

    private static string? ParseDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            return ExtractDeltaContent(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractDeltaContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta)) return null;
        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            return c.GetString();
        return null;
    }

    private static string ExtractMessageContent(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return "";
        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var msg)) return "";
        return ExtractContentValue(msg);
    }

    private static string ExtractContentValue(JsonElement msg)
    {
        if (msg.TryGetProperty("content", out var c))
        {
            if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
            if (c.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in c.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object &&
                        part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(t.GetString());
                    }
                }
                return sb.ToString();
            }
        }
        return "";
    }

    private static string? ExtractFinishReason(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        return choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString()
            : null;
    }

    /// <summary>
    /// Forwards a tool/agent request body to the OpenAI-compatible provider
    /// verbatim (only the resolved model name is patched) and relays the raw
    /// response back: full JSON for non-streaming, raw SSE payloads otherwise.
    /// </summary>
    private async Task<ProviderChatResult> RelayAsync(ProviderRequest request, CancellationToken ct)
    {
        var parsed = JsonNode.Parse(request.RawBody!);
        parsed!["model"] = request.Model;
        // DeepSeek (thinking mode) demands the reasoning_content of every previous
        // assistant turn while tools are in play; the client dropped it, so put it back.
        var (restored, filled, unmatched) = ReinjectReasoningContent(parsed, NeedsReasoningEcho);
        if (restored > 0)
            _log.Info($"reasoning echo: restored reasoning_content on {restored} assistant turn(s) from cache.");
        if (filled > 0)
            _log.Info($"reasoning echo: filled missing reasoning_content on {filled} assistant turn(s) with a placeholder.");
        if (unmatched > 0)
            _log.Warn($"reasoning echo: {unmatched} assistant turn(s) still lack reasoning_content.");
        var json = parsed.ToJsonString();

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.ApiKey);

        var completion = request.Stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        var response = await _http.SendAsync(msg, completion, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new ProviderException($"{(int)response.StatusCode} {response.ReasonPhrase} — {Trim(errBody)}");
        }

        if (!request.Stream)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            RecordReasoningFromFull(body);
            return new ProviderChatResult
            {
                IsStreaming = false,
                IsPassthrough = true,
                RawBody = body,
                Model = request.Model,
            };
        }

        async IAsyncEnumerable<string> EnumerateRaw()
        {
            // While relaying we also watch the chunks: if this assistant turn ends
            // with tool_calls, remember its reasoning_content so it can be echoed
            // back on the next round of the same tool/agent conversation.
            var reasoning = new StringBuilder();
            var content = new StringBuilder();
            var toolIds = new HashSet<string>();
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line is null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                    var data = line.AsSpan(5).Trim();
                    if (data.IsEmpty) continue;
                    var payload = data.ToString();
                    if (payload == "[DONE]") yield break;
                    CaptureReasoningDelta(payload, reasoning, content, toolIds);
                    yield return payload;
                }
            }
            finally
            {
                response.Dispose();
                DeepSeekReasoningEcho.Record(toolIds.ToList(), content.ToString(), reasoning.ToString());
            }
        }

        return new ProviderChatResult
        {
            IsStreaming = true,
            IsPassthrough = true,
            RawStream = EnumerateRaw(),
            Model = request.Model,
        };
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// Adds reasoning_content back to assistant messages that are missing it: tool-call
    /// turns are matched by their tool-call ids, tool-less turns by their text. When a turn
    /// cannot be matched and <paramref name="fillMissing"/> is set, a placeholder is written
    /// instead of leaving the field absent, because DeepSeek thinking mode rejects the whole
    /// round with HTTP 400 ("The reasoning_content in the thinking mode must be passed back
    /// to the API"). Historical turns are routinely unmatched — a restarted proxy has no
    /// cache for them and every OpenAI-compatible client strips reasoning_content — so
    /// omitting the field is not an option. Returns how many turns were restored from cache,
    /// how many were filled with the placeholder and how many were left untouched.
    /// </summary>
    private static (int Restored, int Filled, int Unmatched) ReinjectReasoningContent(JsonNode root, bool fillMissing)
    {
        if (root is not JsonObject obj || obj["messages"] is not JsonArray msgs) return (0, 0, 0);

        var toolsInPlay = obj["tools"] is JsonArray { Count: > 0 } || obj["tool_choice"] is not null;
        var restored = 0;
        var filled = 0;
        var unmatched = 0;

        foreach (var m in msgs)
        {
            if (m is not JsonObject msg) continue;
            if (!string.Equals(JsonHelpers.GetStringValue(msg["role"]), "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            // Client already preserved a non-empty reasoning for this turn — leave it
            // alone. An empty string still counts as missing because DeepSeek rejects it.
            if (JsonHelpers.GetStringValue(msg["reasoning_content"]) is { Length: > 0 }) continue;

            if (msg["tool_calls"] is JsonArray { Count: > 0 } calls)
            {
                var ids = new List<string>();
                foreach (var call in calls)
                {
                    if (call is not JsonObject co) continue;
                    var id = JsonHelpers.GetStringValue(co["id"]);
                    if (!string.IsNullOrEmpty(id)) ids.Add(id!);
                }

                var reasoning = DeepSeekReasoningEcho.Lookup(ids);
                if (reasoning is null)
                {
                    if (!fillMissing) { unmatched++; continue; }
                    msg["reasoning_content"] = MissingReasoningPlaceholder;
                    filled++;
                    continue;
                }

                msg["reasoning_content"] = reasoning;
                restored++;
                continue;
            }

            // With tools in the request DeepSeek also requires the reasoning_content of
            // turns where the model called no tool. The client dropped it, so match those
            // turns by the text it echoed back for them.
            var plainReasoning = DeepSeekReasoningEcho.LookupByContent(JsonHelpers.GetStringValue(msg["content"]));
            if (plainReasoning is null)
            {
                if (!fillMissing || !toolsInPlay) continue;
                msg["reasoning_content"] = MissingReasoningPlaceholder;
                filled++;
                continue;
            }

            msg["reasoning_content"] = plainReasoning;
            restored++;
        }

        return (restored, filled, unmatched);
    }

    /// <summary>Records reasoning_content from a non-stream relayed completion body.</summary>
    private static void RecordReasoningFromFull(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
            if (!choices[0].TryGetProperty("message", out var message)) return;

            string? reasoning = null;
            if (message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                reasoning = rc.GetString();

            var ids = new List<string>();
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(id.GetString()))
                    {
                        ids.Add(id.GetString()!);
                    }
                }
            }

            string? content = null;
            if (message.TryGetProperty("content", out var cc) && cc.ValueKind == JsonValueKind.String)
                content = cc.GetString();

            DeepSeekReasoningEcho.Record(ids, content, reasoning ?? "");
        }
        catch (JsonException)
        {
            // Not a completion body we can interpret; nothing to remember.
        }
    }

    /// <summary>Accumulates reasoning_content, assistant text and tool-call ids from an SSE chunk.</summary>
    private static void CaptureReasoningDelta(string payload, StringBuilder reasoning, StringBuilder content, HashSet<string> toolIds)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;
            if (!choices[0].TryGetProperty("delta", out var delta)) return;

            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String &&
                rc.GetString() is { Length: > 0 } text)
            {
                reasoning.Append(text);
            }

            if (delta.TryGetProperty("content", out var cc) && cc.ValueKind == JsonValueKind.String &&
                cc.GetString() is { Length: > 0 } text2)
            {
                content.Append(text2);
            }

            if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(id.GetString()))
                    {
                        toolIds.Add(id.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore a malformed chunk.
        }
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 500 ? s : s[..500] + "…";
    }
}
