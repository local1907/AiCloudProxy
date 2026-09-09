using System.Text;
using System.Text.Json.Nodes;

namespace AiCloudProxy.Services.Translation;

/// <summary>Small helpers for reading values out of incoming JSON bodies.</summary>
internal static class JsonHelpers
{
    public static string? GetString(JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(property, out var v))
            return GetStringValue(v);
        return null;
    }

    public static string? GetStringValue(JsonNode? v)
    {
        if (v is JsonValue jv)
        {
            try { return jv.GetValue<string>(); }
            catch { return null; }
        }
        return null;
    }

    public static double? GetDouble(JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(property, out var v) && v is JsonValue jv)
        {
            try { return jv.GetValue<double>(); }
            catch { }
        }
        return null;
    }

    public static int? GetInt(JsonNode? node, string property)
    {
        if (node is JsonObject obj && obj.TryGetPropertyValue(property, out var v) && v is JsonValue jv)
        {
            try { return jv.GetValue<int>(); }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Extracts plain text from a message "content" field that is either a string
    /// or an array of content parts (OpenAI / newer Ollama style).
    /// </summary>
    public static string ExtractContent(JsonNode? node)
    {
        if (node is null) return "";

        if (node is JsonValue jv)
        {
            try { return jv.GetValue<string>(); }
            catch { return node.ToJsonString(); }
        }

        if (node is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var part in arr)
            {
                if (part is JsonObject obj)
                {
                    var type = GetStringValue(obj["type"]);
                    if (type is null or "text")
                    {
                        var text = GetStringValue(obj["text"]);
                        if (text is not null)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(text);
                        }
                    }
                }
                else
                {
                    sb.Append(part?.ToJsonString());
                }
            }
            return sb.ToString();
        }

        return node.ToJsonString();
    }

    public static string NormalizeRole(string role)
    {
        return role switch
        {
            "model" => "assistant",
            "system" => "system",
            "assistant" => "assistant",
            "tool" => "tool",
            "function" => "tool",
            _ => "user",
        };
    }
}
