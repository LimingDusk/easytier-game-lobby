using System.Net;
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

using var corrupt = new GitHubUpdates(new Reply(_ =>
    new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 0, 0, 0]) }));
await ExpectInvalid(() => corrupt.DownloadAsync(target, Stream.Null, null, CancellationToken.None));
await ExpectInvalid(() => corrupt.DownloadAsync(target with { Size = 5 }, Stream.Null, null, CancellationToken.None));

int requests = 0;
using var unsafeRedirect = new GitHubUpdates(new Reply(_ =>
{
    requests++;
    var response = new HttpResponseMessage(HttpStatusCode.Found);
    response.Headers.Location = new("https://example.org/standalone.exe");
    return response;
}));
await ExpectInvalid(() => unsafeRedirect.DownloadAsync(target, Stream.Null, null, CancellationToken.None));
Check(requests == 1, "Unverified redirects are not followed");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await downloader.DownloadAsync(target, Stream.Null, null, cancelled.Token);
    throw new InvalidOperationException("Cancellation ignored");
}
catch (OperationCanceledException) { }

Console.WriteLine("Update version, channel, artifact, redirect, checksum and cancellation checks passed.");

sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
