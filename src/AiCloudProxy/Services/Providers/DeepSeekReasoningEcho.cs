using System.Collections.Generic;
using System.Linq;

namespace AiCloudProxy.Services.Providers;

/// <summary>
/// Bounded in-process store that lets the OpenAI-compatible relay satisfy the
/// "thinking mode" requirement of reasoning models such as DeepSeek.
///
/// When a reasoning model returns an assistant message that contains BOTH
/// reasoning_content (its chain of thought) and tool_calls, the provider requires
/// that exact reasoning_content to be sent back on every later turn of the same
/// tool/agent conversation. Standard OpenAI-compatible clients (VS Code Copilot
/// BYOM etc.) discard reasoning_content, so the proxy remembers the reasoning text
/// it relayed for each assistant tool-call turn and re-injects it into the matching
/// assistant message on the next request. Without this, DeepSeek rejects the relay
/// with "The `reasoning_content` in the thinking mode must be passed back to the API."
/// </summary>
public static class DeepSeekReasoningEcho
{
    private const int MaxEntries = 512;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> ByTurn = new();
    private static readonly Queue<string> Order = new();

    /// <summary>
    /// Order-independent signature for an assistant turn's tool-call ids. This is
    /// stable across relayed rounds because clients echo back the same tool-call
    /// ids they received, so the proxy can match a later request's assistant
    /// message to the reasoning_content it originally relayed.
    /// </summary>
    public static string Signature(IReadOnlyList<string>? toolCallIds)
    {
        if (toolCallIds is not { Count: > 0 }) return "";
        return string.Join("\u001F",
            toolCallIds.Where(id => !string.IsNullOrEmpty(id)).OrderBy(id => id, System.StringComparer.Ordinal));
    }

    /// <summary>Remembers the reasoning_content for an assistant tool-call turn.</summary>
    public static void Record(string signature, string reasoningContent)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(reasoningContent)) return;

        lock (Gate)
        {
            var isNew = !ByTurn.ContainsKey(signature);
            if (!isNew && ByTurn[signature] == reasoningContent) return;

            ByTurn[signature] = reasoningContent;
            if (!isNew) return; // value refreshed in place; insertion order unchanged

            Order.Enqueue(signature);
            while (Order.Count > MaxEntries)
            {
                ByTurn.Remove(Order.Dequeue());
            }
        }
    }

    /// <summary>Returns the stored reasoning_content for an assistant tool-call turn, if any.</summary>
    public static string? Lookup(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        lock (Gate)
        {
            return ByTurn.TryGetValue(signature, out var reasoning) ? reasoning : null;
        }
    }

    /// <summary>Drops all cached reasoning (e.g. when the proxy is stopped).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            ByTurn.Clear();
            Order.Clear();
        }
    }
}
