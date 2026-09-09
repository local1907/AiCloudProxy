namespace AiCloudProxy.Models;

/// <summary>Provider-agnostic chat completion request.</summary>
public class ProviderRequest
{
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = new();
    public bool Stream { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    public List<string> StopSequences { get; set; } = new();

    /// <summary>Function definitions the client allowed the model to call.</summary>
    public List<FunctionTool>? Tools { get; set; }

    /// <summary>Tool selection: "auto", "none", "required", or a function name.</summary>
    public string? ToolChoice { get; set; }

    /// <summary>
    /// When set, the whole incoming OpenAI body is relayed to the provider
    /// verbatim (only the model is patched). Used for tool/agent rounds on
    /// OpenAI-compatible providers so tools, tool_calls and tool results
    /// survive the proxy unchanged.
    /// </summary>
    public string? RawBody { get; set; }
}
