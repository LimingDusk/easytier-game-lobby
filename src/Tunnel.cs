using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace GameLobby;

internal sealed class Tunnel(SessionLog log) : IDisposable
{
    private static readonly string[] LogTopics = ["broadcast", "WinDivert", "error", "failed", "conflict", "timeout", "panic"];
    private string? engineDirectory;
    private Process? core;
    private string portal = "";
    public IPAddress? HostAddress { get; private set; }
    public bool Running => core is { HasExited: false };
    public int? ExitCode => core is { HasExited: true } p ? p.ExitCode : null;

    private ProcessStartInfo Tool(string file, params string[] args)
    {
        string directory = engineDirectory ??= EngineBundle.Locate();
        string path = Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException("缺少 EasyTier 内核文件，请先运行 Install-Engine.cmd。");
        var info = new ProcessStartInfo(path) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public void Start(Room room, bool hosting)
    {
        if (Running) throw new InvalidOperationException("请先离开当前房间。");
        Stop();
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            portal = probe.LocalEndpoint.ToString()!;
        }
        log.Write("引导节点 " + room.Bootstrap);
        var address = hosting ? HostNetwork.Choose() : null;
        var info = Tool("easytier-core.exe", "--network-name", room.Network,
            "--rpc-portal", portal, "--console-log-level", "warn", "--dhcp", hosting ? "false" : "true");
        if (address != null)
        {
            info.ArgumentList.Add("--ipv4");
            info.ArgumentList.Add($"{address}/24");
            log.Write($"房主虚拟网卡申请地址 {address}/24");
        }
        info.ArgumentList.Add("--peers");
        info.ArgumentList.Add(room.Bootstrap);
        info.Environment["ET_NETWORK_SECRET"] = room.Secret;
        if (room.Game.RelayBroadcast) info.ArgumentList.Add("--enable-udp-broadcast-relay");
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        core = new Process { StartInfo = info };
        core.OutputDataReceived += OnEngineMessage;
        core.ErrorDataReceived += OnEngineMessage;
        try
        {
            core.Start();
            core.BeginOutputReadLine();
            core.BeginErrorReadLine();
            HostAddress = address;
        }
        catch
        {
            try { if (core.Id > 0) Stop(); }
            catch (InvalidOperationException) { core?.Dispose(); core = null; }
            throw;
        }
    }

    private void OnEngineMessage(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line) || line.Contains("secret", StringComparison.OrdinalIgnoreCase)) return;
        foreach (string topic in LogTopics)
        {
            if (!line.Contains(topic, StringComparison.OrdinalIgnoreCase)) continue;
            log.Write("内核 " + line);
            break;
        }
    }

    public async Task<string> Query(string command)
    {
        var info = Tool("easytier-cli.exe", "-p", portal, "-o", "json", command);
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardOutputEncoding = info.StandardErrorEncoding = System.Text.Encoding.UTF8;
        using var cli = Process.Start(info) ?? throw new InvalidOperationException("无法启动节点查询。");
        var output = cli.StandardOutput.ReadToEndAsync();
        var error = cli.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await cli.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!cli.HasExited) cli.Kill(); }
            catch (InvalidOperationException) when (cli.HasExited) { }
            throw new TimeoutException("节点查询超时，可稍后重试。");
        }
        string result = (await output).Trim();
        string failure = (await error).Trim();
        if (cli.ExitCode != 0) throw new InvalidOperationException("尚未取得节点信息。 " + failure);
        if (result.Length == 0) throw new InvalidDataException($"内核 {command} 查询返回空响应。" + failure);
        return result;
    }

    public void Stop()
    {
        HostAddress = null;
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
