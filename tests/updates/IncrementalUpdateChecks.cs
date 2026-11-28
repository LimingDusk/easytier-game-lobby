using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GameLobby;

internal static class IncrementalUpdateChecks
{
    private const string Tag = "v0.4.1";
    private static readonly string executableUrl = $"{GitHubUpdates.RepositoryUrl}/releases/download/{Tag}/{GitHubUpdates.AssetName}";
    private static readonly string manifestUrl = $"{GitHubUpdates.RepositoryUrl}/releases/download/{Tag}/{GitHubUpdates.ManifestAssetName}";

    public static async Task RunAsync()
    {
        var random = new Random(417);
        byte[] previous = new byte[6 * 1024 * 1024];
        random.NextBytes(previous);
        const int insertionOffset = 1024 * 1024 + 129, insertionLength = 7613;
        byte[] next = new byte[previous.Length + insertionLength];
        previous.AsSpan(0, insertionOffset).CopyTo(next);
        random.NextBytes(next.AsSpan(insertionOffset, insertionLength));
        previous.AsSpan(insertionOffset).CopyTo(next.AsSpan(insertionOffset + insertionLength));
        random.NextBytes(next.AsSpan(next.Length - 16 * 1024));

        using var image = new MemoryStream(next, writable: false);
        var generated = UpdateBlockManifest.Create(image, Tag);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(generated);
        var release = new[]
        {
            new
            {
                tag_name = Tag,
                draft = false,
                prerelease = false,
                assets = new[]
                {
                    Asset(GitHubUpdates.AssetName, executableUrl, next),
                    Asset(GitHubUpdates.ManifestAssetName, manifestUrl, manifest)
                }
            }
        };
        using var catalog = new GitHubUpdates(new Reply(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(release))
        }));
        Check(ReleaseVersion.TryParse("0.4.0", out var current), "Fixture version is valid");
        var selected = await catalog.CheckAsync(current!, CancellationToken.None);
        if (selected is not { Manifest: not null })
            throw new InvalidOperationException("GitHub release selection should include its verified block manifest asset.");
        Check(UpdateBlockManifest.TryRead(manifest, selected) is not null &&
              UpdateBlockManifest.TryRead(manifest, selected with { Sha256 = new string('0', 64) }) is null,
            "Manifest round trip must validate against the target executable digest");

        var folder = Directory.CreateTempSubdirectory("GameLobby-block-update-");
        try
        {
            string baseExecutable = Path.Combine(folder.FullName, GitHubUpdates.AssetName);
            File.WriteAllBytes(baseExecutable, previous);
            int verifiedPrefix = await ReuseBlocksAsync(selected, next, manifest, baseExecutable, verifiedPrefix: 0);
            byte[] lowercaseManifest = JsonSerializer.SerializeToUtf8Bytes(generated with
            {
                Blocks = generated.Blocks.Select(block => block with { Sha256 = block.Sha256.ToLowerInvariant() }).ToArray()
            });
            var lowercasePackage = selected with { Manifest = new(lowercaseManifest.Length, Digest(lowercaseManifest)) };
            await ReuseBlocksAsync(lowercasePackage, next, lowercaseManifest, baseExecutable, verifiedPrefix);
            await FallBackAsync(selected, next, baseExecutable, () => new HttpResponseMessage(HttpStatusCode.NotFound));
            byte[] corrupted = (byte[])manifest.Clone();
            corrupted[0] ^= 1;
            await FallBackAsync(selected, next, baseExecutable, () => Full(corrupted));
            await FallBackFromCorruptRangeAsync(selected, next, manifest, baseExecutable);
        }
        finally { folder.Delete(recursive: true); }
        Console.WriteLine("Block manifests, shifted-block reuse, precise ranges, staged resume and whole-file fallback checks passed.");
    }

    private static async Task<int> ReuseBlocksAsync(UpdatePackage selected, byte[] next, byte[] manifest,
        string baseExecutable, int verifiedPrefix)
    {
        long transferred = 0;
        int manifestRequests = 0, firstRangeEnd = 0;
        using var download = new GitHubUpdates(new Reply(request =>
        {
            if (request.RequestUri?.AbsoluteUri == manifestUrl)
            {
                manifestRequests++;
                Check(request.Headers.Range is null, "Manifest is downloaded as a whole");
                return Full(manifest);
            }
            Check(request.RequestUri?.AbsoluteUri == executableUrl, "Incremental requests target the standalone release executable");
            var range = request.Headers.Range?.Ranges.Single();
            if (range is not { From: long first, To: long last } || first < 0 || last < first || last >= next.Length)
                throw new InvalidOperationException("Incremental downloads should request exact, bounded ranges.");
            Check(first >= verifiedPrefix, "A staged block with verified contents should not be downloaded again");
            int length = checked((int)(last - first + 1));
            if (firstRangeEnd == 0) firstRangeEnd = checked((int)last + 1);
            transferred += length;
            return Partial(new ByteArrayContent(next, (int)first, length), first, last, next.Length);
        }));

        using var destination = new MemoryStream();
        if (verifiedPrefix > 0)
        {
            destination.Write(next.AsSpan(0, verifiedPrefix));
            destination.Write(new byte[19]); // A previous transfer wrote an incomplete, unverified block.
        }
        var reports = new List<UpdateProgress>();
        await download.DownloadAsync(selected, destination, new InlineProgress(reports.Add), CancellationToken.None, baseExecutable);
        Check(manifestRequests == 1 && transferred > 0 && transferred < next.Length / 2 &&
              destination.ToArray().SequenceEqual(next),
            "Incremental downloads reuse shifted blocks and reconstruct exactly the published executable");
        Check(reports.FirstOrDefault(report => report.DownloadBytes.HasValue).DownloadBytes == transferred,
            "The incremental plan reports the actual number of bytes fetched from the release");
        return firstRangeEnd;
    }

    private static async Task FallBackAsync(UpdatePackage selected, byte[] next, string baseExecutable,
        Func<HttpResponseMessage> manifestResponse)
    {
        int manifestRequests = 0, fullRequests = 0;
        using var download = new GitHubUpdates(new Reply(request =>
        {
            if (request.RequestUri?.AbsoluteUri == manifestUrl)
            {
                manifestRequests++;
                return manifestResponse();
            }
            Check(request.RequestUri?.AbsoluteUri == executableUrl && request.Headers.Range is null,
                "An unavailable or corrupt manifest falls back to a whole executable response");
            fullRequests++;
            return Full(next);
        }));

        using var destination = new MemoryStream();
        await download.DownloadAsync(selected, destination, null, CancellationToken.None, baseExecutable);
        Check(manifestRequests == 1 && fullRequests == 1 && destination.ToArray().SequenceEqual(next),
            "The fallback restores the same verified standalone executable");
    }

    private static async Task FallBackFromCorruptRangeAsync(UpdatePackage selected, byte[] next, byte[] manifest,
        string baseExecutable)
    {
        long corruptStart = -1;
        int resumedRequests = 0;
        using var download = new GitHubUpdates(new Reply(request =>
        {
            if (request.RequestUri?.AbsoluteUri == manifestUrl) return Full(manifest);
            Check(request.RequestUri?.AbsoluteUri == executableUrl, "Range fallback downloads the release executable");
            var range = request.Headers.Range?.Ranges.Single();
            if (range is { From: long first, To: long last } && corruptStart < 0)
            {
                corruptStart = first;
                byte[] corrupt = next[(int)first..((int)last + 1)];
                corrupt[0] ^= 1;
                return Partial(new ByteArrayContent(corrupt), first, last, next.Length);
            }

            Check(corruptStart > 0 && range is { From: long resume, To: null } && resume == corruptStart,
                "Corrupt incremental bytes are rejected while the already verified prefix is retained");
            resumedRequests++;
            return Partial(new ByteArrayContent(next, (int)corruptStart, next.Length - (int)corruptStart),
                corruptStart, next.Length - 1, next.Length);
        }));

        using var destination = new MemoryStream();
        await download.DownloadAsync(selected, destination, null, CancellationToken.None, baseExecutable);
        Check(resumedRequests == 1 && destination.ToArray().SequenceEqual(next),
            "Ordinary resumed download reconstructs the release after a corrupt incremental range");
    }

    private static object Asset(string name, string url, byte[] data) => new
    {
        name,
        state = "uploaded",
        size = data.Length,
        digest = "sha256:" + Digest(data),
        browser_download_url = url
    };

    private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static HttpResponseMessage Full(byte[] data) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(data)
    };

    private static HttpResponseMessage Partial(HttpContent content, long first, long last, long size)
    {
        content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, size);
        return new(HttpStatusCode.PartialContent) { Content = content };
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
