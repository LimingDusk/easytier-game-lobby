using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLobby;

internal sealed record UpdateManifestAsset(long Size, string Sha256);

internal sealed record UpdatePackage(string Tag, long Size, string Sha256, UpdateManifestAsset? Manifest = null)
{
    internal const long MaximumSize = 512L * 1024 * 1024;
    internal static bool ValidDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal bool IsValid() => Size > 0 && Size <= MaximumSize && ValidDigest(Sha256) &&
        ReleaseVersion.TryParse(Tag, out _);
}

internal enum UpdatePhase { Downloading, Retrying, Verifying, Starting }

internal readonly record struct UpdateProgress(UpdatePhase Phase, int Percent = 0, int Retry = 0, long? DownloadBytes = null);

internal sealed class GitHubUpdates : IDisposable
{
    internal const string RepositoryUrl = "https://github.com/LimingDusk/easytier-game-lobby";
    internal const string AssetName = "GameLobby-win-x64.exe";
    internal const string ManifestAssetName = "GameLobby-win-x64.blocks.json";
    internal const int DownloadRetries = 3;
    private const int MaximumRangeSize = 8 * 1024 * 1024;
    private const string ReleasesApi = "https://api.github.com/repos/LimingDusk/easytier-game-lobby/releases?per_page=30";
    private readonly HttpClient client;
    private readonly TimeSpan readIdleTimeout;

    private sealed class BlockComparer : IEqualityComparer<UpdateBlock>
    {
        internal static readonly BlockComparer Instance = new();

        public bool Equals(UpdateBlock? first, UpdateBlock? second) => ReferenceEquals(first, second) ||
            first is not null && second is not null && first.Size == second.Size &&
            StringComparer.OrdinalIgnoreCase.Equals(first.Sha256, second.Sha256);

        public int GetHashCode(UpdateBlock block) =>
            HashCode.Combine(block.Size, StringComparer.OrdinalIgnoreCase.GetHashCode(block.Sha256));
    }

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

