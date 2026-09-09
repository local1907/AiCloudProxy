using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using AiCloudProxy.Models;
using AiCloudProxy.Services.Providers;
using AiCloudProxy.Services.Translation;

namespace AiCloudProxy.Services;

/// <summary>
/// Lightweight in-process HTTP listener exposing Ollama-compatible endpoints
/// (plus an OpenAI-compatible one) that forward to one or more configured
/// cloud providers, routing each request by model name.
/// </summary>
public class ProxyServer
{
    private readonly LogService _log;
    private readonly ProviderFactory _factory;
    private WebApplication? _app;
    private bool _isRunning;
    private readonly List<ProviderRoute> _routes = new();
    private string _defaultKey = "";

    public bool IsRunning => _isRunning;
    public int Port { get; private set; }

    public ProxyServer(LogService log, ProviderFactory factory)
    {
        _log = log;
        _factory = factory;
    }

    /// <summary>A single active provider route; requests resolve to one route by model name.</summary>
    private sealed class ProviderRoute
    {
        public required string Key { get; init; }
        public required ProviderConfig Config { get; init; }
        public string DefaultModel { get; init; } = "";
        public string[] Models { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Starts the proxy with one or more active providers. Every provider that has a
    /// configured API key becomes a route; requests are routed by the requested model
    /// name (qualified "model@Provider" names route unambiguously).
    /// </summary>
    public async Task StartAsync(
        IReadOnlyList<ProviderRouteConfig> routeConfigs,
        string defaultKey,
        int port,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? knownModelsByProvider = null)
    {
        await StopAsync();

        if (routeConfigs.Count == 0)
            throw new InvalidOperationException("No provider is configured with an API key.");

        _routes.Clear();
        _defaultKey = defaultKey;

        foreach (var rc in routeConfigs)
        {
            IReadOnlyList<string>? models = null;
            knownModelsByProvider?.TryGetValue(rc.Key, out models);
            if (models is not { Count: > 0 })
                models = await TryFetchModelsAsync(rc.Config);

            _routes.Add(new ProviderRoute
            {
                Key = rc.Key,
                Config = rc.Config,
                DefaultModel = rc.Config.Model ?? "",
                Models = models is { Count: > 0 } ? models.ToArray() : Array.Empty<string>(),
            });
            _log.Info($"Route '{rc.Key}' ready — {_routes[^1].Models.Length} model(s) advertised.");
        }

        if (!_routes.Any(r => string.Equals(r.Key, _defaultKey, StringComparison.OrdinalIgnoreCase)))
            _defaultKey = _routes[0].Key;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        var app = builder.Build();

        app.MapPost("/api/chat", async (HttpContext ctx, CancellationToken ct) =>
            await HandleOllamaAsync(ctx, isGenerate: false, ct));

        app.MapPost("/api/generate", async (HttpContext ctx, CancellationToken ct) =>
            await HandleOllamaAsync(ctx, isGenerate: true, ct));

        app.MapPost("/v1/chat/completions", async (HttpContext ctx, CancellationToken ct) =>
            await HandleOpenAiAsync(ctx, ct));

        app.MapGet("/api/tags", (HttpContext ctx) => HandleTags(ctx));
        app.MapGet("/api/show", async (HttpContext ctx, CancellationToken ct) => await HandleShowAsync(ctx, ct));
        app.MapPost("/api/show", async (HttpContext ctx, CancellationToken ct) => await HandleShowAsync(ctx, ct));
        app.MapGet("/v1/models", (HttpContext ctx) => HandleOpenAiModels(ctx));
        app.MapGet("/api/version", () => Results.Json(new { version = "0.1.0" }));
        app.MapGet("/", () => Results.Json(new
        {
            name = "AI Cloud Proxy",
            status = "ok",
            endpoints = new[] { "/api/chat", "/api/generate", "/api/show", "/v1/chat/completions", "/api/tags", "/v1/models" },
        }));

        _app = app;
        Port = port;
        try
        {
            await app.StartAsync();
            _isRunning = true;
            _log.Info($"Listening on http://127.0.0.1:{port}");
        }
        catch (Exception ex)
        {
            _app = null;
            Port = 0;
            try { await app.DisposeAsync(); } catch { }
            _log.Error($"Failed to bind port {port}. It may already be in use by another process.", ex);
            throw;
        }
    }

    /// <summary>Convenience overload for a single active provider (used by tests).</summary>
    public async Task StartAsync(ProviderConfig cfg, int port, IReadOnlyList<string>? knownModels = null)
    {
        var routes = new[] { new ProviderRouteConfig { Key = cfg.Provider.ToString(), Config = cfg } };
        IReadOnlyDictionary<string, IReadOnlyList<string>>? known = null;
        if (knownModels is { Count: > 0 })
            known = new Dictionary<string, IReadOnlyList<string>> { [cfg.Provider.ToString()] = knownModels };
        await StartAsync(routes, cfg.Provider.ToString(), port, known);
    }

    public async Task StopAsync()
    {
        var app = _app;
        if (app is null) return;
        _app = null;
        _isRunning = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(timeout.Token);
        }
        catch { }
        await app.DisposeAsync();
        _log.Info("Proxy stopped.");
    }

