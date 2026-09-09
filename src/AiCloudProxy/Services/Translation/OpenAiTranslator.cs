using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCloudProxy.Models;

namespace AiCloudProxy.Services.Translation;

/// <summary>
/// Translates between the OpenAI /v1/chat/completions wire format and the
/// provider-agnostic <see cref="ProviderRequest"/> / token stream used internally.
/// </summary>
public static class OpenAiTranslator
{
    public static ProviderRequest ToProviderRequest(JsonNode body, ProviderConfig cfg)
    {
        var messages = new List<ChatMessage>();
        if (body["messages"] is JsonArray arr)
        {
            foreach (var m in arr)
            {
                if (m is not JsonObject obj) continue;
                var role = JsonHelpers.NormalizeRole(JsonHelpers.GetStringValue(obj["role"]) ?? "user");
                var content = JsonHelpers.ExtractContent(obj["content"]);
                var cm = new ChatMessage(role, content);

                // Tool results carry the id/name of the call they answer.
                if (role == "tool")
                {
                    cm.ToolCallId = JsonHelpers.GetStringValue(obj["tool_call_id"]) ?? "";
                    cm.Name = JsonHelpers.GetStringValue(obj["name"]);
                }

                // Assistant turns that requested function calls.
                if (role == "assistant" && obj["tool_calls"] is JsonArray callsArr)
                {
                    var calls = new List<ToolCall>();
                    foreach (var c in callsArr)
                    {
                        if (c is not JsonObject co) continue;
                        var tc = new ToolCall { Id = JsonHelpers.GetStringValue(co["id"]) ?? "" };
                        if (co["function"] is JsonObject fn)
                        {
                            tc.Name = JsonHelpers.GetStringValue(fn["name"]) ?? "";
                            var argsNode = fn["arguments"];
                            if (argsNode is JsonValue av && JsonHelpers.GetStringValue(argsNode) is { } argStr)
                                tc.Arguments = argStr;
                            else if (argsNode is not null)
                                tc.Arguments = argsNode.ToJsonString();
                        }
                        calls.Add(tc);
                    }
                    cm.ToolCalls = calls;
                }

                messages.Add(cm);
            }
        }

        // Function definitions the client allowed the model to call.
        var tools = new List<FunctionTool>();
        if (body["tools"] is JsonArray toolsArr)
        {
            foreach (var t in toolsArr)
            {
                if (t is not JsonObject to || to["function"] is not JsonObject fn) continue;
                var name = JsonHelpers.GetStringValue(fn["name"]);
                if (string.IsNullOrWhiteSpace(name)) continue;
                tools.Add(new FunctionTool
                {
                    Name = name!,
                    Description = JsonHelpers.GetStringValue(fn["description"]),
                    Parameters = fn["parameters"]?.DeepClone(),
                });
            }
        }

        // Tool selection policy: "auto"/"none"/"required" or a specific function.
        string? toolChoice = null;
        if (body["tool_choice"] is JsonValue tcv && JsonHelpers.GetStringValue(tcv) is { } tcr &&
            tcr is "auto" or "none" or "required")
        {
            toolChoice = tcr;
        }
        else if (body["tool_choice"] is JsonObject tco && tco["function"] is JsonObject tcfn)
        {
            toolChoice = JsonHelpers.GetStringValue(tcfn["name"]) ?? "auto";
        }

        // Preserve the model the client asked for; fall back to the configured
        // default only when the request does not specify one.
        var model = JsonHelpers.GetString(body, "model") ?? "";
        if (string.IsNullOrWhiteSpace(model)) model = cfg.Model;

        var stop = new List<string>();
        if (body["stop"] is JsonArray stopArr)
        {
            foreach (var s in stopArr)
            {
                if (JsonHelpers.GetStringValue(s) is { } str) stop.Add(str);
            }
        }
        else if (JsonHelpers.GetStringValue(body["stop"]) is { } singleStop)
        {
            stop.Add(singleStop);
        }

        return new ProviderRequest
        {
            Model = model,
            Messages = messages,
            Stream = body["stream"]?.GetValue<bool>() ?? false,
            Temperature = JsonHelpers.GetDouble(body, "temperature"),
            MaxTokens = JsonHelpers.GetInt(body, "max_tokens"),
            StopSequences = stop,
            Tools = tools.Count > 0 ? tools : null,
            ToolChoice = toolChoice,
        };
    }

