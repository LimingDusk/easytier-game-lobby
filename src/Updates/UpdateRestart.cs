using System.ComponentModel;
using System.Diagnostics;

namespace GameLobby;

internal static class UpdateRestart
{
    private const string ReadyOption = "--update-ready";
    private const string EventPrefix = @"Local\EasyTierGameLobby.UpdateReady.";

    internal static EventWaitHandle? OpenReadySignal(string[] args)
    {
        if (args.Length != 2 || args[0] != ReadyOption || !Guid.TryParseExact(args[1], "N", out var id))
            return null;
        try
        {
            return EventWaitHandle.TryOpenExisting(EventPrefix + id.ToString("N"), out var ready) ? ready : null;
        }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static Process Launch(string executable, string extractionDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string id = Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventPrefix + id);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add(ReadyOption);
        start.ArgumentList.Add(id);
        string[] runtimePrefixes = ["DOTNET_", "CORECLR_", "COMPLUS_", "COR_"];
        foreach (string name in start.Environment.Keys.Where(name =>
            runtimePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
            start.Environment.Remove(name);
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extractionDirectory;

        var child = Process.Start(start) ?? throw new InvalidOperationException("无法启动新版联机大厅。");
        try
        {
            int signaled = WaitHandle.WaitAny([ready, cancellationToken.WaitHandle], TimeSpan.FromSeconds(20));
            cancellationToken.ThrowIfCancellationRequested();
            if (signaled != 0 || child.HasExited)
                throw new InvalidOperationException("新版联机大厅未能正常打开。");
            return child;
        }
        catch
        {
            Stop(child);
            child.Dispose();
            throw;
        }
    }

    internal static void Stop(Process child)
    {
        try { if (!child.HasExited) child.Kill(); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }
}
