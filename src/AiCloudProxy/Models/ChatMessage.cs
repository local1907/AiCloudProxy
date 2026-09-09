namespace AiCloudProxy.Models;

public sealed class ChatMessage
{
    public string Role { get; set; }
    public string Content { get; set; }

    /// <summary>For role="tool": the id of the assistant tool call this is the result of.</summary>
    public string? ToolCallId { get; set; }

    /// <summary>For role="tool": the function name that produced this result.</summary>
    public string? Name { get; set; }

    /// <summary>For role="assistant": function calls the model made.</summary>
    public List<ToolCall>? ToolCalls { get; set; }

    public ChatMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}
