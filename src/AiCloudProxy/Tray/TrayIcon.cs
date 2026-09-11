using System.Drawing;
using System.Windows.Forms;
using AiCloudProxy.Infrastructure;

namespace AiCloudProxy.Tray;

/// <summary>System tray icon with a context menu (open, start/stop proxy, exit).</summary>
public class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly Icon _normalIcon;
    private readonly Icon _backgroundIcon;
    private readonly System.Windows.Forms.Timer _balloonRestoreTimer;
    private Icon? _iconBeforeBalloon;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ToggleProxyRequested;
    public event Action? ExitRequested;

    public TrayIcon(Icon icon, Icon backgroundIcon)
    {
        _normalIcon = icon;
        _backgroundIcon = backgroundIcon;

        _toggleItem = new ToolStripMenuItem("Start Proxy");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open AI Cloud Proxy", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _toggleItem.Click += (_, _) => ToggleProxyRequested?.Invoke();

        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = $"{AppInfo.TitleWithVersion} — Stopped",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();

        // Windows takes the balloon's header icon from NotifyIcon.Icon while it
        // renders the balloon. We briefly switch to the full-colour app icon so
        // the balloon shows the real artwork, then restore the current mode's
        // icon (normal vs. dimmed "still running") a moment later.
        _balloonRestoreTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _balloonRestoreTimer.Tick += (_, _) =>
        {
            _balloonRestoreTimer.Stop();
            if (!_disposed && _iconBeforeBalloon is not null)
                _icon.Icon = _iconBeforeBalloon;
            _iconBeforeBalloon = null;
        };
    }

    /// <summary>
    /// Swaps the tray icon between the normal artwork (window visible) and the
    /// "running in the background" variant (window hidden to the system tray).
    /// </summary>
    public void SetBackgroundMode(bool background)
    {
        _icon.Icon = background ? _backgroundIcon : _normalIcon;
    }

    public void SetProxyRunning(bool running)
    {
        _toggleItem.Text = running ? "Stop Proxy" : "Start Proxy";
        _icon.Text = running
            ? $"{AppInfo.TitleWithVersion} — Running"
            : $"{AppInfo.TitleWithVersion} — Stopped";
    }

    public void ShowBalloon(string title, string text, ToolTipIcon tip, int timeoutMs)
    {
        _balloonRestoreTimer.Stop();
        _iconBeforeBalloon = _icon.Icon;
        _icon.Icon = _normalIcon; // balloon always shows the real app icon
        _icon.ShowBalloonTip(timeoutMs, title, text, tip);
        if (!ReferenceEquals(_iconBeforeBalloon, _normalIcon))
            _balloonRestoreTimer.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _balloonRestoreTimer.Stop();
        _balloonRestoreTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
