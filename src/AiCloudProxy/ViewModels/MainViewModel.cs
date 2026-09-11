using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using AiCloudProxy.Infrastructure;
using AiCloudProxy.Models;
using AiCloudProxy.Services;
using AiCloudProxy.Services.Providers;

namespace AiCloudProxy.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly ProxyServer _server;
    private readonly ProviderFactory _factory;

    public ObservableCollection<ProviderOption> Providers { get; } = new(ProviderCatalog.All);
    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    public ObservableCollection<string> AvailableModels { get; } = new();

    public RelayCommand StartStopCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand OpenKeyPageCommand { get; }
    public RelayCommand TestCommand { get; }
    public RelayCommand FetchModelsCommand { get; }
    public RelayCommand AddCustomProviderCommand { get; }
    public RelayCommand RemoveCustomProviderCommand { get; }
    public RelayCommand OpenDonationPageCommand { get; }
    public RelayCommand SendFeedbackCommand { get; }

    private ProviderOption _selectedProvider;
    private string _port;
    private string _apiKey = "";
    private string _model = "";
    private string _baseUrl = "";
    private bool _isRunning;
    private string _statusText = "Stopped";
    private bool _showApiKey;
    private bool _autoStartProxy;
    private bool _minimizeToTray = true;
    private bool _isFetchingModels;
    private string _testInput = "";
    private string _testOutput = "";
    private bool _isTesting;
    private string _keyPageUrl = "";
    private string _newProviderName = "";
    private CancellationTokenSource? _testCts;

    public MainViewModel(SettingsService settings, LogService log, ProxyServer server, ProviderFactory factory)
    {
        _settings = settings;
        _log = log;
        _server = server;
        _factory = factory;

        var s = _settings.Settings;

        // Rebuild the provider list: built-ins + any saved custom providers.
        foreach (var c in s.CustomProviders)
        {
            var name = string.IsNullOrWhiteSpace(c.Name) ? "Custom" : c.Name.Trim();
            Providers.Add(new ProviderOption(ProviderType.Custom, name, name, "", "", ""));
        }

        _selectedProvider =
            ProviderCatalog.All.FirstOrDefault(p => string.Equals(p.Key, s.Provider, StringComparison.OrdinalIgnoreCase))
            ?? Providers.FirstOrDefault(p => string.Equals(p.Key, s.Provider, StringComparison.OrdinalIgnoreCase))
            ?? Providers[0];

        _port = s.Port.ToString();
        _autoStartProxy = s.AutoStartProxy;
        _minimizeToTray = s.MinimizeToTray;
        LoadProviderState(_selectedProvider);
        KeyPageUrl = _selectedProvider.KeyPageUrl;

        StartStopCommand = new RelayCommand(_ => _ = ToggleServerAsync());
        ClearLogCommand = new RelayCommand(_ => LogEntries.Clear());
        CopyLogCommand = new RelayCommand(_ => CopyLog());
        OpenKeyPageCommand = new RelayCommand(_ => OpenKeyPage());
        TestCommand = new RelayCommand(_ => _ = TestAsync());
        FetchModelsCommand = new RelayCommand(_ => _ = FetchModelsAsync());
        AddCustomProviderCommand = new RelayCommand(_ => AddCustomProvider());
        RemoveCustomProviderCommand = new RelayCommand(_ => RemoveCustomProvider());
        OpenDonationPageCommand = new RelayCommand(_ => OpenDonationPage());
        SendFeedbackCommand = new RelayCommand(_ => SendFeedback());

        log.EntryAdded += OnLogEntry;
    }

    // ---------- UI state ----------

    public ProviderOption SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            var prev = _selectedProvider;
            if (!Set(ref _selectedProvider, value) || value is null) return;

            // Save the outgoing provider's key/base URL/model, then load the new one's.
            if (prev is not null) SaveProviderMemory(prev);
            LoadProviderState(value);

            // Models are provider-specific; drop anything fetched for the old provider.
            AvailableModels.Clear();

            KeyPageUrl = value.KeyPageUrl;
            OnPropertyChanged(nameof(IsCustomProvider));
        }
    }

    public string Port { get => _port; set => Set(ref _port, value); }

    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value); }

    public string Model { get => _model; set => Set(ref _model, value); }

    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value); }

    public bool ShowApiKey { get => _showApiKey; set => Set(ref _showApiKey, value); }

    public bool AutoStartProxy { get => _autoStartProxy; set => Set(ref _autoStartProxy, value); }

    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }

    public string KeyPageUrl
    {
        get => _keyPageUrl;
        private set
        {
            if (Set(ref _keyPageUrl, value)) OnPropertyChanged(nameof(CanOpenKeyPage));
        }
    }

    /// <summary>True when the selected provider is a user-added custom endpoint.</summary>
    public bool IsCustomProvider => SelectedProvider?.Type == ProviderType.Custom;

    /// <summary>True when the selected provider has a "Get API Key" page to open.</summary>
    public bool CanOpenKeyPage => !string.IsNullOrWhiteSpace(KeyPageUrl);

    /// <summary>Buy Me a Coffee donation page for this project.</summary>
    public string DonationUrl => "https://buymeacoffee.com/local1907";

    /// <summary>Email address where feedback should be sent.</summary>
    public string FeedbackEmail => "local1907@gmail.com";

    /// <summary>Name being typed for a new custom AI provider.</summary>
    public string NewProviderName { get => _newProviderName; set => Set(ref _newProviderName, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StartStopLabel));
                OnPropertyChanged(nameof(StartStopHint));
            }
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Version of the running build shown in the header (e.g. "v1.0.8.0").</summary>
    public string VersionLabel => AppInfo.VersionLabel;

    /// <summary>Window title including the running version.</summary>
    public string WindowTitle => AppInfo.TitleWithVersion;

    public string StartStopLabel => IsRunning ? "Stop Proxy" : "Start Proxy";

    public string StartStopHint => IsRunning
        ? "Click to stop the local Ollama-compatible server"
        : "Click to start the local Ollama-compatible server";

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (Set(ref _isTesting, value)) OnPropertyChanged(nameof(TestButtonLabel));
        }
    }

    public string TestButtonLabel => IsTesting ? "Stop" : "Send";

    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        private set
        {
            if (Set(ref _isFetchingModels, value)) OnPropertyChanged(nameof(FetchModelsLabel));
        }
    }

    public string FetchModelsLabel => IsFetchingModels ? "Loading…" : "Get Models";

    public string TestInput { get => _testInput; set => Set(ref _testInput, value); }

    public string TestOutput { get => _testOutput; set => Set(ref _testOutput, value); }

    // ---------- Actions ----------

    public ProviderConfig BuildConfig()
    {
        return new ProviderConfig
        {
            Provider = SelectedProvider?.Type ?? ProviderType.DeepSeek,
            ApiKey = ApiKey?.Trim() ?? "",
            Model = string.IsNullOrWhiteSpace(Model) ? SelectedProvider?.DefaultModel ?? "" : Model.Trim(),
            BaseUrl = BaseUrl?.Trim() ?? "",
        };
    }

    /// <summary>
    /// Builds the list of active provider routes: every provider with a saved API key
    /// (and, for custom providers, a base URL). The selected provider's live field
    /// values are used; the others come from the persisted per-provider settings.
    /// </summary>
    public List<ProviderRouteConfig> BuildActiveRoutes()
    {
        var s = _settings.Settings;
        var routes = new List<ProviderRouteConfig>();

        foreach (var p in Providers)
        {
            string apiKey, model, baseUrl;
            if (p == SelectedProvider)
            {
                apiKey = ApiKey?.Trim() ?? "";
                model = string.IsNullOrWhiteSpace(Model) ? p.DefaultModel : Model.Trim();
                baseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? p.DefaultBaseUrl : BaseUrl.Trim();
            }
            else
            {
                apiKey = s.ApiKeys.TryGetValue(p.Key, out var k) ? k ?? "" : "";
                var hasBase = s.BaseUrls.TryGetValue(p.Key, out var b) && !string.IsNullOrWhiteSpace(b);
                baseUrl = hasBase ? b! : p.DefaultBaseUrl;
                var hasModel = s.Models.TryGetValue(p.Key, out var m) && !string.IsNullOrWhiteSpace(m);
                model = hasModel ? m! : p.DefaultModel;
            }

            if (string.IsNullOrWhiteSpace(apiKey)) continue;                     // provider not configured
            if (p.Type == ProviderType.Custom && string.IsNullOrWhiteSpace(baseUrl)) continue; // custom needs a URL

            routes.Add(new ProviderRouteConfig
            {
                Key = p.Key,
                Config = new ProviderConfig
                {
                    Provider = p.Type,
                    ApiKey = apiKey,
                    Model = model,
                    BaseUrl = baseUrl,
                },
            });
        }

        return routes;
    }

    public async Task StartProxyAsync()
    {
        if (_server.IsRunning) return;

        var cfg = BuildConfig();
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            _log.Warn("Cannot start: no API key. Enter your provider API key first.");
            return;
        }
        if (SelectedProvider?.Type == ProviderType.Custom && string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            _log.Warn("Cannot start: enter a Base URL for your custom AI provider first.");
            return;
        }
        if (!int.TryParse(Port?.Trim(), out var port) || port is < 1 or > 65535)
        {
            _log.Warn($"Cannot start: invalid port '{Port}'. Use a number between 1 and 65535.");
            return;
        }

        // Validate the configured model against the provider before starting.
        // The user keeps full control: they can switch, continue anyway, or cancel.
        var (cancelled, newModel, models) = await ValidateAndMaybeFixModelAsync(cfg);
        if (cancelled) return;
        if (!string.IsNullOrWhiteSpace(newModel))
        {
            Model = newModel;
            cfg = BuildConfig(); // rebuild with the corrected model
        }

        _settings.Settings.Port = port;
        PersistNow();

        try
        {
            // All configured providers (with a saved key) are active at once;
            // requests route to the provider that owns the requested model.
            var routes = BuildActiveRoutes();
            var defaultKey = SelectedProvider?.Key ?? routes[0].Key;
            var known = new Dictionary<string, IReadOnlyList<string>>();
            if (models is { Count: > 0 } && SelectedProvider is not null)
                known[SelectedProvider.Key] = models;

            await _server.StartAsync(routes, defaultKey, port, known);
            IsRunning = true;
            StatusText = $"Running on http://127.0.0.1:{port}";
            _log.Info($"Active providers: {string.Join(", ", routes.Select(r => r.Key))} — default: {defaultKey}");
        }
        catch
        {
            IsRunning = false;
            StatusText = "Failed to start";
        }
    }

    /// <summary>
    /// Queries the provider's live model list and warns when the configured model
    /// is no longer available. Returns whether the user cancelled startup, a
    /// suggested replacement (or null to keep the current one), and the discovered
    /// model list so the proxy does not have to fetch it again.
    /// </summary>
    private async Task<(bool Cancelled, string? NewModel, IReadOnlyList<string>? Models)> ValidateAndMaybeFixModelAsync(ProviderConfig cfg)
    {
        IReadOnlyList<string> models;
        try
        {
            var client = _factory.Create(cfg);
            models = await client.ListModelsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Validation is best-effort: never block startup because of it.
            _log.Warn($"Could not validate the model against the provider ({ex.Message}). Starting anyway.");
            return (false, null, null);
        }

        if (models.Count == 0)
        {
            _log.Warn("The provider returned an empty model list. Starting anyway.");
            return (false, null, null);
        }

        // Populate the dropdown while we have the list handy.
        AvailableModels.Clear();
        foreach (var m in models) AvailableModels.Add(m);

        var current = cfg.Model;
        if (models.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase)))
        {
            _log.Info($"Model '{current}' is valid for {SelectedProvider?.DisplayName}.");
            return (false, null, models);
        }

        var first = models[0];
        var available = string.Join(", ", models.Take(5)) + (models.Count > 5 ? "…" : "");
        var result = System.Windows.MessageBox.Show(
            $"The model '{current}' is not in your provider's current model list.\n\n" +
            $"Available models: {available}\n\n" +
            "Do you want to switch to a valid model, or continue with the current one?",
            "Model not found",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);

        switch (result)
        {
            case System.Windows.MessageBoxResult.Yes:
                _log.Warn($"Model '{current}' not found. Switching to '{first}'.");
                return (false, first, models);
            case System.Windows.MessageBoxResult.No:
                _log.Warn($"Model '{current}' not found, but continuing anyway as requested.");
                return (false, null, models);
            default: // Cancel
                _log.Warn("Start cancelled by the user.");
                return (true, null, null);
        }
    }

    public async Task StopProxyAsync()
    {
        if (!_server.IsRunning) return;
        await _server.StopAsync();
        IsRunning = false;
        StatusText = "Stopped";
    }

    public async Task ToggleServerAsync()
    {
        if (_server.IsRunning) await StopProxyAsync();
        else await StartProxyAsync();
    }

    private async Task FetchModelsAsync()
    {
        if (IsFetchingModels) return;

        var cfg = BuildConfig();
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            _log.Warn("Enter your provider API key first, then load the model list.");
            return;
        }
        if (SelectedProvider?.Type == ProviderType.Custom && string.IsNullOrWhiteSpace(cfg.BaseUrl))
        {
            _log.Warn("Enter the Base URL for your custom AI provider first, then load the model list.");
            return;
        }

        IsFetchingModels = true;
        try
        {
            var client = _factory.Create(cfg);
            var models = await client.ListModelsAsync(CancellationToken.None);

            AvailableModels.Clear();
            foreach (var m in models) AvailableModels.Add(m);

            if (AvailableModels.Count == 0)
            {
                _log.Warn("The provider returned an empty model list.");
                return;
            }

            // Keep the current model when it is still valid, otherwise pick the first.
            var current = Model?.Trim();
            if (string.IsNullOrWhiteSpace(current) ||
                !AvailableModels.Any(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase)))
            {
                Model = AvailableModels[0];
            }
            _log.Info($"Loaded {AvailableModels.Count} models from {SelectedProvider?.DisplayName}.");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load the model list", ex);
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    private async Task TestAsync()
    {
        if (IsTesting)
        {
            _testCts?.Cancel();
            _log.Info("Test cancelled by user.");
            return;
        }

        var cfg = BuildConfig();
        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            _log.Warn("API key is required to run a test.");
            return;
        }
        if (string.IsNullOrWhiteSpace(TestInput))
        {
            _log.Warn("Enter a question in the Test tab first.");
            return;
        }
        if (string.IsNullOrWhiteSpace(cfg.Model))
        {
            _log.Warn("Model is required.");
            return;
        }

        IsTesting = true;
        TestOutput = "";
        StatusText = "Testing…";
        _log.Info($"Sending test question to {SelectedProvider?.DisplayName} (model {cfg.Model})…");
        _testCts = new CancellationTokenSource();

        var sb = new StringBuilder();
        try
        {
            var client = _factory.Create(cfg);
            var request = new ProviderRequest
            {
                Model = cfg.Model,
                Messages = new List<ChatMessage> { new("user", TestInput) },
                Stream = true,
            };

            var result = await client.ChatAsync(request, _testCts.Token);
            var tokens = result.TokenStream ?? EmptyTokens();
            var count = 0;
            await foreach (var token in tokens)
            {
                sb.Append(token);
                if ((++count % 24) == 0) TestOutput = sb.ToString();
            }
            TestOutput = sb.ToString();
            _log.Info($"Test finished. Received {sb.Length} characters.");
        }
        catch (OperationCanceledException)
        {
            TestOutput = sb.ToString() + "\n\n[stopped by user]";
            _log.Warn("Test stopped.");
        }
        catch (Exception ex)
        {
            TestOutput = sb.ToString() + $"\n\n[error] {ex.Message}";
            _log.Error("Test failed", ex);
        }
        finally
        {
            IsTesting = false;
            StatusText = _server.IsRunning
                ? $"Running on http://127.0.0.1:{_server.Port}"
                : "Stopped";
        }
    }

    private void OpenKeyPage()
    {
        if (string.IsNullOrWhiteSpace(KeyPageUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(KeyPageUrl) { UseShellExecute = true });
            _log.Info($"Opened API key page: {KeyPageUrl}");
        }
        catch (Exception ex)
        {
            _log.Error("Could not open the browser", ex);
        }
    }

    private void OpenDonationPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DonationUrl) { UseShellExecute = true });
            _log.Info($"Opened donation page: {DonationUrl}");
        }
        catch (Exception ex)
        {
            _log.Error("Could not open the browser", ex);
        }
    }

    private void SendFeedback()
    {
        try
        {
            // Opens the user's own email client; no personal info is collected or stored by the app.
            var subject = Uri.EscapeDataString("AI Cloud Proxy Feedback");
            var mailto = $"mailto:{FeedbackEmail}?subject={subject}";
            Process.Start(new ProcessStartInfo(mailto) { UseShellExecute = true });
            _log.Info($"Opened email client for feedback: {FeedbackEmail}");
        }
        catch (Exception ex)
        {
            _log.Error("Could not open the email client", ex);
        }
    }

    private void LoadProviderState(ProviderOption p)
    {
        var s = _settings.Settings;
        ApiKey = s.ApiKeys.TryGetValue(p.Key, out var k) ? k ?? "" : "";
        var hasBase = s.BaseUrls.TryGetValue(p.Key, out var b) && !string.IsNullOrWhiteSpace(b);
        BaseUrl = hasBase ? b! : p.DefaultBaseUrl;
        var hasModel = s.Models.TryGetValue(p.Key, out var m) && !string.IsNullOrWhiteSpace(m);
        Model = hasModel ? m! : p.DefaultModel;
    }

    private void SaveProviderMemory(ProviderOption p)
    {
        var s = _settings.Settings;
        if (!string.IsNullOrWhiteSpace(ApiKey)) s.ApiKeys[p.Key] = ApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(Model)) s.Models[p.Key] = Model.Trim();
        if (!string.IsNullOrWhiteSpace(BaseUrl) &&
            !string.Equals(BaseUrl, p.DefaultBaseUrl, StringComparison.OrdinalIgnoreCase))
            s.BaseUrls[p.Key] = BaseUrl.Trim();
        else
            s.BaseUrls.Remove(p.Key);
    }

    /// <summary>Saves the current UI state (per-provider key/base URL/model, port, options) to disk.</summary>
    public void PersistNow()
    {
        var s = _settings.Settings;
        if (SelectedProvider is not null) SaveProviderMemory(SelectedProvider);
        s.Provider = SelectedProvider?.Key ?? s.Provider;
        if (int.TryParse(Port?.Trim(), out var port) && port is > 0 and < 65536) s.Port = port;
        s.AutoStartProxy = AutoStartProxy;
        s.MinimizeToTray = MinimizeToTray;
        _settings.Save();
    }

    private void AddCustomProvider()
    {
        var name = NewProviderName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _log.Warn("Enter a name for your custom AI provider first.");
            return;
        }
        if (Providers.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
        {
            _log.Warn($"A provider named '{name}' already exists.");
            return;
        }

        _settings.Settings.CustomProviders.Add(new CustomProvider { Name = name });
        Providers.Add(new ProviderOption(ProviderType.Custom, name, name, "", "", ""));
        PersistNow();
        SelectedProvider = Providers[^1];
        NewProviderName = "";
        _log.Info($"Added custom AI provider '{name}'. Enter its Base URL, API key and default model, then click Get Models.");
    }

    private void RemoveCustomProvider()
    {
        var p = SelectedProvider;
        if (p is null || p.Type != ProviderType.Custom)
        {
            _log.Warn("Select a custom AI provider to remove it.");
            return;
        }

        Providers.Remove(p);
        SelectedProvider = Providers[0]; // switching back also re-saves the removed provider's memory, purged below
        _settings.Settings.CustomProviders.RemoveAll(c => string.Equals(c.Name, p.Key, StringComparison.OrdinalIgnoreCase));
        _settings.Settings.ApiKeys.Remove(p.Key);
        _settings.Settings.BaseUrls.Remove(p.Key);
        _settings.Settings.Models.Remove(p.Key);
        _settings.Save();
        _log.Info($"Removed custom AI provider '{p.DisplayName}'.");
    }

    private void CopyLog()
    {
        if (LogEntries.Count == 0)
        {
            _log.Warn("The log is empty.");
            return;
        }
        var sb = new StringBuilder();
        foreach (var e in LogEntries)
            sb.AppendLine($"[{e.TimeDisplay}] {e.Level,-5} {e.Message}");
        try
        {
            System.Windows.Clipboard.SetText(sb.ToString());
            _log.Info($"Copied {LogEntries.Count} log line(s) to the clipboard.");
        }
        catch (Exception ex)
        {
            _log.Error("Could not copy the log", ex);
        }
    }

    private void OnLogEntry(LogEntry entry)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AddLogEntry(entry);
        }
        else
        {
            dispatcher.InvokeAsync(() => AddLogEntry(entry));
        }
    }

    private void AddLogEntry(LogEntry entry)
    {
        LogEntries.Add(entry);
        while (LogEntries.Count > 600) LogEntries.RemoveAt(0);
    }

    private static async IAsyncEnumerable<string> EmptyTokens()
    {
        await Task.CompletedTask;
        yield break;
    }
}