            if (FindAsset(assets, tag!, AssetName, UpdatePackage.MaximumSize) is not { } executable) continue;
            var manifest = FindAsset(assets, tag!, ManifestAssetName, UpdateBlockManifest.MaximumManifestSize);
            selected = new UpdatePackage(tag!, executable.Size, executable.Sha256,
                manifest is { } file ? new UpdateManifestAsset(file.Size, file.Sha256) : null);
            latest = version;
        }
        return selected;
    }

    private static (long Size, string Sha256)? FindAsset(JsonElement assets, string tag, string name, long maximumSize)
    {
        string expectedUrl = DownloadAddress(tag, name).AbsoluteUri;
        foreach (var asset in assets.EnumerateArray())
        {
            string? digest = StringProperty(asset, "digest");
            if (StringProperty(asset, "name") != name || StringProperty(asset, "state") != "uploaded" ||
                StringProperty(asset, "browser_download_url") != expectedUrl || digest is null ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || !UpdatePackage.ValidDigest(digest[7..]) ||
                !asset.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number ||
                !sizeValue.TryGetInt64(out long size) || size < 1 || size > maximumSize)
                continue;

            return (size, digest[7..].ToUpperInvariant());
        }
        return null;
    }

    public async Task DownloadAsync(UpdatePackage package, Stream destination, IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken, string? baseExecutable = null)
    {
        if (!package.IsValid()) throw new InvalidDataException("更新包信息无效。");
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.Length > package.Size) destination.SetLength(0);

        if (baseExecutable is not null && destination.Length < package.Size &&
            await TryDownloadBlocksAsync(package, destination, baseExecutable, progress, cancellationToken).ConfigureAwait(false) &&
            await VerifyDownloadAsync(package, destination, progress, cancellationToken).ConfigureAwait(false))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        await DownloadRemainderAsync(package, destination, progress, cancellationToken).ConfigureAwait(false);
        if (!await VerifyDownloadAsync(package, destination, progress, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("更新包未通过 SHA-256 校验，重试时会重新下载。");
    }

    private async Task DownloadRemainderAsync(UpdatePackage package, Stream destination, IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        int retries = 0;
        var address = DownloadAddress(package.Tag);
        progress?.Report(new(UpdatePhase.Downloading, Percent(destination.Length, package.Size)));
        while (destination.Length < package.Size)
        {
            long offset = destination.Length;
            try
            {
                var wanted = offset == 0 ? null : new RangeHeaderValue(offset, null);
                using var response = await OpenDownloadAsync(address, wanted, cancellationToken).ConfigureAwait(false);
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
    }

    private static async Task<bool> VerifyDownloadAsync(UpdatePackage package, Stream destination,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(UpdatePhase.Verifying, 100));
        destination.Position = 0;
        var digest = await SHA256.HashDataAsync(destination, cancellationToken).ConfigureAwait(false);
        if (destination.Length == package.Size &&
            string.Equals(Convert.ToHexString(digest), package.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
        destination.SetLength(0);
        return false;
    }

    private async Task<bool> TryDownloadBlocksAsync(UpdatePackage package, Stream destination, string baseExecutable,
        IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        if (package.Manifest is not { } manifestAsset || manifestAsset.Size is <= 0 or > UpdateBlockManifest.MaximumManifestSize ||
            !UpdatePackage.ValidDigest(manifestAsset.Sha256)) return false;

        using var baseFile = TryOpenBaseExecutable(baseExecutable);
        if (baseFile is null) return false;
        var manifest = await TryReadManifestAsync(package, manifestAsset, cancellationToken).ConfigureAwait(false);
        if (manifest is null) return false;
        var offsets = ScanBaseExecutable(baseFile, cancellationToken);
        if (offsets is null) return false;

        var buffer = new byte[UpdateBlockManifest.MaximumBlockSize];
        var blocks = manifest.Blocks;
        int index = await VerifyStagedPrefixAsync(destination, blocks, buffer, cancellationToken).ConfigureAwait(false);
        long downloadBytes = blocks.Skip(index).Where(block => !offsets.ContainsKey(block)).Sum(block => (long)block.Size);
        if (index < blocks.Count && downloadBytes == package.Size - destination.Length) return false;

        int reportedPercent = -1;
        long reportedBytes = -1;
        void ReportProgress()
        {
            int percent = Percent(destination.Length, package.Size);
            if (percent == reportedPercent && downloadBytes == reportedBytes) return;
            progress?.Report(new(UpdatePhase.Downloading, percent, DownloadBytes: downloadBytes));
            reportedPercent = percent;
            reportedBytes = downloadBytes;
        }
        ReportProgress();

        while (index < blocks.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = blocks[index];
            Memory<byte> data = buffer.AsMemory(0, block.Size);
            if (offsets.TryGetValue(block, out long offset))
            {
                if (await TryReadBaseBlockAsync(baseFile, offset, block, data, cancellationToken).ConfigureAwait(false))
                {
                    await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    ReportProgress();
                    index++;
                    continue;
                }
                offsets.Remove(block);
                downloadBytes += blocks.Skip(index).Where(candidate => offsets.Comparer.Equals(candidate, block))
                    .Sum(candidate => (long)candidate.Size);
                ReportProgress();
            }

            int end = index + 1;
            int rangeSize = block.Size;
            while (end < blocks.Count && rangeSize + blocks[end].Size <= MaximumRangeSize && !offsets.ContainsKey(blocks[end]))
                rangeSize += blocks[end++].Size;

            var missing = blocks.Skip(index).Take(end - index);
            if (!await TryDownloadBlockRangeAsync(package, destination, missing, rangeSize,
                buffer, ReportProgress, cancellationToken).ConfigureAwait(false)) return false;
            index = end;
        }
        return true;
    }

    private async Task<UpdateBlockManifest?> TryReadManifestAsync(UpdatePackage package, UpdateManifestAsset asset,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await OpenDownloadAsync(DownloadAddress(package.Tag, ManifestAssetName), null,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            ValidateContentHeaders(response.Content, asset.Size);

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(readIdleTimeout);
            using var source = await ReadDownloadStreamAsync(response.Content, idle.Token, cancellationToken).ConfigureAwait(false);
            byte[] json = new byte[(int)asset.Size];
            await ReadTransferExactlyAsync(source, json, idle, cancellationToken).ConfigureAwait(false);
            await VerifyTransferEndAsync(response.Content, source, idle, cancellationToken).ConfigureAwait(false);
            return MatchesHash(json, asset.Sha256) ? UpdateBlockManifest.TryRead(json, package) : null;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException) { return null; }
    }

    private static FileStream? TryOpenBaseExecutable(string path)
    {
        try { return File.OpenRead(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static Dictionary<UpdateBlock, long>? ScanBaseExecutable(FileStream baseFile, CancellationToken cancellationToken)
    {
        try
        {
            if (baseFile.Length is <= 0 or > UpdatePackage.MaximumSize) return null;
            var offsets = new Dictionary<UpdateBlock, long>(BlockComparer.Instance);
            long offset = 0;
            foreach (var block in UpdateBlockManifest.Scan(baseFile, cancellationToken))
            {
                offsets.TryAdd(block, offset);
                offset += block.Size;
            }
            return offsets;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<int> VerifyStagedPrefixAsync(Stream destination, IReadOnlyList<UpdateBlock> blocks,
        byte[] buffer, CancellationToken cancellationToken)
    {
        destination.Position = 0;
        long verified = 0;
        int index = 0;
        while (index < blocks.Count && blocks[index].Size <= destination.Length - verified)
        {
            var block = blocks[index];
            Memory<byte> data = buffer.AsMemory(0, block.Size);
            await destination.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            if (!MatchesHash(data.Span, block.Sha256)) break;
            verified += block.Size;
            index++;
        }
        if (destination.Length != verified) destination.SetLength(verified);
        destination.Position = verified;
        return index;
    }

    private static async Task<bool> TryReadBaseBlockAsync(FileStream baseFile, long offset, UpdateBlock block,
        Memory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            baseFile.Position = offset;
            await baseFile.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            return MatchesHash(data.Span, block.Sha256);
        }
        catch (IOException) { return false; }
    }

    private async Task<bool> TryDownloadBlockRangeAsync(UpdatePackage package, Stream destination,
        IEnumerable<UpdateBlock> blocks, int length, byte[] buffer, Action reportProgress,
        CancellationToken cancellationToken)
    {
        long start = destination.Length;
        long limit = start + length;
        try
        {
            var wanted = new RangeHeaderValue(start, limit - 1);
            using var response = await OpenDownloadAsync(DownloadAddress(package.Tag), wanted, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                DownloadRange(response, start, package.Size) is not { } actual || actual != (start, limit)) return false;
            ValidateContentHeaders(response.Content, length);

            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(readIdleTimeout);
            using var source = await ReadDownloadStreamAsync(response.Content, idle.Token, cancellationToken).ConfigureAwait(false);
            foreach (var block in blocks)
            {
                Memory<byte> data = buffer.AsMemory(0, block.Size);
                await ReadTransferExactlyAsync(source, data, idle, cancellationToken).ConfigureAwait(false);
                if (!MatchesHash(data.Span, block.Sha256)) return false;
                await destination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                reportProgress();
            }
            await VerifyTransferEndAsync(response.Content, source, idle, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException) { return false; }
    }

    private static bool MatchesHash(ReadOnlySpan<byte> data, string digest) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(data)), digest, StringComparison.OrdinalIgnoreCase);

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
        ValidateContentHeaders(content, range.End - range.Start);

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
            int read = await ReadTransferAsync(source, buffer, idle, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new HttpRequestException("更新下载提前结束。");
            if (read > range.End - copied) throw new InvalidDataException("更新包大小超出发行记录。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            int percent = Percent(copied, size);
            if (percent != reported) progress?.Report(new(UpdatePhase.Downloading, reported = percent));
        }
        if (destination.Length != range.End) throw new IOException("无法保存已下载的更新数据。");
    }

    private static void ValidateContentHeaders(HttpContent content, long size)
    {
        if (content.Headers.ContentEncoding.Any(value => !string.Equals(value, "identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("GitHub 返回了意外的更新包编码。");
        if (content.Headers.ContentLength is long length && length != size)
            throw new InvalidDataException("更新包大小与发行记录不符。");
    }

    private async Task ReadTransferExactlyAsync(Stream source, Memory<byte> buffer, CancellationTokenSource idle,
        CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            int read = await ReadTransferAsync(source, buffer, idle, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new HttpRequestException("更新下载提前结束。");
            buffer = buffer[read..];
        }
    }

    private async Task VerifyTransferEndAsync(HttpContent content, Stream source, CancellationTokenSource idle,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is null &&
            await ReadTransferAsync(source, new byte[1], idle, cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("更新包大小超出发行记录。");
    }

    private async Task<int> ReadTransferAsync(Stream source, Memory<byte> buffer, CancellationTokenSource idle,
        CancellationToken cancellationToken)
    {
        idle.CancelAfter(readIdleTimeout);
        try { return await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
        catch (IOException error) { throw new HttpRequestException("连接在下载时中断。", error); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("下载长时间未收到数据。", error); }
        finally { idle.CancelAfter(Timeout.InfiniteTimeSpan); }
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

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri initial, RangeHeaderValue? range, CancellationToken token)
    {
        var address = initial;
        for (int i = 0; i < 5; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (address == initial) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (range is not null) request.Headers.Range = range;
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (IOException error) { throw new HttpRequestException("连接 GitHub 下载时中断。", error); }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            { throw new HttpRequestException("连接 GitHub 下载超时。", error); }

            if (response.IsSuccessStatusCode || range is not null && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
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

    private static Uri DownloadAddress(string tag, string name = AssetName) =>
        new($"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tag)}/{name}");

    private static string? StringProperty(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool BooleanProperty(JsonElement item, string name, bool expected) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    public void Dispose() => client.Dispose();
}
