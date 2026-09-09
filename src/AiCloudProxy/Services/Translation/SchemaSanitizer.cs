using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace AiCloudProxy.Services.Translation;

/// <summary>
/// Recursively reduces an OpenAI tool's JSON-Schema "parameters" to the subset of
/// keywords target providers accept. Gemini rejects any unknown keyword with HTTP
/// 400 ("Unknown name ... Cannot find field"); Claude is more lenient but also
/// accepts this reduced form. Keeps only: type, format, description, nullable,
/// enum, default, items, properties, required. JSON-Schema union types such as
/// ["string","null"] are collapsed to a single type + nullable:true because
/// Gemini's proto `type` field is singular.
/// </summary>
public static class SchemaSanitizer
{
    private static readonly HashSet<string> AllowedKeys = new(System.StringComparer.Ordinal)
    {
        "type", "format", "description", "nullable", "enum", "default",
        "items", "properties", "required",
    };

    public static JsonNode? Sanitize(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return schema?.DeepClone();

        var clean = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (!AllowedKeys.Contains(key)) continue; // drop unsupported / provider-only keys
            if (value is null) { clean[key] = null; continue; }

            switch (key)
            {
                case "properties" when value is JsonObject props:
                    var cleanedProps = new JsonObject();
                    foreach (var (name, sub) in props)
                    {
                        cleanedProps[name] = Sanitize(sub) ?? new JsonObject();
                    }
                    clean["properties"] = cleanedProps;
                    break;
                case "items":
                    clean["items"] = Sanitize(value) ?? new JsonObject();
                    break;
                // JSON Schema union form like ["string","null"]: some providers have a
                // singular type field, so collapse to one type (+ nullable).
                case "type" when value is JsonArray types:
                    string? primary = null;
                    var nullable = false;
                    foreach (var t in types)
                    {
                        var s = JsonHelpers.GetStringValue(t);
                        if (s is null) continue;
                        if (s == "null") nullable = true;
                        else if (primary is null) primary = s;
                    }
                    if (primary is not null)
                    {
                        clean["type"] = primary;
                        if (nullable) clean["nullable"] = true;
                    }
                    break;
                case "enum" or "required" when value is JsonArray arr:
                    clean[key] = arr.DeepClone();
                    break;
                default:
                    clean[key] = value.DeepClone();
                    break;
            }
        }

        return clean;
    }
}