    /// <summary>Best-effort fetch of the provider's live model list; never throws.</summary>
    private async Task<string[]?> TryFetchModelsAsync(ProviderConfig cfg)
    {
        try
        {
            var client = _factory.Create(cfg);
            var models = await client.ListModelsAsync(CancellationToken.None);
            if (models.Count == 0) return null;
            _log.Info($"Discovered {models.Count} model(s) from the provider: {string.Join(", ", models)}");
            return models.ToArray();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not fetch the provider model list ({ex.Message}). Advertising the configured model only.");
            return null;
        }
    }

    /// <summary>
    /// Multi-provider resolution: qualified "model@Provider" names route unambiguously;
    /// plain names route to the provider that owns them; unknown names fall back to the
    /// default provider's default model (handles Ollama's arbitrary names like "llama3").
    /// </summary>
    private (ProviderRoute Route, string Model) Resolve(string requested)
    {
        var normalized = NormalizeModelName(requested);
        var defaultRoute = GetDefaultRoute();

        if (string.IsNullOrWhiteSpace(normalized))
            return (defaultRoute, defaultRoute.DefaultModel);

        // Qualified form "model@ProviderKey".
        var at = normalized.LastIndexOf('@');
        if (at > 0)
        {
            var modelPart = normalized[..at];
            var keyPart = normalized[(at + 1)..];
            var byKey = _routes.FirstOrDefault(r => string.Equals(r.Key, keyPart, StringComparison.OrdinalIgnoreCase));
            if (byKey is not null && !string.IsNullOrWhiteSpace(modelPart))
                return (byKey, modelPart);
        }

        // Plain name: find the provider(s) that own it.
        ProviderRoute? match = null;
        var matches = 0;
        foreach (var r in _routes)
        {
            if (r.Models.Any(m => string.Equals(m, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                match = r;
                matches++;
            }
        }
        if (matches == 1 && match is not null) return (match, normalized);
        if (matches > 1) return (defaultRoute, normalized); // ambiguous: keep the name, prefer the default provider

        // Unknown name: use the default provider's default model.
        return (defaultRoute, string.IsNullOrWhiteSpace(defaultRoute.DefaultModel) ? normalized : defaultRoute.DefaultModel);
    }

    private ProviderRoute GetDefaultRoute() =>
        _routes.FirstOrDefault(r => string.Equals(r.Key, _defaultKey, StringComparison.OrdinalIgnoreCase))
        ?? _routes.FirstOrDefault()
        ?? throw new InvalidOperationException("No provider routes configured.");

    private ProviderConfig DefaultConfig => GetDefaultRoute().Config;

    private static string NormalizeModelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        const string latest = ":latest";
        var trimmed = name.Trim();
        return trimmed.EndsWith(latest, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^latest.Length]
            : trimmed;
    }

    /// <summary>
    /// Static per-provider profile used to advertise capabilities/token limits to
    /// Ollama clients (VS 2026 Copilot BYOM, etc.) via /api/tags and /api/show.
    /// </summary>
    private sealed record ModelProfile(int ContextLength, int MaxOutput, bool SupportsTools, bool SupportsVision, string Family);

    private static ModelProfile GetProfile(ProviderConfig cfg, string model)
    {
        // Tool calling is advertised (and works) for OpenAI-compatible providers
        // (DeepSeek, OpenAI, Custom), Gemini and Claude — the proxy relays or
        // translates their function-calling payloads into each provider's format.
        _ = model;
        return cfg.Provider switch
        {
            ProviderType.DeepSeek => new(1_000_000, 16_384, true, true, "deepseek"),
            ProviderType.OpenAI => new(128_000, 16_384, true, true, "openai"),
            ProviderType.Gemini => new(1_000_000, 8_192, true, true, "google"),
            ProviderType.Claude => new(200_000, 16_384, true, true, "anthropic"),
            _ => new(32_768, 4_096, true, true, "api"),
        };
    }

    private static string[] BuildCapabilities(ModelProfile p)
    {
        var caps = new List<string> { "completion" };
        if (p.SupportsTools) caps.Add("tools");
        if (p.SupportsVision) caps.Add("vision");
        return caps.ToArray();
    }

    /// <summary>True when the OpenAI request is a tool/agent round that must be
    /// relayed verbatim (it declares tools, uses tool_choice, or carries tool
    /// results / assistant tool_calls in the conversation).</summary>
    private static bool HasToolRound(JsonNode? body)
    {
        if (body is not JsonObject root) return false;
        if (root["tools"] is JsonArray { Count: > 0 }) return true;
        if (root["tool_choice"] is not null) return true;
        if (root["messages"] is JsonArray msgs)
        {
            foreach (var m in msgs)
            {
                if (m is not JsonObject mo) continue;
                if (string.Equals(JsonHelpers.GetStringValue(mo["role"]), "tool", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (mo["tool_calls"] is not null) return true;
            }
        }
        return false;
    }

    private async Task HandleOllamaAsync(HttpContext ctx, bool isGenerate, CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(ctx.Request.Body))
            raw = await reader.ReadToEndAsync(ct);

        JsonNode? body;
        try { body = JsonNode.Parse(raw); }
        catch (JsonException)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid JSON body" }, ct);
            return;
        }
        if (body is null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Empty body" }, ct);
            return;
        }

        var wantStream = body["stream"]?.GetValue<bool>() ?? false;
        ProviderRequest preq;
        try { preq = OllamaTranslator.ToProviderRequest(body, DefaultConfig, isGenerate); }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            return;
        }

        var requestedModel = preq.Model;
        var (route, resolvedModel) = Resolve(requestedModel);
        preq.Model = resolvedModel;

        _log.Info($"[{ctx.Request.Path}] model={requestedModel} → {resolvedModel} via {route.Key} stream={wantStream} messages={preq.Messages.Count}");

        IProviderClient client;
        try { client = _factory.Create(route.Config); }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            return;
        }

