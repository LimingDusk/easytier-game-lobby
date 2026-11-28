using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLobby;

internal static class UpdateWindowsChecks
{
    internal static Lobby CreatePreviewLobby()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installation = new UpdateInstallation(programFiles, Environment.ProcessPath!, Parse("0.2.0-alpha.8"));
        const string tag = "v0.2.0-alpha.9";
        var releases = new[]
        {
            new
            {
                tag_name = tag, prerelease = true, draft = false,
                assets = new[]
                {
                    new
                    {
                        name = GitHubUpdates.AssetName, state = "uploaded", size = 12,
                        digest = "sha256:" + new string('A', 64),
                        browser_download_url = $"{GitHubUpdates.RepositoryUrl}/releases/download/{tag}/{GitHubUpdates.AssetName}"
                    }
                }
            }
        };
        return new Lobby(installation, new GitHubUpdates(new Reply(_ =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases)) })));
    }

    internal static void Run(string fixture)
    {
        string path = Path.GetFullPath(fixture);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string sandbox = Path.Combine(programFiles, "EasyTierGameLobbyUpdateChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            string original = Path.Combine(sandbox, "Original.exe");
            File.WriteAllText(original, "original");
            var installation = new UpdateInstallation(sandbox, original, Parse("0.0.0-alpha.1"));
            string tag = "v" + FileVersionInfo.GetVersionInfo(path).ProductVersion;
            using (var corrupt = new GitHubUpdates(new Reply(_ =>
                new(HttpStatusCode.OK) { Content = new ByteArrayContent([0, 1, 2]) })))
            {
                bool rejected = false;
                try
                {
                    installation.ApplyAsync(corrupt, new(tag, 3, new string('A', 64)), null, CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected || installation.TryLaunchInstalledUpdate() is not null)
                    throw new InvalidOperationException("A damaged update must not activate a version.");
            }

            UpdatePackage package;
            using (var source = File.OpenRead(path))
                package = new(tag, source.Length, Convert.ToHexString(SHA256.HashData(source)));

            using var pause = new CancellationTokenSource();
            using (var first = new GitHubUpdates(new Reply(_ =>
                new(HttpStatusCode.OK) { Content = new StreamContent(File.OpenRead(path)) })))
            {
                try
                {
                    installation.ApplyAsync(first, package, new InlineProgress(update =>
                    {
                        if (update is { Phase: UpdatePhase.Downloading, Percent: > 0 and < 100 }) pause.Cancel();
                    }), pause.Token).GetAwaiter().GetResult();
                    throw new InvalidOperationException("The partial download should be paused before activation.");
                }
                catch (OperationCanceledException) when (pause.IsCancellationRequested) { }
            }
            if (installation.TryLaunchInstalledUpdate() is not null)
                throw new InvalidOperationException("A paused download must not activate a version.");

            using var valid = new GitHubUpdates(new Reply(request =>
            {
                long offset = request.Headers.Range?.Ranges.Single().From ?? 0;
                if (offset <= 0 || offset >= package.Size)
                    throw new InvalidOperationException("The protected download was not resumed.");
                var file = File.OpenRead(path);
                file.Position = offset;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(file) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, package.Size - 1, package.Size);
                return response;
            }));
            var reopened = new UpdateInstallation(sandbox, original, Parse("0.0.0-alpha.1"));
            int pid = reopened.ApplyAsync(valid, package, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            using (var child = Process.GetProcessById(pid)) VerifyAndClose(child);

            using var relaunched = installation.TryLaunchInstalledUpdate()
                ?? throw new InvalidOperationException("The original launcher did not open the installed version.");
            string installedCache = LauncherCache(sandbox, original);
            string installedPath = Path.Combine(installedCache, package.Sha256, GitHubUpdates.AssetName);
            VerifyAndClose(relaunched, installedPath);
            using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(installedCache, "installed.json"))))
                if (!record.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet().SetEquals(["Tag", "Size", "Sha256"]))
                    throw new InvalidOperationException("The launcher receipt changed its legacy schema.");
            CheckAlternateLauncher(sandbox, original, path, package, installedPath);
            if (File.ReadAllText(original) != "original")
                throw new InvalidOperationException("The original launcher was modified.");
            Console.WriteLine("Protected Windows installation, resumed download, failed checksum and relaunch across launcher locations passed.");
        }
        finally { Directory.Delete(sandbox, recursive: true); }
    }

    private static void CheckAlternateLauncher(string sandbox, string original, string fixture, UpdatePackage package,
        string installedPath)
    {
        string alternate = Path.Combine(sandbox, "Moved.exe");
        File.Copy(original, alternate);
        var moved = new UpdateInstallation(sandbox, alternate, Parse("0.0.0-alpha.1"));
        using (var recovered = moved.TryLaunchInstalledUpdate() ??
            throw new InvalidOperationException("A moved launcher did not recover the installed version."))
            VerifyAndClose(recovered, installedPath);

        string alternateCache = LauncherCache(sandbox, alternate);
        string packageDirectory = Path.Combine(alternateCache, package.Sha256);
        UpdateFiles.DirectoryAt(alternateCache, create: true);
        UpdateFiles.DirectoryAt(packageDirectory, create: true);
        string alternateExecutable = Path.Combine(packageDirectory, GitHubUpdates.AssetName);
        using (var file = File.OpenRead(fixture))
        using (var copy = UpdateFiles.CreateFile(alternateExecutable, FileMode.CreateNew))
            file.CopyTo(copy);

        string alternateReceipt = Path.Combine(alternateCache, "installed.json");
        using (var receipt = UpdateFiles.CreateFile(alternateReceipt, FileMode.CreateNew))
            JsonSerializer.Serialize(receipt, package with { Tag = "v2147483647.0.0" });
        using (var recovered = moved.TryLaunchInstalledUpdate() ??
            throw new InvalidOperationException("A false product version blocked a valid older receipt."))
            VerifyAndClose(recovered, installedPath);

        File.Delete(alternateReceipt);
        using (var receipt = UpdateFiles.CreateFile(alternateReceipt, FileMode.CreateNew))
            JsonSerializer.Serialize(receipt, package);
        using (var exact = moved.TryLaunchInstalledUpdate() ??
            throw new InvalidOperationException("An exact receipt was skipped for an equal release."))
            VerifyAndClose(exact, alternateExecutable);

        var current = new UpdateInstallation(sandbox, installedPath, Parse(package.Tag));
        using var redundant = current.TryLaunchInstalledUpdate();
        if (redundant is not null)
        {
            VerifyAndClose(redundant);
            throw new InvalidOperationException("Startup tried to relaunch an already running release.");
        }
    }

    private static string LauncherCache(string sandbox, string launcher) => Path.Combine(sandbox,
        "EasyTierGameLobbyUpdates", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(launcher.ToUpperInvariant()))));

    private static void VerifyAndClose(Process child, string? expectedPath = null)
    {
        try
        {
            // Retain a process handle before asking the window to close so ExitCode stays available.
            _ = child.SafeHandle;
            if (expectedPath is not null && !string.Equals(child.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Startup selected the wrong cached executable.");
            if (child.MainWindowHandle == IntPtr.Zero || !child.CloseMainWindow() ||
                !child.WaitForExit(8000))
                throw new InvalidOperationException("The updated lobby did not open and close normally.");

            int exitCode = child.ExitCode;
            if (exitCode != 0)
                throw new InvalidOperationException($"The updated lobby exited with code 0x{exitCode:X8}.");
        }
        finally
        {
            UpdateRestart.Stop(child);
            child.WaitForExit(5000);
        }
    }

    private static ReleaseVersion Parse(string value) => ReleaseVersion.TryParse(value, out var version)
        ? version : throw new InvalidOperationException("Invalid fixture version.");

    private sealed class InlineProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    private sealed class Reply(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
