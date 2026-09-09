namespace AiCloudProxy.Models;

/// <summary>Result of a provider chat completion call. Either a full content
/// string or a stream of content tokens is populated.</summary>
public class ProviderChatResult
{
    public bool IsStreaming { get; set; }
    public string? FullContent { get; set; }
    public IAsyncEnumerable<string>? TokenStream { get; set; }
    public string? FinishReason { get; set; }
    public string? Model { get; set; }

    /// <summary>
    /// Raw relay result for tool/agent rounds on OpenAI-compatible providers:
    /// the provider response is passed through verbatim (JSON body, or the raw
    /// SSE payloads) so function-call definitions, tool_calls and tool results
    /// are never dropped.
    /// </summary>
    public bool IsPassthrough { get; set; }
    public string? RawBody { get; set; }
    public IAsyncEnumerable<string>? RawStream { get; set; }

    /// <summary>
    /// Function calls returned by a non-OpenAI provider (Gemini/Claude) after
    /// tool/agent translation. When present the proxy emits them as OpenAI
    /// tool_calls (JSON or SSE).
    /// </summary>
    public List<ToolCall>? ToolCalls { get; set; }
}