        try
        {
            var result = await client.ChatAsync(preq, ct);
            // Echo the model the client asked for, even when we resolved a fallback.
            var model = requestedModel;

            if (wantStream)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ndjson";

                if (result.IsStreaming)
                {
                    await foreach (var token in result.TokenStream!)
                    {
                        if (ct.IsCancellationRequested) break;
                        await ctx.Response.WriteAsync(OllamaTranslator.BuildStreamLine(model, token, isGenerate) + "\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
                else if (!string.IsNullOrEmpty(result.FullContent))
                {
                    await ctx.Response.WriteAsync(OllamaTranslator.BuildStreamLine(model, result.FullContent, isGenerate) + "\n", ct);
                }

                await ctx.Response.WriteAsync(OllamaTranslator.BuildDoneLine(model, isGenerate) + "\n", ct);
            }
            else
            {
                var full = result.IsStreaming
                    ? await CollectAsync(result.TokenStream!, ct)
                    : result.FullContent ?? "";
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(OllamaTranslator.BuildFullJson(requestedModel, full, isGenerate), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-stream; nothing to do.
        }
        catch (ProviderException ex)
        {
            _log.Error($"Provider error on {ctx.Request.Path}: {ex.Message}");
            if (ctx.Response.HasStarted)
            {
                try
                {
                    await ctx.Response.WriteAsync(
                        $"{{\"error\":{JsonSerializer.Serialize(ex.Message)},\"done\":true}}\n", ct);
                }
                catch { }
            }
            else
            {
                // Ollama-style error body, but with the provider's real status (e.g.
                // 429) so clients back off instead of instantly retrying a 502.
                var status = TryGetUpstreamStatus(ex.Message) ?? 502;
                if (status == 429)
                    ctx.Response.Headers["Retry-After"] = GetRetryAfterSeconds(ex.Message).ToString();
                ctx.Response.StatusCode = status;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Unhandled error on {ctx.Request.Path}", ex);
            if (ctx.Response.HasStarted)
            {
                try
                {
                    await ctx.Response.WriteAsync(
                        $"{{\"error\":{JsonSerializer.Serialize(ex.Message)},\"done\":true}}\n", ct);
                }
                catch { }
            }
            else
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            }
        }
    }

    private async Task HandleOpenAiAsync(HttpContext ctx, CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(ctx.Request.Body))
            raw = await reader.ReadToEndAsync(ct);

        JsonNode? body;
        try { body = JsonNode.Parse(raw); }
        catch (JsonException)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid JSON body" }, ct);
            return;
        }
        if (body is null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "Empty body" }, ct);
            return;
        }

