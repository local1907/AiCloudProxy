using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows;

namespace AiCloudProxy.Infrastructure;

/// <summary>Provides the application icon, loaded from the embedded Assets\icon.ico resource.</summary>
public static class AppIcon
{
    private const string IconUri = "pack://application:,,,/Assets/icon.ico";

    /// <summary>Returns the system tray icon, preferring the embedded icon.ico.</summary>
    public static Icon CreateTrayIcon()
    {
        // Prefer the embedded icon.ico (same artwork as the taskbar / title bar).
        if (TryLoadEmbeddedIcon() is { } embedded)
            return embedded;

        // If the WPF resource can't be read (e.g. unusual host), pull the icon
        // straight out of our own EXE — also Assets\icon.ico via <ApplicationIcon> —
        // so the tray always shows the REAL app icon, never an unrelated placeholder.
        if (TryExtractExeIcon() is { } exeIcon)
            return exeIcon;

        // Absolute last resort (cannot normally happen): a generic fallback.
        return RenderFallbackIcon();
    }

    /// <summary>Reads the icon embedded in the currently running executable (the app icon).</summary>
    private static Icon? TryExtractExeIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            return !string.IsNullOrEmpty(path) && File.Exists(path)
                ? System.Drawing.Icon.ExtractAssociatedIcon(path)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the tray icon shown while the main window is hidden to the system tray:
    /// the normal artwork, muted and with a green "live" dot, so it clearly differs from
    /// the standard icon and signals the app is still running in the background.
    /// </summary>
    public static Icon CreateTrayBackgroundIcon()
    {
        try
        {
            using var source = CreateTrayIcon();
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.DrawIcon(source, new Rectangle(0, 0, 32, 32));

                // Fade the vivid artwork so it reads as "running in the background".
                using var shade = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
                g.FillRectangle(shade, 0, 0, 32, 32);

                // Small green dot = the app is still alive in the tray.
                var dot = new Rectangle(21, 21, 11, 11);
                using var dotBrush = new SolidBrush(Color.FromArgb(34, 197, 94));
                using var ringPen = new Pen(Color.White, 2f);
                g.FillEllipse(dotBrush, dot);
                g.DrawEllipse(ringPen, dot);
            }

            return CloneIconFrom(bmp);
        }
        catch
        {
            // Fall back to the normal tray icon if rendering fails.
            return CreateTrayIcon();
        }
    }

    private static Icon? TryLoadEmbeddedIcon()
    {
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(new Uri(IconUri))?.Stream;
            return stream != null ? new Icon(stream) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Icon RenderFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new LinearGradientBrush(
                new Rectangle(0, 0, 32, 32),
                Color.FromArgb(96, 165, 250),
                Color.FromArgb(37, 99, 235),
                45f);
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var pen = new Pen(Color.FromArgb(255, 255, 255, 210), 1.5f);
            g.DrawEllipse(pen, 1, 1, 30, 30);

            using var font = new Font("Segoe UI", 14f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.White);
            var size = g.MeasureString("AI", font);
            g.DrawString("AI", font, brush, (32 - size.Width) / 2, (32 - size.Height) / 2 - 1);
        }

        return CloneIconFrom(bmp);
    }

    /// <summary>Creates an independent Icon that owns its pixel data, so the source bitmap can be disposed safely.</summary>
    private static Icon CloneIconFrom(Bitmap bmp)
    {
        // FromHandle does not own the handle; Clone copies the pixels into a new
        // independent icon, so disposing the temporary is safe either way.
        using var temp = Icon.FromHandle(bmp.GetHicon());
        return (Icon)temp.Clone();
    }
}
