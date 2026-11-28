using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameLobby;

internal sealed class UpdateInstallation
{
    private sealed record InstalledUpdate(string Directory, UpdatePackage Package, ReleaseVersion Version, DateTime Completed);

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
            if (!Directory.Exists(root)) return null;
            EnsureRoot(create: false);
            var candidates = Directory.EnumerateDirectories(root)
                .Where(path => UpdatePackage.ValidDigest(Path.GetFileName(path)))
                .Select(ReadInstalledUpdate)
                .OfType<InstalledUpdate>()
                .OrderByDescending(update => update.Version)
                .ThenByDescending(update => string.Equals(update.Directory, directory, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(update => update.Completed);
            foreach (var update in candidates)
            {
                try { return LaunchVerified(update.Directory, update.Package); }
                catch (Exception error) when (IsStartupUpdateFailure(error)) { }
            }
        }
        catch (Exception error) when (IsStartupUpdateFailure(error)) { }
        return null;
    }

    private InstalledUpdate? ReadInstalledUpdate(string path)
    {
        try
        {
            UpdateFiles.DirectoryAt(path, create: false);
            string completedReceipt = Path.Combine(path, "installed.json");
            if (ReadReceipt(completedReceipt) is not { } package) return null;
            var version = GetVersion(package);
            if (!IsNewer(version) || string.Equals(PackagePath(path, package), executable, StringComparison.OrdinalIgnoreCase))
                return null;
            return new(path, package, version, File.GetLastWriteTimeUtc(completedReceipt));
        }
        catch (Exception error) when (IsStartupUpdateFailure(error)) { return null; }
    }

    private static bool IsStartupUpdateFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or Win32Exception or InvalidOperationException;

    internal async Task<int> ApplyAsync(GitHubUpdates source, UpdatePackage package, IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!package.IsValid() || !IsNewer(GetVersion(package)))
            throw new InvalidDataException("更新包版本无效。");
        EnsureRoot(create: true);
        UpdateFiles.DirectoryAt(directory, create: true);
        using var updateLock = UpdateFiles.CreateFile(Path.Combine(directory, "update.lock"), FileMode.OpenOrCreate);
        if (ReadReceipt(receipt) is { } installed && IsNewer(GetVersion(installed)) &&
            GetVersion(installed).CompareTo(GetVersion(package)) > 0)
            package = installed;

        string executablePath = PackagePath(directory, package);
        string packageDirectory = Path.GetDirectoryName(executablePath)!;
        UpdateFiles.DirectoryAt(packageDirectory, create: true);
        string download = Path.Combine(packageDirectory, "package.download");
        string pending = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            if (!IsCached(executablePath, package))
            {
                try
                {
                    using var file = UpdateFiles.CreateFile(download, FileMode.OpenOrCreate);
                    await source.DownloadAsync(package, file, progress, cancellationToken, executable).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                }
                catch (InvalidDataException) { TryDelete(download); throw; }
                File.Move(download, executablePath, overwrite: true);
            }

            using (var record = UpdateFiles.CreateFile(pending, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(record, new { package.Tag, package.Size, package.Sha256 });
                record.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(UpdatePhase.Starting));
            using var child = LaunchVerified(directory, package, cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pending, receipt, overwrite: true);
            }
            catch { UpdateRestart.Stop(child); throw; }
            return child.Id;
        }
        finally { TryDelete(pending); }
    }

    private void EnsureRoot(bool create)
    {
        for (var ancestor = new DirectoryInfo(programFiles); ancestor != null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Program Files 路径使用了重解析点。");
        UpdateFiles.DirectoryAt(root, create);
    }

    private static UpdatePackage? ReadReceipt(string path)
    {
        if (!File.Exists(path)) return null;
        UpdateFiles.VerifyFile(path);
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 4096) return null;
        try
        {
            var package = JsonSerializer.Deserialize<UpdatePackage>(file);
            return package?.IsValid() == true ? package : null;
        }
        catch (JsonException) { return null; }
    }

    private Process LaunchVerified(string installDirectory, UpdatePackage package, CancellationToken cancellationToken = default)
    {
        string path = PackagePath(installDirectory, package);
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

    private bool IsNewer(ReleaseVersion version) =>
        version.CompareTo(Current) > 0 && (Current.IsPrerelease || !version.IsPrerelease);

    private static string PackagePath(string installDirectory, UpdatePackage package) =>
        Path.Combine(installDirectory, package.Sha256.ToUpperInvariant(), GitHubUpdates.AssetName);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