        var wantStream = body["stream"]?.GetValue<bool>() ?? false;
        ProviderRequest preq;
        try { preq = OpenAiTranslator.ToProviderRequest(body, DefaultConfig); }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            return;
        }

        var requestedModel = preq.Model;
        var (route, resolvedModel) = Resolve(requestedModel);
        preq.Model = resolvedModel;

        _log.Info($"[{ctx.Request.Path}] model={requestedModel} → {resolvedModel} via {route.Key} stream={wantStream} messages={preq.Messages.Count}");

        // Tool/agent rounds. OpenAI-compatible providers (DeepSeek/OpenAI/Custom)
        // relay the request verbatim so function calling survives unchanged.
        // Gemini and Claude go through their own native function-calling
        // translation in the provider clients.
        if (HasToolRound(body))
        {
            if (route.Config.Provider is ProviderType.Gemini or ProviderType.Claude)
            {
                _log.Info($"[{ctx.Request.Path}] tool/agent round — translating for {route.Key}.");
            }
            else
            {
                preq.RawBody = body.ToJsonString();
                _log.Info($"[{ctx.Request.Path}] tool/agent round detected — relaying verbatim to provider.");
            }
        }

        IProviderClient client;
        try { client = _factory.Create(route.Config); }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            return;
        }

        try
        {
            var result = await client.ChatAsync(preq, ct);
            // Echo the model the client asked for, even when we resolved a fallback.
            var model = requestedModel;

            if (wantStream)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";

                // Tool/agent round: relay the provider's SSE verbatim so tool_calls
                // deltas and the finish_reason="tool_calls" event reach the client.
                if (result.IsPassthrough && result.RawStream is not null)
                {
                    await foreach (var payload in result.RawStream)
                    {
                        if (ct.IsCancellationRequested) break;
                        await ctx.Response.WriteAsync("data: " + payload + "\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                    return;
                }

                // Tool round on Gemini/Claude: the provider client returns the
                // function calls — emit them as OpenAI tool_calls SSE.
                if (result.ToolCalls is { Count: > 0 })
                {
                    await ctx.Response.WriteAsync(OpenAiTranslator.BuildToolCallsStream(model, result.ToolCalls), ct);
                    return;
                }

                var first = true;
                if (result.IsStreaming)
                {
                    await foreach (var token in result.TokenStream!)
                    {
                        if (ct.IsCancellationRequested) break;
                        await ctx.Response.WriteAsync(OpenAiTranslator.BuildStreamChunk(model, token, first, null), ct);
                        await ctx.Response.Body.FlushAsync(ct);
                        first = false;
                    }
                }
                else if (!string.IsNullOrEmpty(result.FullContent))
                {
                    await ctx.Response.WriteAsync(OpenAiTranslator.BuildStreamChunk(model, result.FullContent, true, null), ct);
                }

                await ctx.Response.WriteAsync(OpenAiTranslator.BuildDoneChunk(model), ct);
            }
            else
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";

                // Tool/agent round: return the provider's JSON verbatim so the
                // message.tool_calls payload (and tool_choice result) is preserved.
                if (result.IsPassthrough && !string.IsNullOrEmpty(result.RawBody))
                {
                    await ctx.Response.WriteAsync(result.RawBody, ct);
                    return;
                }

                // Tool round on Gemini/Claude: return a chat.completion whose
                // assistant message carries the translated tool_calls.
                if (result.ToolCalls is { Count: > 0 })
                {
                    await ctx.Response.WriteAsync(OpenAiTranslator.BuildFullJsonTool(model, result.ToolCalls), ct);
                    return;
                }

                var full = result.IsStreaming
                    ? await CollectAsync(result.TokenStream!, ct)
                    : result.FullContent ?? "";
                await ctx.Response.WriteAsync(OpenAiTranslator.BuildFullJson(model, full), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected mid-stream; nothing to do.
        }
        catch (ProviderException ex)
        {
            _log.Error($"Provider error on {ctx.Request.Path}: {ex.Message}");
            await WriteProviderErrorAsync(ctx, ex.Message, ct);
        }
        catch (Exception ex)
        {
            _log.Error($"Error on {ctx.Request.Path}: {ex.Message}");
            if (ctx.Response.HasStarted)
            {
                try { await ctx.Response.WriteAsync(OpenAiTranslator.BuildErrorChunk(ex.Message), ct); }
                catch { }
            }
            else
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsJsonAsync(new { error = ex.Message }, ct);
            }
        }
    }

    /// <summary>
    /// Writes an upstream provider error to an OpenAI-compatible client. Instead of a
    /// blanket 502 it forwards the provider's real status (400/401/404/429/...), and for
    /// 429 it sets Retry-After so the client waits instead of hammering the same request.
    /// </summary>
    private async Task WriteProviderErrorAsync(HttpContext ctx, string message, CancellationToken ct)
    {
        var status = TryGetUpstreamStatus(message) ?? 502;

        if (ctx.Response.HasStarted)
        {
            // Streaming had begun — emit an OpenAI error chunk and stop.
            try { await ctx.Response.WriteAsync(OpenAiTranslator.BuildErrorChunk(message), ct); }
            catch { }
            return;
        }

        if (status == 429)
            ctx.Response.Headers["Retry-After"] = GetRetryAfterSeconds(message).ToString();

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { error = new { message, type = "provider_error", code = status } }, ct);
    }

    /// <summary>
    /// Upstream failures are thrown as "&lt;status&gt; &lt;reason&gt; — &lt;body&gt;" (e.g. "429 Too
    /// Many Requests — ..."). Returns the leading 3-digit HTTP status when present.
    /// </summary>
    private static int? TryGetUpstreamStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var s = message.TrimStart();
        if (s.Length >= 3 && char.IsDigit(s[0]) && char.IsDigit(s[1]) && char.IsDigit(s[2]) &&
            (s.Length == 3 || !char.IsDigit(s[3])) &&
            int.TryParse(s[..3], out var code) && code is >= 400 and <= 599)
        {
            return code;
        }
        return null;
    }

    /// <summary>
    /// Best-effort parse of a "retry in Ns" hint inside the upstream 429 body;
    /// falls back to 60 seconds when the hint is absent/truncated.
    /// </summary>
    private static int GetRetryAfterSeconds(string message)
    {
        const string marker = "retry in";
        var idx = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var rest = message[(idx + marker.Length)..].TrimStart();
            var end = 0;
            while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;
            if (end > 0 && double.TryParse(rest[..end], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var secs) && secs > 0)
            {
                return (int)Math.Ceiling(secs);
            }
        }
        return 60;
    }

    private IResult HandleTags(HttpContext ctx)
    {
        var entries = new List<object>();
        foreach (var route in _routes)
        {
            var names = route.Models.Length > 0 ? route.Models : new[] { route.DefaultModel };
            foreach (var model in names)
            {
                if (string.IsNullOrWhiteSpace(model)) continue;
                var qualified = $"{model}@{route.Key}";
                var p = GetProfile(route.Config, model);
                entries.Add(new
                {
                    name = qualified,
                    model = qualified,
                    modified_at = DateTimeOffset.UtcNow.ToString("o"),
                    size = 0L,
                    digest = "00000000000000000000000000000000",
                    details = new
                    {
                        parent_model = "",
                        format = "gguf",
                        family = p.Family,
                        families = new[] { p.Family },
                        parameter_size = "",
                        quantization_level = "",
                    },
                    capabilities = BuildCapabilities(p),
                    context_length = p.ContextLength,
                    max_output_tokens = p.MaxOutput,
                    input_token_limit = p.ContextLength,
                    output_token_limit = p.MaxOutput,
                    supports_tools = p.SupportsTools,
                    supports_tool_calls = p.SupportsTools,
                    supports_vision = p.SupportsVision,
                    supports_images = p.SupportsVision,
                });
            }
        }

        _log.Info($"[{ctx.Request.Path}] advertising {entries.Count} model(s)");
        return Results.Json(new { models = entries.ToArray() });
    }

    /// <summary>
    /// Ollama /api/show: returns model metadata + capabilities so clients such as
    /// Visual Studio 2026 Copilot BYOM can enable the model (tools) and set token limits.
    /// Supports both GET ?model= and POST with a JSON body.
    /// </summary>
    private async Task HandleShowAsync(HttpContext ctx, CancellationToken ct)
    {
        string? requested = null;
        if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Query.TryGetValue("model", out var q))
        {
            requested = q.ToString();
        }
        else if (HttpMethods.IsPost(ctx.Request.Method))
        {
            string raw;
            using (var reader = new StreamReader(ctx.Request.Body))
                raw = await reader.ReadToEndAsync(ct);
            try
            {
                var body = JsonNode.Parse(raw);
                requested = body?["model"]?.GetValue<string>();
            }
            catch { /* ignore malformed body, fall back to the default model */ }
        }

        var (route, model) = Resolve(NormalizeModelName(requested));
        var qualified = string.IsNullOrWhiteSpace(model)
            ? (string.IsNullOrWhiteSpace(route.DefaultModel) ? route.Key : route.DefaultModel)
            : $"{model}@{route.Key}";
        var p = GetProfile(route.Config, model);

        _log.Info($"[{ctx.Request.Path}] model={qualified}");

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(BuildShowJson(qualified, p), ct);
    }

    private static object BuildShowJson(string model, ModelProfile p)
    {
        return new
        {
            model,
            modified_at = DateTimeOffset.UtcNow.ToString("o"),
            size = 0L,
            digest = "00000000000000000000000000000000",
            license = "api",
            modelfile = $"FROM {model}",
            parameters = $"num_ctx {p.ContextLength}",
            template = "{{ .Prompt }}",
            details = new
            {
                parent_model = "",
                format = "gguf",
                family = p.Family,
                families = new[] { p.Family },
                parameter_size = "",
                quantization_level = "",
            },
            model_info = new Dictionary<string, object>
            {
                ["general.architecture"] = p.Family,
                ["general.basename"] = model,
                ["general.context_length"] = p.ContextLength,
                ["context_length"] = p.ContextLength,
                ["max_output_tokens"] = p.MaxOutput,
                ["input_token_limit"] = p.ContextLength,
                ["output_token_limit"] = p.MaxOutput,
                ["supports_tools"] = p.SupportsTools,
                ["supports_tool_calls"] = p.SupportsTools,
                ["supports_vision"] = p.SupportsVision,
                ["supports_images"] = p.SupportsVision,
            },
            capabilities = BuildCapabilities(p),
            context_length = p.ContextLength,
            max_output_tokens = p.MaxOutput,
            input_token_limit = p.ContextLength,
            output_token_limit = p.MaxOutput,
            supports_tools = p.SupportsTools,
            supports_tool_calls = p.SupportsTools,
            supports_vision = p.SupportsVision,
            supports_images = p.SupportsVision,
        };
    }

    /// <summary>OpenAI-compatible model list (GET /v1/models) for clients that populate a picker from it.</summary>
    private IResult HandleOpenAiModels(HttpContext ctx)
    {
        var ids = new List<object>();
        foreach (var route in _routes)
        {
            var names = route.Models.Length > 0 ? route.Models : new[] { route.DefaultModel };
            foreach (var model in names)
            {
                if (string.IsNullOrWhiteSpace(model)) continue;
                ids.Add(new { id = $"{model}@{route.Key}", @object = "model", created = 0L, owned_by = "ai-cloud-proxy" });
            }
        }

        _log.Info($"[{ctx.Request.Path}] advertising {ids.Count} model(s)");
        return Results.Json(new { @object = "list", data = ids.ToArray() });
    }

    private static async Task<string> CollectAsync(IAsyncEnumerable<string> tokens, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var t in tokens.WithCancellation(ct).ConfigureAwait(false))
        {
            sb.Append(t);
        }
        return sb.ToString();
    }
}
