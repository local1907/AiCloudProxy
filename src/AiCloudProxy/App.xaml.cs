using System.Windows;
using Forms = System.Windows.Forms;
using AiCloudProxy.Infrastructure;
using AiCloudProxy.Services;
using AiCloudProxy.Services.Providers;
using AiCloudProxy.Tray;
using AiCloudProxy.ViewModels;
using AiCloudProxy.Views;

namespace AiCloudProxy;

public partial class App : System.Windows.Application
{
    private SettingsService _settings = null!;
    private LogService _log = null!;
    private ProxyServer _server = null!;
    private MainViewModel _vm = null!;
    private MainWindow _window = null!;
    private TrayIcon _tray = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = new SettingsService();
        _log = new LogService();

        _log.Info($"{AppInfo.TitleWithVersion} starting…");
        _log.Info($"Settings file: {_settings.SettingsPath}");

        var http = HttpClientFactory.Create();
        var factory = new ProviderFactory(http, _log);
        _server = new ProxyServer(_log, factory);
        _vm = new MainViewModel(_settings, _log, _server, factory);

        _window = new MainWindow(_vm);
        _window.HideRequested += () =>
        {
            // The window was closed to the tray: switch to the "still running in the
            // background" icon so it's obvious the app did not quit, then notify.
            _tray?.SetBackgroundMode(true);
            _tray?.ShowBalloon(
                "AI Cloud Proxy",
                "Still running in the system tray. Use the tray icon to reopen.",
                Forms.ToolTipIcon.Info,
                2500);
        };

        _tray = new TrayIcon(AppIcon.CreateTrayIcon(), AppIcon.CreateTrayBackgroundIcon());
        _tray.ShowRequested += () =>
        {
            // Restore the normal icon as soon as the window is reopened from the tray.
            _tray.SetBackgroundMode(false);
            _window.ShowAndFocus();
        };
        _tray.ToggleProxyRequested += () => _ = _vm.ToggleServerAsync();
        _tray.ExitRequested += ShutdownApp;

        _vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsRunning))
                _tray.SetProxyRunning(_vm.IsRunning);
        };

        MainWindow = _window;
        _window.Show();

        // First-run quick tour (shown only once; reopenable from the header button).
        if (!_settings.Settings.HasSeenTour)
        {
            _settings.Settings.HasSeenTour = true;
            _settings.Save();
            Dispatcher.BeginInvoke(() => QuickTourWindow.ShowDialog(_window));
        }

        if (_settings.Settings.AutoStartProxy)
        {
            Dispatcher.BeginInvoke(async () => await _vm.StartProxyAsync());
        }
    }

    private void ShutdownApp()
    {
        try { _ = _server.StopAsync(); } catch { }
        try { _settings.Save(); } catch { }
        _tray?.Dispose();
        _window.AllowClose = true;
        Shutdown();
    }
}
