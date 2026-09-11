using System.Collections.Generic;

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
/// it relayed and re-injects it into the matching assistant message on the next
/// request. Without this, DeepSeek rejects the relay with
/// "The `reasoning_content` in the thinking mode must be passed back to the API."
///
/// The reasoning is keyed per individual tool-call id rather than per whole id set,
/// so the match still holds when a client splits one multi-tool-call assistant turn
/// into several messages or echoes back only a subset of the ids.
/// </summary>
public static class DeepSeekReasoningEcho
{
    private const int MaxIds = 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> ByToolCallId = new();
    private static readonly Queue<string> Order = new();

    /// <summary>Remembers the reasoning_content produced for a set of tool-call ids.</summary>
    public static void Record(IReadOnlyList<string>? toolCallIds, string reasoningContent)
    {
        if (toolCallIds is not { Count: > 0 } || string.IsNullOrEmpty(reasoningContent)) return;

        lock (Gate)
        {
            foreach (var id in toolCallIds)
            {
                if (string.IsNullOrEmpty(id)) continue;

                var isNew = !ByToolCallId.ContainsKey(id);
                ByToolCallId[id] = reasoningContent;
                if (isNew) Order.Enqueue(id);
            }

            while (Order.Count > MaxIds)
            {
                ByToolCallId.Remove(Order.Dequeue());
            }
        }
    }

    /// <summary>
    /// Returns the stored reasoning_content for any of the supplied tool-call ids.
    /// Every id of an assistant tool-call turn maps to the same reasoning, so the
    /// first known id is enough to recover the whole turn.
    /// </summary>
    public static string? Lookup(IReadOnlyList<string>? toolCallIds)
    {
        if (toolCallIds is not { Count: > 0 }) return null;

        lock (Gate)
        {
            foreach (var id in toolCallIds)
            {
                if (!string.IsNullOrEmpty(id) && ByToolCallId.TryGetValue(id, out var reasoning))
                    return reasoning;
            }

            return null;
        }
    }

    /// <summary>Drops all cached reasoning (e.g. when the proxy is stopped).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            ByToolCallId.Clear();
            Order.Clear();
        }
    }
}
