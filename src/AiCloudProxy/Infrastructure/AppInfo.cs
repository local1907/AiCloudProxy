using System.Reflection;

namespace AiCloudProxy.Infrastructure;

/// <summary>
/// Reads the version of the running build so it can be surfaced in the UI, the
/// tray tooltip and the log — the single source of truth is the &lt;Version&gt;
/// property in the project file. Reflection on the assembly (rather than
/// Assembly.Location) keeps this working in single-file published builds.
/// </summary>
public static class AppInfo
{
    /// <summary>Product version of the running build, e.g. "1.0.8.0".</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Version prefixed with "v", e.g. "v1.0.8.0".</summary>
    public static string VersionLabel => "v" + Version;

    /// <summary>App name with the running version, e.g. "AI Cloud Proxy v1.0.8.0".</summary>
    public static string TitleWithVersion => $"AI Cloud Proxy {VersionLabel}";

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK can append a source-revision suffix (e.g. "1.0.8.0+abc1234"); keep the version only.
            var plus = informational.IndexOf('+', System.StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
