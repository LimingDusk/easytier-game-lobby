using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLobby;

internal sealed record UpdatePackage(string Tag, long Size, string Sha256)
{
    internal const long MaximumSize = 512L * 1024 * 1024;
    internal static bool ValidDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal bool IsValid() => Size > 0 && Size <= MaximumSize && ValidDigest(Sha256) &&
        ReleaseVersion.TryParse(Tag, out _);
}

internal sealed class GitHubUpdates : IDisposable
{
    internal const string RepositoryUrl = "https://github.com/LimingDusk/easytier-game-lobby";
    internal const string AssetName = "GameLobby-win-x64.exe";
    private const string ReleasesApi = "https://api.github.com/repos/LimingDusk/easytier-game-lobby/releases?per_page=30";
    private readonly HttpClient client;

    public GitHubUpdates() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    internal GitHubUpdates(HttpMessageHandler handler)
    {
        client = new(handler) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EasyTierGameLobby");
    }

    public async Task<UpdatePackage?> CheckAsync(ReleaseVersion current, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub 返回了无法读取的发行列表。");

        var latest = current;
        UpdatePackage? selected = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            string? tag = StringProperty(release, "tag_name");
            if (!ReleaseVersion.TryParse(tag, out var version) || version.CompareTo(latest) <= 0 ||
                !BooleanProperty(release, "draft", false) ||
                (!current.IsPrerelease && (version.IsPrerelease || !BooleanProperty(release, "prerelease", false))) ||
                !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var asset in assets.EnumerateArray())
            {
                string? digest = StringProperty(asset, "digest");
                if (StringProperty(asset, "name") != AssetName || StringProperty(asset, "state") != "uploaded" ||
                    StringProperty(asset, "browser_download_url") != DownloadAddress(tag!).AbsoluteUri ||
                    digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
                    !sizeValue.TryGetInt64(out long size))
                    continue;

                var package = new UpdatePackage(tag!, size, digest[7..].ToUpperInvariant());
                if (!package.IsValid()) continue;
                selected = package;
                latest = version;
                break;
            }
        }
        return selected;
    }

    public async Task DownloadAsync(UpdatePackage package, Stream destination, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!package.IsValid()) throw new InvalidDataException("更新包信息无效。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        using var response = await OpenDownloadAsync(DownloadAddress(package.Tag), token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length != package.Size)
            throw new InvalidDataException("更新包大小与发行记录不符。");

        using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long lengthRead = 0;
        int reported = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            lengthRead += read;
            if (lengthRead > package.Size) throw new InvalidDataException("更新包大小超出发行记录。");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            int percent = (int)(lengthRead * 100 / package.Size);
            if (progress != null && percent != reported) progress.Report(reported = percent);
        }
        if (lengthRead != package.Size ||
            !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包未通过大小和 SHA-256 校验。");
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri initial, CancellationToken token)
    {
        var address = initial;
        for (int i = 0; i < 5; i++)
        {
            var response = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                    HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    response.EnsureSuccessStatusCode();
                var location = response.Headers.Location;
                if (location is null || !Uri.TryCreate(address, location, out var next) ||
                    next.Scheme != Uri.UriSchemeHttps || !next.IsDefaultPort || next.UserInfo.Length != 0 ||
                    (next != initial && next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com")))
                    throw new InvalidDataException("更新下载跳转到了无法验证的地址。");
                address = next;
            }
        }
        throw new HttpRequestException("更新下载跳转次数过多。");
    }

    private static Uri DownloadAddress(string tag) =>
        new($"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{AssetName}");

    private static string? StringProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool BooleanProperty(JsonElement item, string name, bool expected) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    public void Dispose() => client.Dispose();
}
