using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCloudProxy.Models;
using AiCloudProxy.Services;
using AiCloudProxy.Services.Translation;

namespace AiCloudProxy.Services.Providers;

/// <summary>Client for the Anthropic Claude Messages API.</summary>
public class ClaudeClient : IProviderClient
{
    private const string DefaultBase = "https://api.anthropic.com";
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly ProviderConfig _cfg;
    private readonly LogService _log;

    public ClaudeClient(HttpClient http, ProviderConfig cfg, LogService log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string Model => _cfg.Model;

    private string BaseUrl => string.IsNullOrWhiteSpace(_cfg.BaseUrl) ? DefaultBase : _cfg.BaseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/models");
        msg.Headers.Add("x-api-key", _cfg.ApiKey);
        msg.Headers.Add("anthropic-version", ApiVersion);

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

        var result = new List<string>();
        foreach (var m in data.EnumerateArray())
        {
            if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var name = id.GetString();
                if (!string.IsNullOrWhiteSpace(name) && ModelFilter.IsTextChatModel(name))
                    result.Add(name);
            }
        }
        return result;
    }

    public async Task<ProviderChatResult> ChatAsync(ProviderRequest request, CancellationToken ct)
    {
        // Tool/agent rounds (function definitions, prior tool_use / tool results) are
        // translated to Anthropic's Messages format and sent non-streaming so tool_use
        // blocks arrive complete and can be parsed reliably; the proxy then re-emits
        // them as OpenAI tool_calls. Plain chat stays streaming.
        var toolRound = request.Tools is { Count: > 0 }
            || request.Messages.Any(m => m.Role == "tool" || m.ToolCalls is { Count: > 0 });
        var wantProviderStream = request.Stream && !toolRound;

        var system = string.Join("\n\n", request.Messages
            .Where(m => m.Role == "system" && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["messages"] = BuildClaudeMessages(request.Messages),
            ["stream"] = wantProviderStream,
        };
        if (!string.IsNullOrEmpty(system)) payload["system"] = system;
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
        if (request.StopSequences is { Count: > 0 }) payload["stop_sequences"] = request.StopSequences;
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(f => new
            {
                name = f.Name,
                description = f.Description ?? "",
                input_schema = SchemaSanitizer.Sanitize(f.Parameters) ??
                               new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            }).ToArray();
            payload["tool_choice"] = BuildToolChoice(request.ToolChoice);
        }

        var json = JsonSerializer.Serialize(payload);
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        msg.Headers.Add("x-api-key", _cfg.ApiKey);
        msg.Headers.Add("anthropic-version", ApiVersion);

        var completion = wantProviderStream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        var response = await _http.SendAsync(msg, completion, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new ProviderException($"{(int)response.StatusCode} {response.ReasonPhrase} — {Trim(errBody)}");
        }

        if (!wantProviderStream)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            using var doc = JsonDocument.Parse(body);
            var (text, calls) = ExtractResponse(doc.RootElement);
            var model = TryGetString(doc.RootElement, "model") ?? request.Model;
            _log.Info($"Non-stream response received ({text.Length} chars, {calls.Count} tool call(s)).");
            return new ProviderChatResult
            {
                IsStreaming = false,
                FullContent = string.IsNullOrEmpty(text) ? null : text,
                ToolCalls = calls.Count > 0 ? calls : null,
                Model = model,
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

                    var text = ParseClaudeDelta(data.ToString());
                    if (!string.IsNullOrEmpty(text)) yield return text;
                }
            }
            finally
            {
                response.Dispose();
            }
        }

        return new ProviderChatResult { IsStreaming = true, TokenStream = EnumerateTokens(), Model = request.Model };
    }

    /// <summary>
    /// Converts the provider-agnostic conversation into Anthropic "messages": system
    /// roles go to the top-level system field, function results become user messages
    /// with tool_result blocks, assistant tool calls become tool_use blocks. Adjacent
    /// same-role turns are merged because Claude requires strict user/assistant
    /// alternation.
    /// </summary>
    private static List<object> BuildClaudeMessages(IEnumerable<ChatMessage> incoming)
    {
        var staged = new List<(string Role, List<object> Blocks)>();
        foreach (var m in incoming)
        {
            if (m.Role == "system") continue;

            var role = m.Role == "assistant" ? "assistant" : "user";
            var blocks = new List<object>();

            if (m.Role == "tool")
            {
                var id = string.IsNullOrEmpty(m.ToolCallId) ? (m.Name ?? "tool") : m.ToolCallId!;
                blocks.Add(new { type = "tool_result", tool_use_id = id, content = m.Content ?? "" });
            }
            else if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            {
                if (!string.IsNullOrEmpty(m.Content))
                    blocks.Add(new { type = "text", text = m.Content });
                foreach (var c in m.ToolCalls)
                {
                    blocks.Add(new
                    {
                        type = "tool_use",
                        id = string.IsNullOrEmpty(c.Id) ? "call_" + Guid.NewGuid().ToString("N") : c.Id,
                        name = c.Name,
                        input = ParseJsonNode(c.Arguments) ?? new JsonObject(),
                    });
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(m.Content))
                    blocks.Add(new { type = "text", text = m.Content });
            }

            if (blocks.Count == 0) continue;

            if (staged.Count > 0 && staged[^1].Role == role)
            {
                staged[^1].Blocks.AddRange(blocks);
            }
            else
            {
                staged.Add((role, blocks));
            }
        }

        return staged
            .Select(s => (object)new { role = s.Role, content = s.Blocks.ToArray() })
            .ToList();
    }

    private static object BuildToolChoice(string? choice)
    {
        return choice switch
        {
            "none" => new { type = "none" },
            "required" => new { type = "any" },
            not null when !string.IsNullOrEmpty(choice) &&
                           !string.Equals(choice, "auto", StringComparison.OrdinalIgnoreCase)
                => new { type = "tool", name = choice },
            _ => new { type = "auto" },
        };
    }

    private static JsonNode? ParseJsonNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Extracts assistant text and any tool_use blocks from a Claude non-stream body.
    /// </summary>
    private static (string Text, List<ToolCall> Calls) ExtractResponse(JsonElement root)
    {
        var sb = new StringBuilder();
        var calls = new List<ToolCall>();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                var type = TryGetString(block, "type");
                if (type == "text" && TryGetString(block, "text") is { } t)
                {
                    sb.Append(t);
                }
                else if (type == "tool_use")
                {
                    var id = TryGetString(block, "id") ?? "call_" + Guid.NewGuid().ToString("N");
                    var name = TryGetString(block, "name") ?? "";
                    var args = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                    calls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                }
            }
        }
        return (sb.ToString(), calls);
    }

    private static string? ParseClaudeDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (TryGetString(root, "type") != "content_block_delta") return null;
            if (!root.TryGetProperty("delta", out var delta)) return null;
            if (TryGetString(delta, "type") != "text_delta") return null;
            return TryGetString(delta, "text");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        return el.ValueKind == JsonValueKind.Object &&
               el.TryGetProperty(name, out var v) &&
               v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 500 ? s : s[..500] + "…";
    }
}
