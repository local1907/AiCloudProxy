using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCloudProxy.Models;

namespace AiCloudProxy.Services.Translation;

/// <summary>
/// Translates between the Ollama request/response wire format and the
/// provider-agnostic <see cref="ProviderRequest"/> / token stream used internally.
/// </summary>
public static class OllamaTranslator
{
    public static ProviderRequest ToProviderRequest(JsonNode body, ProviderConfig cfg, bool isGenerate)
    {
        var messages = new List<ChatMessage>();

        if (isGenerate)
        {
            // /api/generate uses a bare prompt; wrap it as a single user message.
            var prompt = JsonHelpers.GetString(body, "prompt") ?? "";
            messages.Add(new ChatMessage("user", prompt));
        }
        else
        {
            // /api/chat uses an OpenAI-style messages array.
            if (body["messages"] is JsonArray arr)
            {
                foreach (var m in arr)
                {
                    if (m is not JsonObject obj) continue;
                    var role = JsonHelpers.NormalizeRole(JsonHelpers.GetStringValue(obj["role"]) ?? "user");
                    var content = JsonHelpers.ExtractContent(obj["content"]);
                    messages.Add(new ChatMessage(role, content));
                }
            }
        }

        double? temperature = null;
        int? maxTokens = null;
        if (body["options"] is JsonObject opts)
        {
            temperature = JsonHelpers.GetDouble(opts, "temperature");
            maxTokens = JsonHelpers.GetInt(opts, "num_predict");
        }

        // Preserve the model the client asked for; fall back to the configured
        // default only when the request does not specify one.
        var model = JsonHelpers.GetString(body, "model") ?? "";
        if (string.IsNullOrWhiteSpace(model)) model = cfg.Model;

        return new ProviderRequest
        {
            Model = model,
            Messages = messages,
            Stream = body["stream"]?.GetValue<bool>() ?? false,
            Temperature = temperature,
            MaxTokens = maxTokens,
        };
    }

    public static string BuildStreamLine(string model, string token, bool isGenerate)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":").Append(JsonSerializer.Serialize(model));
        sb.Append(",\"created_at\":").Append(JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("o")));
        if (isGenerate)
        {
            sb.Append(",\"response\":").Append(JsonSerializer.Serialize(token));
        }
        else
        {
            sb.Append(",\"message\":{\"role\":\"assistant\",\"content\":")
              .Append(JsonSerializer.Serialize(token))
              .Append('}');
        }
        sb.Append(",\"done\":false}");
        return sb.ToString();
    }

    public static string BuildDoneLine(string model, bool isGenerate)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":").Append(JsonSerializer.Serialize(model));
        sb.Append(",\"created_at\":").Append(JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("o")));
        if (isGenerate) sb.Append(",\"response\":\"\"");
        else sb.Append(",\"message\":{\"role\":\"assistant\",\"content\":\"\"}");
        sb.Append(",\"done\":true,\"done_reason\":\"stop\",\"eval_count\":0}");
        return sb.ToString();
    }

    public static string BuildFullJson(string model, string content, bool isGenerate)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":").Append(JsonSerializer.Serialize(model));
        sb.Append(",\"created_at\":").Append(JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("o")));
        if (isGenerate)
        {
            sb.Append(",\"response\":").Append(JsonSerializer.Serialize(content));
        }
        else
        {
            sb.Append(",\"message\":{\"role\":\"assistant\",\"content\":")
              .Append(JsonSerializer.Serialize(content))
              .Append('}');
        }
        sb.Append(",\"done\":true,\"done_reason\":\"stop\",\"eval_count\":")
          .Append(EstimateTokens(content))
          .Append('}');
        return sb.ToString();
    }

    private static int EstimateTokens(string s) =>
        string.IsNullOrEmpty(s) ? 0 : Math.Max(1, (int)Math.Ceiling(s.Length / 4.0));
}
