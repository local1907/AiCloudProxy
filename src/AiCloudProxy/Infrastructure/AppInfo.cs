using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

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

    /// <summary>GetCurrentPackageFullName error code meaning "not running from an MSIX package".</summary>
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    private static string ReadVersion()
    {
        // The Store-installed package identity is what the Store actually updates, so it wins
        // over assembly metadata that can lag behind after a Store update.
        var packageVersion = TryReadPackageVersion();
        if (packageVersion is not null) return packageVersion;

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

    /// <summary>
    /// Version reported by the MSIX package identity (e.g. "1.0.17.0"), or <c>null</c>
    /// when the app is not running from a package. The package full name is formatted as
    /// "Name_version_architecture_resourceId_publisherId".
    /// </summary>
    private static string? TryReadPackageVersion()
    {
        try
        {
            var length = 0;
            if (GetCurrentPackageFullName(ref length, null) == AppModelErrorNoPackage) return null;

            var fullName = new StringBuilder(length);
            if (GetCurrentPackageFullName(ref length, fullName) != 0) return null;

            var segments = fullName.ToString().Split('_');
            return segments.Length >= 2 && System.Version.TryParse(segments[1], out var version)
                ? version.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