    public static string BuildStreamChunk(string model, string token, bool isFirst, string? finishReason)
    {
        var sb = new StringBuilder();
        sb.Append("data: {\"id\":\"chatcmpl-aicp-").Append(Guid.NewGuid().ToString("N")).Append('"');
        sb.Append(",\"object\":\"chat.completion.chunk\"");
        sb.Append(",\"created\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        sb.Append(",\"model\":").Append(JsonSerializer.Serialize(model));
        sb.Append(",\"choices\":[{\"index\":0,\"delta\":{");
        if (isFirst) sb.Append("\"role\":\"assistant\",\"content\":").Append(JsonSerializer.Serialize(token));
        else sb.Append("\"content\":").Append(JsonSerializer.Serialize(token));
        sb.Append("},\"finish_reason\":").Append(finishReason is null ? "null" : JsonSerializer.Serialize(finishReason));
        sb.Append("}]}\n\n");
        return sb.ToString();
    }

    public static string BuildDoneChunk(string model)
    {
        var sb = new StringBuilder();
        sb.Append("data: {\"id\":\"chatcmpl-aicp-").Append(Guid.NewGuid().ToString("N")).Append('"');
        sb.Append(",\"object\":\"chat.completion.chunk\"");
        sb.Append(",\"created\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        sb.Append(",\"model\":").Append(JsonSerializer.Serialize(model));
        sb.Append(",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    public static string BuildErrorChunk(string error)
    {
        var payload = JsonSerializer.Serialize(new { error = new { message = error, type = "proxy_error" } });
        return $"data: {payload}\n\n";
    }

    public static string BuildFullJson(string model, string content)
    {
        var tokens = EstimateTokens(content);
        var payload = new
        {
            id = "chatcmpl-aicp-" + Guid.NewGuid().ToString("N"),
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = "stop",
                },
            },
            usage = new { prompt_tokens = 0, completion_tokens = tokens, total_tokens = tokens },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Builds a full (non-streaming) chat.completion that returns tool_calls.</summary>
    public static string BuildFullJsonTool(string model, IReadOnlyList<ToolCall> calls)
    {
        var payload = new
        {
            id = "chatcmpl-aicp-" + Guid.NewGuid().ToString("N"),
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = (string?)null,
                        tool_calls = calls.Select((c, i) => ToolCallObj(c, i)).ToArray(),
                    },
                    finish_reason = "tool_calls",
                },
            },
            usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Builds the SSE stream that returns tool_calls (role + tool_calls delta, then finish).</summary>
    public static string BuildToolCallsStream(string model, IReadOnlyList<ToolCall> calls)
    {
        var sb = new StringBuilder();
        sb.Append("data: {\"id\":\"chatcmpl-aicp-").Append(Guid.NewGuid().ToString("N")).Append('"');
        sb.Append(",\"object\":\"chat.completion.chunk\"");
        sb.Append(",\"created\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        sb.Append(",\"model\":").Append(JsonSerializer.Serialize(model));
        sb.Append(",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[");
        for (var i = 0; i < calls.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(ToolCallObj(calls[i], i)));
        }
        sb.Append("]},\"finish_reason\":null}]}\n\n");
        sb.Append("data: {\"id\":\"chatcmpl-aicp-").Append(Guid.NewGuid().ToString("N")).Append('"');
        sb.Append(",\"object\":\"chat.completion.chunk\"");
        sb.Append(",\"created\":").Append(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        sb.Append(",\"model\":").Append(JsonSerializer.Serialize(model));
        sb.Append(",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }

    private static object ToolCallObj(ToolCall c, int index) => new
    {
        index,
        id = string.IsNullOrEmpty(c.Id) ? "call_" + Guid.NewGuid().ToString("N") : c.Id,
        type = "function",
        function = new
        {
            name = c.Name,
            arguments = string.IsNullOrEmpty(c.Arguments) ? "{}" : c.Arguments,
        },
    };

    private static int EstimateTokens(string s) =>
        string.IsNullOrEmpty(s) ? 0 : Math.Max(1, (int)Math.Ceiling(s.Length / 4.0));
}
