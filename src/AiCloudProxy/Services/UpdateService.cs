using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AiCloudProxy.Services;

/// <summary>A published release discovered by <see cref="UpdateService"/>.</summary>
public sealed record UpdateInfo(string Version, string ReleaseUrl, string? DownloadUrl);

/// <summary>
/// Outcome of one update check. Network/API problems are reported here instead of
/// being thrown, so an offline machine never disturbs startup.
/// </summary>
public sealed record UpdateCheckResult(bool Succeeded, UpdateInfo? Latest, bool UpdateAvailable, string? Error);

/// <summary>
/// Checks GitHub for a newer published release. The app runs this once at startup
/// (and on demand from the Options tab) so users who installed the standalone exe
/// learn about updates instead of silently running an old build.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/local1907/AiCloudProxy";

    /// <summary>GitHub REST endpoint returning the newest non-prerelease release.</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/local1907/AiCloudProxy/releases/latest";

    /// <summary>Microsoft Store listing, used by Store-installed (MSIX) builds.</summary>
    public const string StorePageUrl = "https://apps.microsoft.com/detail/9NBB09H4P1MK";

    /// <summary>GetCurrentPackageFullName error code meaning "not running from an MSIX package".</summary>
    private const int AppModelErrorNoPackage = 15700;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AICloudProxy-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// True when the app runs from an MSIX package (Microsoft Store build), where
    /// updates arrive through the Store rather than a downloaded exe.
    /// </summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                var length = 0;
                return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
            }
            catch
            {
                return false;
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);

    /// <summary>
    /// Fetches the latest release and compares it with <paramref name="currentVersion"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApi, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, null, false, $"GitHub returned HTTP {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var latest = ParseRelease(json);
            if (latest is null)
                return new UpdateCheckResult(false, null, false, "The GitHub response did not contain a release tag.");

            return new UpdateCheckResult(true, latest, IsNewer(currentVersion, latest.Version), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, false, ex.Message);
        }
    }

    /// <summary>
    /// Parses a GitHub "latest release" JSON document. Returns <c>null</c> when the
    /// document has no usable tag.
    /// </summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var version = NormalizeVersion(tag);
            if (version.Length == 0) return null;

            var releaseUrl = GetString(root, "html_url") ?? $"{RepoUrl}/releases/tag/{tag.Trim()}";
            return new UpdateInfo(version, releaseUrl, FindExeAsset(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="candidateVersion"/> is a newer build than
    /// <paramref name="currentVersion"/>. Tags may carry a leading "v", and build
    /// metadata ("+sha") or prerelease suffixes are ignored. Unparsable versions
    /// never report an update, which keeps a malformed tag from nagging users.
    /// </summary>
    public static bool IsNewer(string? currentVersion, string? candidateVersion)
    {
        var current = NormalizeVersion(currentVersion);
        var candidate = NormalizeVersion(candidateVersion);

        return Version.TryParse(current, out var currentParsed) &&
               Version.TryParse(candidate, out var candidateParsed) &&
               candidateParsed > currentParsed;
    }

    /// <summary>Strips "v", build metadata (after "+") and prerelease suffixes (after "-").</summary>
    public static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";

        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];

        var plus = value.IndexOf('+');
        if (plus > 0) value = value[..plus];

        var dash = value.IndexOf('-');
        if (dash > 0) value = value[..dash];

        return value.Trim();
    }

    /// <summary>Prefers the release's AICloudProxy.exe asset, falling back to any .exe.</summary>
    private static string? FindExeAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? firstExe = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var url = GetString(asset, "browser_download_url");
            var name = GetString(asset, "name") ?? "";
            if (string.IsNullOrWhiteSpace(url)) continue;

            if (name.Equals("AICloudProxy.exe", StringComparison.OrdinalIgnoreCase)) return url;
            if (firstExe is null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) firstExe = url;
        }
        return firstExe;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
