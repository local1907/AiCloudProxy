using System.Windows.Media;

namespace AiCloudProxy.ViewModels;

public sealed class LogEntry
{
    public DateTime Timestamp { get; }
    public string Level { get; }
    public string Message { get; }

    public string TimeDisplay => Timestamp.ToString("HH:mm:ss");

    public System.Windows.Media.Brush LevelBrush => Level switch
    {
        "ERROR" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113)),
        "WARN" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36)),
        _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(52, 211, 153)),
    };

    public LogEntry(DateTime timestamp, string level, string message)
    {
        Timestamp = timestamp;
        Level = level;
        Message = message;
    }
}
