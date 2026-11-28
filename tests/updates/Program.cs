using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GameLobby;

static ReleaseVersion ParseVersion(string text) => ReleaseVersion.TryParse(text, out var version)
    ? version : throw new InvalidOperationException($"Expected a valid version: {text}");

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}

static async Task ExpectInvalid(Func<Task> action)
{
    try { await action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected an invalid update response to be rejected.");
}

Check(ParseVersion("v0.2.0-alpha.10").CompareTo(ParseVersion("0.2.0-alpha.9")) > 0, "Numeric prerelease ordering");
Check(ParseVersion("0.2.0+build.2").CompareTo(ParseVersion("v0.2.0")) == 0, "Build metadata precedence");
Check(ParseVersion("0.2.0").CompareTo(ParseVersion("0.2.0-rc.1")) > 0, "Stable release precedence");
Check(!ReleaseVersion.TryParse("v0.2.0-alpha.01", out _) &&
      !ReleaseVersion.TryParse("v0.2.0/../../test", out _), "Invalid version and path characters");

var payload = new byte[] { 1, 12, 34, 78 };
string sha = Convert.ToHexString(SHA256.HashData(payload));
object Asset(string tag, string? digest, string? url = null) => new
{
    name = GitHubUpdates.AssetName,
    state = "uploaded",
    size = payload.Length,
    digest,
    browser_download_url = url ?? $"{GitHubUpdates.RepositoryUrl}/releases/download/{tag}/{GitHubUpdates.AssetName}"
};
object Release(string tag, bool prerelease, object[] assets, bool draft = false) =>
    new { tag_name = tag, prerelease, draft, assets };
var releases = new[]
{
    Release("v0.2.0-alpha.9", true, [Asset("v0.2.0-alpha.9", "sha256:" + sha)]),
    Release("v0.2.0-alpha.99", true, [Asset("v0.2.0-alpha.99", "sha256:" + sha)], draft: true),
    Release("v0.2.0-alpha.21", true, [Asset("v0.2.0-alpha.21", null)]),
    Release("v0.2.0-alpha.22", true, [Asset("v0.2.0-alpha.22", "sha256:" + sha, "https://example.org/file.exe")]),
    Release("v0.2.0-alpha.10", true, [Asset("v0.2.0-alpha.10", "sha256:" + sha)]),
    Release("v0.1.9", false, [Asset("v0.1.9", "sha256:" + sha)])
};
using var checks = new GitHubUpdates(new Reply(request =>
{
    Check(request.RequestUri?.AbsoluteUri ==
          "https://api.github.com/repos/LimingDusk/easytier-game-lobby/releases?per_page=30", "Repository API origin");
    Check(request.Headers.UserAgent.Any(), "GitHub user agent");
    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases)) };
}));
var package = await checks.CheckAsync(ParseVersion("0.2.0-alpha.8"), CancellationToken.None);
Check(package?.Tag == "v0.2.0-alpha.10", "Newest eligible intact release");
Check(await checks.CheckAsync(ParseVersion("0.1.9"), CancellationToken.None) is null, "Stable channel stays stable");
Check(await checks.CheckAsync(ParseVersion("0.2.0-alpha.10"), CancellationToken.None) is null, "No downgrade or reinstall");

var target = package!;
HttpResponseMessage Full(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
HttpResponseMessage Part(int first, int last)
{
    var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(payload[first..(last + 1)]) };
    response.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, payload.Length);
    return response;
}
HttpResponseMessage Interrupted()
{
    var response = Full(payload[..2]);
    response.Content.Headers.ContentLength = payload.Length;
    return response;
}

int followed = 0;
using var downloader = new GitHubUpdates(new Reply(request =>
{
    followed++;
    if (request.RequestUri?.Host == "github.com")
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new("https://release-assets.githubusercontent.com/test");
        return response;
    }
    Check(request.RequestUri?.Host == "release-assets.githubusercontent.com", "Recognized download host");
    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
}));
using var destination = new MemoryStream();
await downloader.DownloadAsync(target, destination, null, CancellationToken.None);
Check(followed == 2 && destination.ToArray().SequenceEqual(payload), "Verified download through release CDN");

int origins = 0, transfers = 0;
using var reconnect = new GitHubUpdates(new Reply(request =>
{
    string? range = request.Headers.Range?.ToString();
    Check(range == (transfers == 0 ? null : "bytes=2-"), "A retry requests only the missing bytes");
    if (request.RequestUri?.Host == "github.com")
    {
        origins++;
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new($"https://release-assets.githubusercontent.com/test/{origins}");
        return redirect;
    }
    return ++transfers == 1 ? Interrupted() : Part(2, 3);
}));
using var resumed = new MemoryStream();
var reports = new List<UpdateProgress>();
await reconnect.DownloadAsync(target, resumed, new InlineProgress(reports.Add), CancellationToken.None);
Check(origins == 2 && transfers == 2 && resumed.ToArray().SequenceEqual(payload) &&
      reports.Contains(new(UpdatePhase.Retrying, 50, 1)), "An interrupted transfer retains progress and fetches a fresh redirect");

