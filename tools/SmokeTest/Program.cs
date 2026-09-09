using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using AiCloudProxy.Models;
using AiCloudProxy.Services;
using AiCloudProxy.Services.Providers;

namespace SmokeTest;

/// <summary>
/// Offline end-to-end test of the proxy. A local fake provider stands in for
/// DeepSeek/OpenAI so no real API key or internet connection is required.
/// </summary>
internal static class Program
{
    private const int FakeProviderPort = 19191;
    private const int ProxyPort = 18181;

    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.WriteLine("AI Cloud Proxy — smoke test");
        Console.WriteLine("================================");

        using var fakeProvider = await StartFakeProviderAsync(FakeProviderPort);
        var log = new LogService();
        var factory = new ProviderFactory(HttpClientFactory.Create(), log);
        var server = new ProxyServer(log, factory);

        var cfg = new ProviderConfig
        {
            Provider = ProviderType.DeepSeek,
            ApiKey = "test-key",
            Model = "deepseek-v4-flash",
            BaseUrl = $"http://127.0.0.1:{FakeProviderPort}/v1",
        };

        try
        {
            await server.StartAsync(cfg, ProxyPort);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ProxyPort}") };

            await Check("GET /api/version", async () =>
            {
                var res = await http.GetStringAsync("/api/version");
                Assert(res.Contains("0.1.0"), $"version response: {res}");
            });

            await Check("GET /api/tags advertises model", async () =>
            {
                var res = await http.GetStringAsync("/api/tags");
                Assert(res.Contains("deepseek-v4-flash"), $"tags response: {res}");
            });

            await Check("GET /api/tags advertises all discovered models", async () =>
            {
                var res = await http.GetStringAsync("/api/tags");
                var arr = JsonNode.Parse(res)?["models"]?.AsArray();
                Assert(arr != null && arr.Count >= 2, $"expected >=2 advertised models: {res}");
            });

            await Check("GET /api/tags advertises capabilities (VS 2026 BYOM)", async () =>
            {
                var res = await http.GetStringAsync("/api/tags");
                var arr = JsonNode.Parse(res)?["models"]?.AsArray();
                Assert(arr != null && arr.Count >= 1, $"no models: {res}");
                var first = arr![0];
                Assert(first?["supports_tools"]?.GetValue<bool>() == true, $"supports_tools not true: {res}");
                Assert(first?["supports_vision"]?.GetValue<bool>() == true, $"supports_vision not true: {res}");
                var caps = first?["capabilities"]?.AsArray();
                Assert(caps?.Any(c => c?.GetValue<string>() == "tools") == true, $"capabilities missing tools: {res}");
                Assert(caps?.Any(c => c?.GetValue<string>() == "vision") == true, $"capabilities missing vision: {res}");
                Assert((first?["context_length"]?.GetValue<long>() ?? 0) > 0, $"context_length missing: {res}");
            });

