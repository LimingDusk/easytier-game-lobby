using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameLobby;

internal sealed class UpdateInstallation
{
    private readonly string programFiles;
    private readonly string root;
    private readonly string directory;
    private readonly string executable;
    private readonly string receipt;

    internal UpdateInstallation(string programFiles, string executable, ReleaseVersion current)
    {
        this.programFiles = Path.GetFullPath(programFiles);
        root = Path.Combine(this.programFiles, "EasyTierGameLobbyUpdates");
        this.executable = Path.GetFullPath(executable);
        Current = current;

        var parts = Path.GetRelativePath(root, this.executable).Split(Path.DirectorySeparatorChar);
        string id = parts.Length == 3 && UpdatePackage.ValidDigest(parts[0]) && UpdatePackage.ValidDigest(parts[1]) &&
            string.Equals(parts[2], GitHubUpdates.AssetName, StringComparison.OrdinalIgnoreCase)
            ? parts[0]
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.executable.ToUpperInvariant())));
        directory = Path.Combine(root, id);
        receipt = Path.Combine(directory, "installed.json");
    }

    internal ReleaseVersion Current { get; }

    internal static UpdateInstallation? ForRelease()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programFiles.Length == 0 || Environment.ProcessPath is not { } executable ||
            typeof(UpdateInstallation).Assembly.GetManifestResourceInfo("GameLobby.EasyTier.zip") is null ||
            !ReleaseVersion.TryParse(SessionLog.Version, out var installed))
            return null;
        return new(programFiles, executable, installed);
    }

    internal Process? TryLaunchInstalledUpdate()
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            EnsureDirectories(create: false);
            var package = ReadReceipt();
            if (package is null || !IsNewer(package) ||
                string.Equals(PackagePath(package), executable, StringComparison.OrdinalIgnoreCase))
                return null;
            return LaunchVerified(package);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    internal async Task<int> ApplyAsync(GitHubUpdates source, UpdatePackage package, IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (!package.IsValid() || !IsNewer(package))
            throw new InvalidDataException("更新包版本无效。");
        EnsureDirectories(create: true);
        using var updateLock = UpdateFiles.CreateFile(Path.Combine(directory, "update.lock"), FileMode.OpenOrCreate);
        if (ReadReceipt() is { } installed && IsNewer(installed) &&
            GetVersion(installed).CompareTo(GetVersion(package)) > 0)
            package = installed;

        string executablePath = PackagePath(package);
        string packageDirectory = Path.GetDirectoryName(executablePath)!;
        UpdateFiles.DirectoryAt(packageDirectory, create: true);
        string download = Path.Combine(packageDirectory, Guid.NewGuid().ToString("N") + ".download");
        string pending = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (!IsCached(executablePath, package))
            {
                using (var file = UpdateFiles.CreateFile(download, FileMode.CreateNew))
                {
                    await source.DownloadAsync(package, file, progress, cancellationToken).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }
                File.Move(download, executablePath, overwrite: true);
            }

            using (var record = UpdateFiles.CreateFile(pending, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(record, package);
                record.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var child = LaunchVerified(package, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pending, receipt, overwrite: true);
            }
            catch { UpdateRestart.Stop(child); throw; }
            return child.Id;
        }
        finally
        {
            TryDelete(download);
            TryDelete(pending);
        }
    }

    private void EnsureDirectories(bool create)
    {
        for (var ancestor = new DirectoryInfo(programFiles); ancestor != null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Program Files 路径使用了重解析点。");
        UpdateFiles.DirectoryAt(root, create);
        UpdateFiles.DirectoryAt(directory, create);
    }

    private UpdatePackage? ReadReceipt()
    {
        if (!File.Exists(receipt)) return null;
        UpdateFiles.VerifyFile(receipt);
        using var file = File.Open(receipt, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 4096) return null;
        try
        {
            var package = JsonSerializer.Deserialize<UpdatePackage>(file);
            return package?.IsValid() == true ? package : null;
        }
        catch (JsonException) { return null; }
    }

    private Process LaunchVerified(UpdatePackage package, CancellationToken cancellationToken = default)
    {
        string path = PackagePath(package);
        string folder = Path.GetDirectoryName(path)!;
        UpdateFiles.DirectoryAt(folder, create: false);
        UpdateFiles.VerifyFile(path);
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!Matches(file, package) ||
            !ReleaseVersion.TryParse(FileVersionInfo.GetVersionInfo(path).ProductVersion, out var product) ||
            product.CompareTo(GetVersion(package)) != 0)
            throw new InvalidDataException("更新程序的版本或 SHA-256 校验失败。");
        // The .NET host adds the app name and bundle ID to its extraction path.
        string extraction = Path.Combine(root, "runtime");
        UpdateFiles.DirectoryAt(extraction, create: true);
        return UpdateRestart.Launch(path, extraction, cancellationToken);
    }

    private static bool IsCached(string path, UpdatePackage package)
    {
        if (!File.Exists(path)) return false;
        UpdateFiles.VerifyFile(path);
        using var file = File.OpenRead(path);
        return Matches(file, package);
    }

    private static bool Matches(Stream file, UpdatePackage package) => file.Length == package.Size &&
        string.Equals(Convert.ToHexString(SHA256.HashData(file)), package.Sha256, StringComparison.OrdinalIgnoreCase);

    private static ReleaseVersion GetVersion(UpdatePackage package) =>
        ReleaseVersion.TryParse(package.Tag, out var version) ? version :
        throw new InvalidDataException("更新包版本无效。");

    private bool IsNewer(UpdatePackage package)
    {
        var version = GetVersion(package);
        return version.CompareTo(Current) > 0 && (Current.IsPrerelease || !version.IsPrerelease);
    }

    private string PackagePath(UpdatePackage package) =>
        Path.Combine(directory, package.Sha256.ToUpperInvariant(), GitHubUpdates.AssetName);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
