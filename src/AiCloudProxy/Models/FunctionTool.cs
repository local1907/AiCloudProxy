using System.Text.Json.Nodes;

namespace AiCloudProxy.Models;

/// <summary>A function the model may call (OpenAI-compatible shape, provider-agnostic).</summary>
public sealed class FunctionTool
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonNode? Parameters { get; set; }
}
