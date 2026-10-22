using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AiCloudProxy.Services;

/// <summary>A published release discovered by <see cref="UpdateService"/>.</summary>
public sealed record UpdateInfo(string Version, string ReleaseUrl);

/// <summary>
/// Outcome of one update check. Network/API problems are reported here instead of
/// being thrown, so an offline machine never disturbs startup.
/// <para>
/// <see cref="Latest"/> carries the version currently published in the Microsoft Store
/// (the only distribution channel). <see cref="PendingVersion"/> is a newer GitHub tag
/// that has not reached the Store yet: it is reported, but never triggers the banner.
/// </para>
/// </summary>
public sealed record UpdateCheckResult(
    bool Succeeded,
    UpdateInfo? Latest,
    bool UpdateAvailable,
    string? Error,
    string? PendingVersion = null);

/// <summary>
/// Checks the Microsoft Store for the newest published version and GitHub for the
/// matching release notes. Updates ship exclusively through the Store, so a GitHub tag
/// that is ahead of the Store listing is reported as pending instead of offering an
/// update the user cannot install yet.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/local1907/AiCloudProxy";

    /// <summary>GitHub REST endpoint returning the newest non-prerelease release (release notes).</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/local1907/AiCloudProxy/releases/latest";

    /// <summary>Microsoft Store listing, where every update is installed from.</summary>
    public const string StorePageUrl = "https://apps.microsoft.com/detail/9NBB09H4P1MK";

    /// <summary>Product id used by the Store catalog API.</summary>
    public const string StoreProductId = "9NBB09H4P1MK";

    /// <summary>Store catalog endpoint reporting the packages published for the product.</summary>
    public const string StoreCatalogApi =
        "https://displaycatalog.mp.microsoft.com/v7.0/products/{0}?market=US&languages=en-US&fieldsTemplate=Details";

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
        return client;
    }

    /// <summary>
    /// Reads the Store's published version together with the newest GitHub release and
    /// compares them with <paramref name="currentVersion"/>. An update is offered only
    /// when the Store already carries it; a newer GitHub tag is returned as
    /// <see cref="UpdateCheckResult.PendingVersion"/>.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            var (storeVersion, storeError) = await FetchStoreVersionAsync(ct).ConfigureAwait(false);
            var (release, releaseError) = await FetchLatestReleaseAsync(ct).ConfigureAwait(false);

            if (storeVersion is null && release is null)
                return new UpdateCheckResult(false, null, false, storeError ?? releaseError ?? "Update check failed.");

            var latestVersion = storeVersion ?? release!.Version;
            var latest = new UpdateInfo(latestVersion, release?.ReleaseUrl ?? $"{RepoUrl}/releases");

            // A GitHub tag that the Store has not published yet must not nag the user:
            // it becomes actionable only after the Store listing catches up.
            var pending = release is not null && IsNewer(latestVersion, release.Version)
                ? release.Version
                : null;

            return new UpdateCheckResult(
                true,
                latest,
                IsNewer(currentVersion, latestVersion),
                null,
                pending);
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

    private async Task<(string? Version, string? Error)> FetchStoreVersionAsync(CancellationToken ct)
    {
        try
        {
            var url = string.Format(StoreCatalogApi, StoreProductId);
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"Microsoft Store returned HTTP {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var version = ParseStoreVersion(json);
            return version is null
                ? (null, "The Microsoft Store catalog did not report a package version.")
                : (version, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private async Task<(UpdateInfo? Release, string? Error)> FetchLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub returned HTTP {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = ParseRelease(json);
            return release is null
                ? (null, "The GitHub response did not contain a release tag.")
                : (release, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Reads the newest package version from a Store catalog response. Store catalog
    /// documents list each package as "Name_version_architecture_resourceId_publisherId",
    /// so the second segment is the version the Store would install.
    /// Returns <c>null</c> when no version can be found.
    /// </summary>
    public static string? ParseStoreVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            Version? newest = null;

            void Visit(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            if (property.NameEquals("PackageFullName") &&
                                property.Value.ValueKind == JsonValueKind.String &&
                                TryParsePackageVersion(property.Value.GetString(), out var parsed) &&
                                (newest is null || parsed > newest))
                            {
                                newest = parsed;
                            }
                            else
                            {
                                Visit(property.Value);
                            }
                        }
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray()) Visit(item);
                        break;
                }
            }

            Visit(doc.RootElement);
            return newest?.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParsePackageVersion(string? packageFullName, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(packageFullName)) return false;

        var segments = packageFullName.Split('_');
        return segments.Length >= 2 && Version.TryParse(segments[1], out version!);
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
            return new UpdateInfo(version, releaseUrl);
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

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
