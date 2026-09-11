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
        ? (_cfg.Provider == ProviderType.DeepSeek ? "https://api.deepseek.com" : "https://api.openai.com/v1")
        : _cfg.BaseUrl.TrimEnd('/');

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
        // assistant tool-call turn; the client dropped it, so put it back.
        var (restored, unresolved) = ReinjectReasoningContent(parsed);
        if (unresolved > 0)
        {
            _log.Warn($"reasoning echo: restored {restored} of {restored + unresolved} assistant tool-call turn(s); " +
                      $"{unresolved} could not be matched to cached reasoning.");
        }
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
                    CaptureReasoningDelta(payload, reasoning, toolIds);
                    yield return payload;
                }
            }
            finally
            {
                response.Dispose();
                DeepSeekReasoningEcho.Record(toolIds.ToList(), reasoning.ToString());
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
    /// Adds reasoning_content back to assistant tool-call messages that are missing
    /// it but whose tool-call ids match a previously relayed reasoning turn.
    /// </summary>
    private static (int Restored, int Unresolved) ReinjectReasoningContent(JsonNode root)
    {
        if (root is not JsonObject obj || obj["messages"] is not JsonArray msgs) return (0, 0);

        var restored = 0;
        var unresolved = 0;

        foreach (var m in msgs)
        {
            if (m is not JsonObject msg) continue;
            if (!string.Equals(JsonHelpers.GetStringValue(msg["role"]), "assistant", StringComparison.OrdinalIgnoreCase))
                continue;
            if (msg["tool_calls"] is not JsonArray calls || calls.Count == 0) continue;
            // Client already preserved a non-empty reasoning for this turn — leave it
            // alone. An empty string still counts as missing because DeepSeek rejects it.
            if (JsonHelpers.GetStringValue(msg["reasoning_content"]) is { Length: > 0 }) continue;

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
                unresolved++;
                continue;
            }

            msg["reasoning_content"] = reasoning;
            restored++;
        }

        return (restored, unresolved);
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

            DeepSeekReasoningEcho.Record(ids, reasoning ?? "");
        }
        catch (JsonException)
        {
            // Not a completion body we can interpret; nothing to remember.
        }
    }

    /// <summary>Accumulates reasoning_content text and tool-call ids from an SSE chunk.</summary>
    private static void CaptureReasoningDelta(string payload, StringBuilder reasoning, HashSet<string> toolIds)
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
