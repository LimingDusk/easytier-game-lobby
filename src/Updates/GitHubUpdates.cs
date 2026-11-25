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

internal enum UpdatePhase { Downloading, Retrying, Verifying, Starting }

internal readonly record struct UpdateProgress(UpdatePhase Phase, int Percent = 0, int Retry = 0);

internal sealed class GitHubUpdates : IDisposable
{
    internal const string RepositoryUrl = "https://github.com/LimingDusk/easytier-game-lobby";
    internal const string AssetName = "GameLobby-win-x64.exe";
    internal const int DownloadRetries = 3;
    private const string ReleasesApi = "https://api.github.com/repos/LimingDusk/easytier-game-lobby/releases?per_page=30";
    private readonly HttpClient client;
    private readonly TimeSpan readIdleTimeout;

    public GitHubUpdates() : this(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    internal GitHubUpdates(HttpMessageHandler handler, TimeSpan? readIdleTimeout = null)
    {
        this.readIdleTimeout = readIdleTimeout ?? TimeSpan.FromSeconds(45);
        client = new(handler) { Timeout = TimeSpan.FromSeconds(40), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
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

    public async Task DownloadAsync(UpdatePackage package, Stream destination, IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!package.IsValid()) throw new InvalidDataException("更新包信息无效。");
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Length > package.Size) destination.SetLength(0);

        int retries = 0;
        var address = DownloadAddress(package.Tag);
        progress?.Report(new(UpdatePhase.Downloading, Percent(destination.Length, package.Size)));
        while (destination.Length < package.Size)
        {
            long offset = destination.Length;
            try
            {
                using var response = await OpenDownloadAsync(address, offset, cancellationToken).ConfigureAwait(false);
                if (DownloadRange(response, offset, package.Size) is not { } range)
                {
                    destination.SetLength(0);
                    progress?.Report(new(UpdatePhase.Downloading));
                    continue;
                }
                await CopyRangeAsync(response.Content, destination, range, package.Size, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retries >= DownloadRetries || !Retryable(error.StatusCode)) throw;
                retries++;
                progress?.Report(new(UpdatePhase.Retrying, Percent(destination.Length, package.Size), retries));
                await Task.Delay(TimeSpan.FromSeconds(retries), cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new(UpdatePhase.Verifying, 100));
        destination.Position = 0;
        var digest = await SHA256.HashDataAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(digest), package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            destination.SetLength(0);
            throw new InvalidDataException("更新包未通过 SHA-256 校验，重试时会重新下载。");
        }
    }

    private static (long Start, long End)? DownloadRange(HttpResponseMessage response, long offset, long size)
    {
        if (response.StatusCode == HttpStatusCode.OK) return (0, size);
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange is { Unit: var unit, From: long from, To: long to, Length: long total } &&
            string.Equals(unit, "bytes", StringComparison.OrdinalIgnoreCase) &&
            from == offset && to >= from && to < total && total == size)
            return (from, to + 1);
        if (offset > 0 && response.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable)
            return null;
        throw new InvalidDataException("GitHub 返回了无法验证的更新数据范围。");
    }

    private async Task CopyRangeAsync(HttpContent content, Stream destination, (long Start, long End) range,
        long size, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentEncoding.Any(value => !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("GitHub 返回了意外的更新包编码。");
        if (content.Headers.ContentLength is long length && length != range.End - range.Start)
            throw new InvalidDataException("更新包大小与发行记录不符。");

        if (range.Start == 0 && destination.Length != 0) destination.SetLength(0);
        destination.Position = range.Start;
        long copied = range.Start;
        int reported = Percent(copied, size);
        progress?.Report(new(UpdatePhase.Downloading, reported));

        var buffer = new byte[128 * 1024];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(readIdleTimeout);
        using var source = await ReadDownloadStreamAsync(content, idle.Token, cancellationToken).ConfigureAwait(false);
        while (copied < range.End)
        {
            idle.CancelAfter(readIdleTimeout);
            int read;
            try { read = await source.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false); }
            catch (IOException error) { throw new HttpRequestException("连接在下载时中断。", error); }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
            { throw new HttpRequestException("下载长时间未收到数据。", error); }
            finally { idle.CancelAfter(Timeout.InfiniteTimeSpan); }

            if (read == 0) throw new HttpRequestException("更新下载提前结束。");
            if (read > range.End - copied) throw new InvalidDataException("更新包大小超出发行记录。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            int percent = Percent(copied, size);
            if (percent != reported) progress?.Report(new(UpdatePhase.Downloading, reported = percent));
        }
        if (destination.Length != range.End) throw new IOException("无法保存已下载的更新数据。");
    }

    private static async Task<Stream> ReadDownloadStreamAsync(HttpContent content, CancellationToken idle,
        CancellationToken cancellationToken)
    {
        try { return await content.ReadAsStreamAsync(idle).ConfigureAwait(false); }
        catch (IOException error) { throw new HttpRequestException("无法读取更新下载。", error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("下载长时间未收到数据。", error); }
    }

    private static bool Retryable(HttpStatusCode? status) =>
        status is null or HttpStatusCode.RequestTimeout || (int)status.Value >= 500;

    private static int Percent(long length, long size) => (int)(length * 100 / size);

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri initial, long offset, CancellationToken token)
    {
        var address = initial;
        for (int i = 0; i < 5; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (address == initial) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (IOException error) { throw new HttpRequestException("连接 GitHub 下载时中断。", error); }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new HttpRequestException("连接 GitHub 下载超时。", error); }

            if (response.IsSuccessStatusCode || offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                return response;
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
        throw new InvalidDataException("更新下载跳转次数过多。");
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
