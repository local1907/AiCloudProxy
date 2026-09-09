using System.Net.Http;
using AiCloudProxy.Models;

namespace AiCloudProxy.Services.Providers;

public class ProviderFactory
{
    private readonly HttpClient _http;
    private readonly LogService _log;

    public ProviderFactory(HttpClient http, LogService log)
    {
        _http = http;
        _log = log;
    }

    public IProviderClient Create(ProviderConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
            throw new ProviderException("API key is not configured. Enter your provider API key and try again.");

        if (string.IsNullOrWhiteSpace(cfg.Model))
            throw new ProviderException("Model is not configured. Select a provider or enter a model name.");

        return cfg.Provider switch
        {
            ProviderType.Gemini => new GeminiClient(_http, cfg, _log),
            ProviderType.Claude => new ClaudeClient(_http, cfg, _log),
            _ => new OpenAiCompatibleClient(_http, cfg, _log),
        };
    }
}