            await Check("POST /api/show returns capabilities + token limits", async () =>
            {
                var body = """{"model":"deepseek-v4-flash"}""";
                var res = await http.PostAsync("/api/show", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                Assert(doc?["supports_tools"]?.GetValue<bool>() == true, $"supports_tools not true: {text}");
                Assert(doc?["supports_vision"]?.GetValue<bool>() == true, $"supports_vision not true: {text}");
                var caps = doc?["capabilities"]?.AsArray();
                Assert(caps?.Any(c => c?.GetValue<string>() == "completion") == true && caps?.Any(c => c?.GetValue<string>() == "tools") == true && caps?.Any(c => c?.GetValue<string>() == "vision") == true, $"capabilities wrong: {text}");
                Assert((doc?["max_output_tokens"]?.GetValue<long>() ?? 0) > 0, $"max_output_tokens missing: {text}");
            });

            await Check("GET /v1/models (OpenAI format)", async () =>
            {
                var res = await http.GetStringAsync("/v1/models");
                var doc = JsonNode.Parse(res);
                var data = doc?["data"]?.AsArray();
                Assert(data?.Count >= 2, $"expected >=2 models: {res}");
                Assert(data?.Any(d => d?["id"]?.GetValue<string>() == "deepseek-v4-flash@DeepSeek") == true, $"missing deepseek-v4-flash@DeepSeek: {res}");
            });

            await Check("POST /api/chat (stream) -> Ollama NDJSON", async () =>
            {
                var body = """{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"hi"}],"stream":true}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert(lines.Length >= 2, $"expected >=2 lines, got {lines.Length}");
                Assert(text.Contains("Hello") && text.Contains("world!"), $"streamed content missing: {text}");
                var last = JsonNode.Parse(lines[^1]);
                Assert(last?["done"]?.GetValue<bool>() == true, "last line must be done:true");
            });

            await Check("POST /api/chat (no stream) -> JSON", async () =>
            {
                var body = """{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"hi"}],"stream":false}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                var content = doc?["message"]?["content"]?.GetValue<string>() ?? "";
                Assert(content == "Hello world!", $"content mismatch: '{content}'");
                Assert(doc?["done"]?.GetValue<bool>() == true, "expected done:true");
            });

            await Check("POST /api/generate (stream) -> Ollama NDJSON", async () =>
            {
                var body = """{"model":"llama3","prompt":"hi","stream":true}""";
                var res = await http.PostAsync("/api/generate", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                Assert(text.Contains("Hello") && text.Contains("world!"), $"streamed response missing: {text}");
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var last = JsonNode.Parse(lines[^1]);
                Assert(last?["done"]?.GetValue<bool>() == true, "last line must be done:true");
            });

            await Check("POST /v1/chat/completions (stream) -> SSE", async () =>
            {
                var body = """{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"hi"}],"stream":true}""";
                var res = await http.PostAsync("/v1/chat/completions", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                Assert(text.Contains("data: [DONE]"), "expected [DONE] terminator");
                Assert(text.Contains("Hello") && text.Contains("world!"), $"streamed content missing: {text}");
            });

            await Check("POST /v1/chat/completions (no stream) -> JSON", async () =>
            {
                var body = """{"model":"deepseek-v4-flash","messages":[{"role":"user","content":"hi"}],"stream":false}""";
                var res = await http.PostAsync("/v1/chat/completions", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                var content = doc?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
                Assert(content == "Hello world!", $"content mismatch: '{content}'");
            });

            await Check("Invalid JSON -> 400", async () =>
            {
                var res = await http.PostAsync("/api/chat", new StringContent("not json", Encoding.UTF8, "application/json"));
                Assert(res.StatusCode == System.Net.HttpStatusCode.BadRequest, $"expected 400, got {(int)res.StatusCode}");
            });

            await Check("OpenAI-compatible ListModelsAsync", async () =>
            {
                var client = factory.Create(cfg);
                var models = await client.ListModelsAsync(CancellationToken.None);
                Assert(models.Contains("deepseek-v4-flash"), $"missing deepseek-v4-flash: {string.Join(",", models)}");
            });

            // --- Gemini (native format) ---
            await server.StopAsync();
            var geminiCfg = new ProviderConfig
            {
                Provider = ProviderType.Gemini,
                ApiKey = "test-key",
                Model = "gemini-2.5-flash",
                BaseUrl = $"http://127.0.0.1:{FakeProviderPort}",
            };
            await server.StartAsync(geminiCfg, ProxyPort);

            await Check("Gemini POST /api/chat (stream)", async () =>
            {
                var body = """{"model":"gemini-2.5-flash","messages":[{"role":"user","content":"hi"}],"stream":true}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                Assert(text.Contains("Gemini") && text.Contains("says hi"), $"streamed content missing: {text}");
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var last = JsonNode.Parse(lines[^1]);
                Assert(last?["done"]?.GetValue<bool>() == true, "last line must be done:true");
            });

            await Check("Gemini POST /api/chat (no stream)", async () =>
            {
                var body = """{"model":"gemini-2.5-flash","messages":[{"role":"user","content":"hi"}],"stream":false}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                var content = doc?["message"]?["content"]?.GetValue<string>() ?? "";
                Assert(content == "Gemini says hi", $"content mismatch: '{content}'");
            });

            await Check("Gemini ListModelsAsync", async () =>
            {
                var client = factory.Create(geminiCfg);
                var models = await client.ListModelsAsync(CancellationToken.None);
                Assert(models.Contains("gemini-2.5-flash"), $"missing gemini-2.5-flash: {string.Join(",", models)}");
            });

            // --- Claude (native format) ---
            await server.StopAsync();
            var claudeCfg = new ProviderConfig
            {
                Provider = ProviderType.Claude,
                ApiKey = "test-key",
                Model = "claude-sonnet-4-5-20250929",
                BaseUrl = $"http://127.0.0.1:{FakeProviderPort}",
            };
            await server.StartAsync(claudeCfg, ProxyPort);

            await Check("Claude POST /api/chat (stream)", async () =>
            {
                var body = """{"model":"claude-sonnet-4-5-20250929","messages":[{"role":"user","content":"hi"}],"stream":true}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                Assert(text.Contains("Claude") && text.Contains("says hi"), $"streamed content missing: {text}");
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var last = JsonNode.Parse(lines[^1]);
                Assert(last?["done"]?.GetValue<bool>() == true, "last line must be done:true");
            });

            await Check("Claude POST /api/chat (no stream)", async () =>
            {
                var body = """{"model":"claude-sonnet-4-5-20250929","messages":[{"role":"user","content":"hi"}],"stream":false}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                var content = doc?["message"]?["content"]?.GetValue<string>() ?? "";
                Assert(content == "Claude says hi", $"content mismatch: '{content}'");
            });

            await Check("Claude ListModelsAsync", async () =>
            {
                var client = factory.Create(claudeCfg);
                var models = await client.ListModelsAsync(CancellationToken.None);
                Assert(models.Contains("claude-sonnet-4-5-20250929"), $"missing claude model: {string.Join(",", models)}");
            });

            // --- Multi-provider routing (all active at once, qualified model@Provider names) ---
            await server.StopAsync();
            var deepSeekRoute = new ProviderRouteConfig
            {
                Key = "DeepSeek",
                Config = new ProviderConfig
                {
                    Provider = ProviderType.DeepSeek,
                    ApiKey = "test-key",
                    Model = "deepseek-v4-flash",
                    BaseUrl = $"http://127.0.0.1:{FakeProviderPort}/v1",
                },
            };
            var geminiRoute = new ProviderRouteConfig
            {
                Key = "Gemini",
                Config = new ProviderConfig
                {
                    Provider = ProviderType.Gemini,
                    ApiKey = "test-key",
                    Model = "gemini-2.5-flash",
                    BaseUrl = $"http://127.0.0.1:{FakeProviderPort}",
                },
            };
            await server.StartAsync(new[] { deepSeekRoute, geminiRoute }, "DeepSeek", ProxyPort);

            await Check("Multi-provider /api/tags advertises qualified names", async () =>
            {
                var res = await http.GetStringAsync("/api/tags");
                Assert(res.Contains("deepseek-v4-flash@DeepSeek"), $"missing qualified deepseek: {res}");
                Assert(res.Contains("gemini-2.5-flash@Gemini"), $"missing qualified gemini: {res}");
            });

            await Check("Multi-provider routes to DeepSeek by qualified name", async () =>
            {
                var body = """{"model":"deepseek-v4-flash@DeepSeek","messages":[{"role":"user","content":"hi"}],"stream":false}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                var content = doc?["message"]?["content"]?.GetValue<string>() ?? "";
                Assert(content == "Hello world!", $"expected DeepSeek response, got '{content}': {text}");
            });

            await Check("Multi-provider routes to Gemini by qualified name", async () =>
            {
                var body = """{"model":"gemini-2.5-flash@Gemini","messages":[{"role":"user","content":"hi"}],"stream":false}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                var content = doc?["message"]?["content"]?.GetValue<string>() ?? "";
                Assert(content == "Gemini says hi", $"expected Gemini response, got '{content}': {text}");
            });

            await Check("Multi-provider unknown name falls back to default provider", async () =>
            {
                var body = """{"model":"llama3","messages":[{"role":"user","content":"hi"}],"stream":false}""";
                var res = await http.PostAsync("/api/chat", Json(body));
                var text = await res.Content.ReadAsStringAsync();
                var doc = JsonNode.Parse(text);
                var content = doc?["message"]?["content"]?.GetValue<string>() ?? "";
                Assert(content == "Hello world!", $"expected default (DeepSeek) response, got '{content}': {text}");
            });
        }
        finally
        {
            await server.StopAsync();
            await fakeProvider.StopAsync();
        }

        Console.WriteLine();
        Console.WriteLine($"Passed: {_passed}   Failed: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task<WebApplication> StartFakeProviderAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapPost("/v1/chat/completions", async (HttpContext ctx, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var stream = body.Contains("\"stream\":true");
            ctx.Response.ContentType = stream ? "text/event-stream" : "application/json";

            if (stream)
            {
                await ctx.Response.WriteAsync(
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n", ct);
                await ctx.Response.WriteAsync(
                    "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world!\"},\"finish_reason\":null}]}\n\n", ct);
                await ctx.Response.WriteAsync(
                    "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n", ct);
                await ctx.Response.WriteAsync("data: [DONE]\n\n", ct);
            }
            else
            {
                await ctx.Response.WriteAsync(
                    "{\"model\":\"deepseek-v4-flash\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"Hello world!\"},\"finish_reason\":\"stop\"}]}", ct);
            }
        });

        // OpenAI-compatible / Anthropic model list (same path, different auth).
        app.MapGet("/v1/models", async (HttpContext ctx, CancellationToken ct) =>
        {
            await ctx.Response.WriteAsync(
                """{"data":[{"id":"deepseek-v4-flash"},{"id":"gpt-4o-mini"},{"id":"claude-sonnet-4-5-20250929"}]}""", ct);
        });

        // Gemini model list.
        app.MapGet("/v1beta/models", async (HttpContext ctx, CancellationToken ct) =>
        {
            await ctx.Response.WriteAsync(
                """{"models":[{"name":"models/gemini-2.5-flash"},{"name":"models/gemini-2.5-pro"}]}""", ct);
        });

        // Gemini native API (streaming uses ?alt=sse).
        app.MapPost("/v1beta/{**path}", async (HttpContext ctx, CancellationToken ct) =>
        {
            var isStream = (ctx.Request.QueryString.Value ?? "").Contains("alt=sse");
            ctx.Response.ContentType = isStream ? "text/event-stream" : "application/json";
            if (isStream)
            {
                await ctx.Response.WriteAsync(
                    "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Gemini\"}]}}]}\n\n", ct);
                await ctx.Response.WriteAsync(
                    "data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\" says hi\"}]}}]}\n\n", ct);
            }
            else
            {
                await ctx.Response.WriteAsync(
                    "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"Gemini says hi\"}]}}]}", ct);
            }
        });

        // Anthropic Messages API.
        app.MapPost("/v1/messages", async (HttpContext ctx, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var stream = body.Contains("\"stream\":true");
            ctx.Response.ContentType = stream ? "text/event-stream" : "application/json";
            if (stream)
            {
                await ctx.Response.WriteAsync(
                    "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Claude\"}}\n\n", ct);
                await ctx.Response.WriteAsync(
                    "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" says hi\"}}\n\n", ct);
                await ctx.Response.WriteAsync(
                    "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", ct);
            }
            else
            {
                await ctx.Response.WriteAsync(
                    "{\"model\":\"claude-sonnet-4-5-20250929\",\"content\":[{\"type\":\"text\",\"text\":\"Claude says hi\"}]}", ct);
            }
        });

        await app.StartAsync();
        return app;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task Check(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  FAIL  {name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
