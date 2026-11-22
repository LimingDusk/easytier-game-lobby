using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GameLobby;

internal sealed class Tunnel : IDisposable
{
    private readonly Lazy<string> engineDirectory = new(EngineBundle.Locate);
    private Process? core;
    private string portal = "";
    public bool Running => core is { HasExited: false };
    public int? ExitCode => core is { HasExited: true } p ? p.ExitCode : null;

    private ProcessStartInfo Tool(string file, params string[] args)
    {
        string directory = engineDirectory.Value;
        string path = Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException("缺少 EasyTier 内核文件，请先运行 Install-Engine.cmd。");
        var info = new ProcessStartInfo(path) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public void Start(Room room)
    {
        if (Running) throw new InvalidOperationException("请先离开当前房间。");
        Stop();
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            portal = probe.LocalEndpoint.ToString()!;
        }
        var info = Tool("easytier-core.exe", "--dhcp", "--network-name", room.Network,
            "--peers", "tcp://public.easytier.cn:11010", "--rpc-portal", portal);
        info.Environment["ET_NETWORK_SECRET"] = room.Secret;
        if (room.Game.RelayBroadcast) info.ArgumentList.Add("--enable-udp-broadcast-relay");
        core = Process.Start(info) ?? throw new InvalidOperationException("EasyTier 启动失败。");
    }

    public async Task<string> Peers()
    {
        var info = Tool("easytier-cli.exe", "-p", portal, "peer");
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardOutputEncoding = info.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var cli = Process.Start(info) ?? throw new InvalidOperationException("无法启动节点查询。");
        var output = cli.StandardOutput.ReadToEndAsync();
        var error = cli.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await cli.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!cli.HasExited) cli.Kill();
            throw new TimeoutException("节点查询超时，可稍后重试。");
        }
        string result = (await output).Trim();
        string failure = (await error).Trim();
        if (cli.ExitCode != 0) throw new InvalidOperationException("尚未取得节点信息。 " + failure);
        return result;
    }

    public void Stop()
    {
        if (core is null) return;
        if (!core.HasExited)
        {
            try { core.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (core.HasExited) { }
            core.WaitForExit();
        }
        core.Dispose();
        core = null;
    }
    public void Dispose() => Stop();
}
