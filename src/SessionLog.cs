using System.Text;
using System.Text.RegularExpressions;

namespace GameLobby;

internal sealed class SessionLog
{
    private readonly Queue<string> recent = new();
    private readonly object gate = new();
    private readonly string path;
    private long bytes;
    internal const string Version = "0.1.0-alpha.10";
    public string? DiskError { get; private set; }

    public SessionLog(string folder)
    {
        path = Path.Combine(folder, $"lobby-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var file in new DirectoryInfo(folder).GetFiles("lobby-*.log")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(9))
                if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) file.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiskError = e.Message; }
        Write($"启动 GameLobby {Version} / EasyTier 2.6.4 / {Environment.OSVersion}");
    }

    public void Write(string value)
    {
        // Invitations and raw room secrets use the same 32-hex token across room formats.
        var clean = Regex.Replace(value, @"(?i)GL2-[A-Z0-9]+-[0-9A-F]{32}\|\S+|(GL1-[A-Z0-9]+-)?[0-9A-F]{32}", "[已隐藏]");
        clean = clean.Replace("\r", "").Replace("\n", " | ");
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {clean[..Math.Min(clean.Length, 2400)]}";
        lock (gate)
        {
            recent.Enqueue(line);
            if (recent.Count > 600) recent.Dequeue();
            if (DiskError != null || bytes >= 2_000_000) return;
            try
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                bytes += Encoding.UTF8.GetByteCount(line) + 2;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiskError = e.Message; }
        }
    }

    public string Snapshot()
    {
        lock (gate) return string.Join(Environment.NewLine, recent) + Environment.NewLine;
    }
}
