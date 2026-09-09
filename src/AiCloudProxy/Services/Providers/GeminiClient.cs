using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using AiCloudProxy.Services.Translation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCloudProxy.Models;
using AiCloudProxy.Services;

namespace AiCloudProxy.Services.Providers;

/// <summary>Client for Google Gemini's generateContent / streamGenerateContent API.</summary>
public class GeminiClient : IProviderClient
{
    private const string DefaultBase = "https://generativelanguage.googleapis.com";

    private readonly HttpClient _http;
    private readonly ProviderConfig _cfg;
    private readonly LogService _log;

    public GeminiClient(HttpClient http, ProviderConfig cfg, LogService log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string Model => _cfg.Model;

    private string BaseUrl => string.IsNullOrWhiteSpace(_cfg.BaseUrl) ? DefaultBase : _cfg.BaseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var url = $"{BaseUrl}/v1beta/models?key={Uri.EscapeDataString(_cfg.ApiKey)}";
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_cfg.ApiKey)) msg.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _cfg.ApiKey);

        using var response = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new ProviderException(DescribeError((int)response.StatusCode, errBody));
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new ProviderException("Provider returned no model list.");

        var result = new List<string>();
        foreach (var m in models.EnumerateArray())
        {
            if (!m.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String) continue;
            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            // Names come back as "models/gemini-2.5-flash" — strip the prefix.
            var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;

            // Gemini reports the methods a model supports (e.g. generateContent,
            // embedContent, countTokens). Use them to drop embedding-only models.
            List<string>? methods = null;
            if (m.TryGetProperty("supportedGenerationMethods", out var methodsProp) &&
                methodsProp.ValueKind == JsonValueKind.Array)
            {
                methods = methodsProp.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToList();
            }

            if (!ModelFilter.IsTextChatModel(id, methods)) continue;
            result.Add(id);
        }
        return result;
    }

    public async Task<ProviderChatResult> ChatAsync(ProviderRequest request, CancellationToken ct)
    {
        // A tool/agent round has function definitions, tool results, or prior
        // assistant tool_calls. These are translated to Gemini contents and sent
        // non-streaming so a functionCall arrives complete and can be parsed
        // reliably; the proxy then re-emits it as OpenAI tool_calls.
        var toolRound = request.Tools is { Count: > 0 }
            || request.Messages.Any(m => m.Role == "tool" || m.ToolCalls is { Count: > 0 });

        var contents = new List<object>();
        var system = new StringBuilder();

        foreach (var m in request.Messages)
        {
            if (m.Role == "system")
            {
                if (system.Length > 0) system.Append('\n');
                system.Append(m.Content);
                continue;
            }

            var (role, parts) = ToGeminiParts(m);
            contents.Add(new { role, parts });
        }

        var payload = new Dictionary<string, object?>();
        if (contents.Count > 0) payload["contents"] = contents;
        if (system.Length > 0) payload["systemInstruction"] = new { parts = new[] { new { text = system.ToString() } } };

        if (request.Tools is { Count: > 0 })
        {
            var decls = request.Tools.Select(f => new
            {
                name = f.Name,
                description = f.Description ?? "",
                parameters = SchemaSanitizer.Sanitize(f.Parameters) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            }).ToArray();
            payload["tools"] = new[] { new { functionDeclarations = decls } };
        }

        var generation = new Dictionary<string, object?>();
        if (request.Temperature.HasValue) generation["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) generation["maxOutputTokens"] = request.MaxTokens.Value;
        if (generation.Count > 0) payload["generationConfig"] = generation;

        var json = JsonSerializer.Serialize(payload);
        var wantProviderStream = request.Stream && !toolRound;
        var action = wantProviderStream ? "streamGenerateContent?alt=sse" : "generateContent";
        // Use the model actually requested (a route can advertise several models);
        // fall back to the configured default only when the request had none.
        var model = string.IsNullOrWhiteSpace(request.Model) ? _cfg.Model : request.Model;
        // The stream action already carries a query string ('?alt=sse') so its key is
        // appended with '&'; the non-stream action has none, so it needs '?'. Before
        // this fix the key leaked into the path and Gemini answered tool/agent rounds
        // (which are forced non-stream here) with HTTP 404.
        var sep = wantProviderStream ? "&" : "?";
        var url = $"{BaseUrl}/v1beta/models/{Uri.EscapeDataString(model)}:{action}{sep}key={Uri.EscapeDataString(_cfg.ApiKey)}";

        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_cfg.ApiKey)) msg.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _cfg.ApiKey);

        var completion = wantProviderStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        var response = await _http.SendAsync(msg, completion, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new ProviderException(DescribeError((int)response.StatusCode, errBody));
        }

        if (!wantProviderStream)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            using var doc = JsonDocument.Parse(body);
            var (text, calls) = ExtractResponse(doc.RootElement);
            _log.Info($"Non-stream response received ({text.Length} chars, {calls.Count} tool call(s)).");
            return new ProviderChatResult
            {
                IsStreaming = false,
                FullContent = string.IsNullOrEmpty(text) ? null : text,
                ToolCalls = calls.Count > 0 ? calls : null,
                Model = request.Model,
            };
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

                    var part = ParsePart(data.ToString());
                    if (!string.IsNullOrEmpty(part)) yield return part;
                }
            }
            finally
            {
                response.Dispose();
            }
        }

        return new ProviderChatResult { IsStreaming = true, TokenStream = EnumerateTokens(), Model = request.Model };
    }

    /// <summary>Converts a provider-agnostic message into a Gemini content part.</summary>
    private static (string Role, object Parts) ToGeminiParts(ChatMessage m)
    {
        if (m.Role == "tool")
        {
            // A tool result becomes a functionResponse part (Gemini wants a JSON
            // object; parse it when the tool returned JSON, otherwise wrap it).
            var name = string.IsNullOrEmpty(m.Name) ? (m.ToolCallId ?? "tool") : m.Name!;
            return ("function", new[] { new { functionResponse = new { name, response = ContentToObject(m.Content) } } });
        }

        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
        {
            var parts = new List<object>();
            if (!string.IsNullOrEmpty(m.Content)) parts.Add(new { text = m.Content });
            foreach (var c in m.ToolCalls!)
            {
                parts.Add(new { functionCall = new { name = c.Name, args = ParseJsonNode(c.Arguments) } });
            }
            return ("model", parts.ToArray());
        }

        return (m.Role == "assistant" ? "model" : "user", new[] { new { text = m.Content } });
    }

    private static object ContentToObject(string content)
    {
        if (!string.IsNullOrEmpty(content) && ParseJsonNode(content) is { } node)
            return node;
        return new Dictionary<string, object?> { ["result"] = content ?? "" };
    }

    private static JsonNode? ParseJsonNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Extracts assistant text and any functionCall parts from a Gemini response.</summary>
    private static (string Text, List<ToolCall> Calls) ExtractResponse(JsonElement root)
    {
        var sb = new StringBuilder();
        var calls = new List<ToolCall>();
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
        {
            var candidate = candidates[0];
            if (candidate.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.ValueKind != JsonValueKind.Object) continue;
                    if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        sb.Append(t.GetString());
                    }
                    else if (part.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
                    {
                        var name = fc.TryGetProperty("name", out var fn) && fn.ValueKind == JsonValueKind.String
                            ? fn.GetString()
                            : "";
                        var args = fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                        calls.Add(new ToolCall
                        {
                            Id = "call_" + Guid.NewGuid().ToString("N"),
                            Name = name ?? "",
                            Arguments = args,
                        });
                    }
                }
            }
        }
        return (sb.ToString(), calls);
    }

    private static string? ParsePart(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var text = ExtractParts(doc.RootElement);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractParts(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) return "";
        var candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out var content)) return "";
        if (!content.TryGetProperty("parts", out var parts)) return "";

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                sb.Append(t.GetString());
            }
        }
        return sb.ToString();
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 500 ? s : s[..500] + "…";
    }

    /// <summary>
    /// Turns an upstream error into a message with clear English hints: Google's 2026 key
    /// policy (old 'Standard'/unrestricted keys are rejected with 401/403; new keys are
    /// 'auth' keys) and the free-tier quota (429 "free_tier" metric) that agent loops hit.
    /// The same message is both logged and passed through to the client (status 429 keeps
    /// the proxy's Retry-After behaviour).
    /// </summary>
    private static string DescribeError(int status, string body)
    {
        if (status is 401 or 403)
        {
            return $"{status} — {Trim(body)}" +
                   " Google now rejects old 'Standard'/unrestricted API keys — create a fresh key at " +
                   "https://aistudio.google.com/apikey (new keys are 'auth' keys) and try again.";
        }

        if (status == 429 && body.Contains("free_tier", StringComparison.OrdinalIgnoreCase))
        {
            return "429 — Google Gemini free-tier rate limit reached (gemini-2.5-flash allows ~5 requests per minute). " +
                   "Wait about a minute and retry, or in AI Cloud Proxy settings switch to a higher-quota model " +
                   "(e.g. gemini-2.5-flash-lite) or enable billing for Gemini.";
        }

        return $"{status} — {Trim(body)}";
    }
}
