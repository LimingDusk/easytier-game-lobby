using System.IO.Compression;

namespace GameLobby;

internal static class EngineBundle
{
    public static string Locate()
    {
        using var payload = typeof(EngineBundle).Assembly.GetManifestResourceStream("GameLobby.EasyTier.zip");
        if (payload is null) // Developer builds can use a separately installed engine.
            return Path.Combine(AppContext.BaseDirectory, "engine");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "EasyTierGameLobby", "engines");
        var target = Path.Combine(root, "v2.6.4-win-x64");
        if (!Directory.Exists(target))
        {
            Directory.CreateDirectory(root);
            var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                using var zip = new ZipArchive(payload);
                zip.ExtractToDirectory(stage);
                FindTools(stage);
                try { Directory.Move(stage, target); }
                catch (IOException) when (Directory.Exists(target)) { /* Another launcher finished first. */ }
            }
            finally
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            }
        }
        return FindTools(target);
    }

    private static string FindTools(string folder)
    {
        var core = Directory.EnumerateFiles(folder, "easytier-core.exe", SearchOption.AllDirectories).SingleOrDefault();
        if (core is null || !File.Exists(Path.Combine(Path.GetDirectoryName(core)!, "easytier-cli.exe")))
            throw new InvalidDataException($"EasyTier 内核不完整，请删除 {folder} 后重新启动。");
        return Path.GetDirectoryName(core)!;
    }
}
