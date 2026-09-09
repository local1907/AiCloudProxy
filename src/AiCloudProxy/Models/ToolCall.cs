namespace AiCloudProxy.Models;

/// <summary>A function call the assistant requested (OpenAI-compatible shape, provider-agnostic).</summary>
public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Arguments as a JSON string (OpenAI shape).</summary>
    public string Arguments { get; set; } = "{}";
}
