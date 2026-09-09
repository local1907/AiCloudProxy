using AiCloudProxy.ViewModels;

namespace AiCloudProxy.Services;

/// <summary>Thread-safe event-based logger. Consumers subscribe to <see cref="EntryAdded"/>.</summary>
public class LogService
{
    public event Action<LogEntry>? EntryAdded;

    public void Info(string message) => Add("INFO", message);

    public void Warn(string message) => Add("WARN", message);

    public void Error(string message, Exception? ex = null)
    {
        var text = ex is null ? message : $"{message} — {ex.Message}";
        Add("ERROR", text);
    }

    private void Add(string level, string message)
    {
        try
        {
            EntryAdded?.Invoke(new LogEntry(DateTime.Now, level, message));
        }
        catch
        {
            // A broken subscriber must not crash the logging pipeline.
        }
    }
}