using var paused = new CancellationTokenSource();
using var shortDownload = new GitHubUpdates(new Reply(_ => Interrupted()));
string partialPath = Path.Combine(Path.GetTempPath(), $"GameLobby-update-check-{Guid.NewGuid():N}.download");
try
{
    using (var partial = File.Open(partialPath, FileMode.CreateNew))
    {
        try
        {
            await shortDownload.DownloadAsync(target, partial, new InlineProgress(progress =>
            {
                if (progress is { Phase: UpdatePhase.Downloading, Percent: 50 }) paused.Cancel();
            }), paused.Token);
            throw new InvalidOperationException("Pausing a partial download was ignored.");
        }
        catch (OperationCanceledException) when (paused.IsCancellationRequested) { }
        Check(partial.Length == 2, "Pausing keeps the downloaded prefix");
    }
    using var segmented = new GitHubUpdates(new Reply(request => request.Headers.Range?.ToString() switch
    {
        "bytes=2-" => Part(2, 2),
        "bytes=3-" => Part(3, 3),
        _ => throw new InvalidOperationException("The saved download was not resumed.")
    }));
    using var reopened = File.Open(partialPath, FileMode.Open);
    await segmented.DownloadAsync(target, reopened, null, CancellationToken.None);
    Check(reopened.Length == payload.Length, "A saved file resumes after reopening and accepts multiple valid ranges");
}
finally { File.Delete(partialPath); }

using var stalledStream = new StalledStream(payload[..2]);
int stalledRequests = 0;
using var stalled = new GitHubUpdates(new Reply(request =>
{
    if (++stalledRequests == 1)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalledStream) };
        response.Content.Headers.ContentLength = payload.Length;
        return response;
    }
    Check(request.Headers.Range?.ToString() == "bytes=2-", "Idle timeouts keep the completed range");
    return Part(2, 3);
}), TimeSpan.FromMilliseconds(100));
using var timedOut = new MemoryStream();
await stalled.DownloadAsync(target, timedOut, null, CancellationToken.None);
Check(stalledStream.TimedOut && stalledRequests == 2 && timedOut.ToArray().SequenceEqual(payload),
      "A stalled response is cancelled and resumed");

using var fallback = new MemoryStream();
fallback.Write([0, 0]);
using var ignoresRange = new GitHubUpdates(new Reply(request =>
{
    Check(request.Headers.Range?.ToString() == "bytes=2-", "Existing data is offered for resume");
    return Full(payload);
}));
await ignoresRange.DownloadAsync(target, fallback, null, CancellationToken.None);
Check(fallback.ToArray().SequenceEqual(payload), "A full response safely replaces a partial file");

int rejectedRanges = 0;
using var rejectsRange = new GitHubUpdates(new Reply(request =>
{
    if (++rejectedRanges == 1)
    {
        Check(request.Headers.Range?.ToString() == "bytes=2-", "Range rejection starts with a partial file");
        return Part(1, 3);
    }
    Check(request.Headers.Range is null, "A mismatched Content-Range is discarded before a fresh request");
    return Full(payload);
}));
fallback.SetLength(2);
await rejectsRange.DownloadAsync(target, fallback, null, CancellationToken.None);
Check(rejectedRanges == 2 && fallback.ToArray().SequenceEqual(payload), "Invalid ranges are never appended");

using var corrupt = new GitHubUpdates(new Reply(_ =>
    new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 0, 0]) }));
using var invalid = new MemoryStream();
await ExpectInvalid(() => corrupt.DownloadAsync(target, invalid, null, CancellationToken.None));
Check(invalid.Length == 0, "Failed SHA-256 verification discards the invalid data");
await ExpectInvalid(() => corrupt.DownloadAsync(target with { Size = 5 }, invalid, null, CancellationToken.None));

int requests = 0;
using var unsafeRedirect = new GitHubUpdates(new Reply(_ =>
{
    requests++;
    var response = new HttpResponseMessage(HttpStatusCode.Found);
    response.Headers.Location = new("https://example.org/standalone.exe");
    return response;
}));
await ExpectInvalid(() => unsafeRedirect.DownloadAsync(target, invalid, null, CancellationToken.None));
Check(requests == 1, "Unverified redirects are not followed");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await downloader.DownloadAsync(target, invalid, null, cancelled.Token);
    throw new InvalidOperationException("Cancellation ignored");
}
catch (OperationCanceledException) { }

Console.WriteLine("Update version, channel, redirects, interrupted downloads, saved progress, idle timeouts and SHA-256 checks passed.");
await IncrementalUpdateChecks.RunAsync();

sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
{
    public void Report(UpdateProgress value) => report(value);
}

sealed class StalledStream(byte[] prefix) : MemoryStream(prefix)
{
    public bool TimedOut { get; private set; }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Position < Length)
            return await base.ReadAsync(buffer, cancellationToken);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TimedOut = true;
            throw;
        }
        throw new InvalidOperationException("Stalled read completed unexpectedly.");
    }
}

sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
