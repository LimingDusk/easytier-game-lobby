using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GameLobby;

internal sealed class Tunnel(SessionLog log) : IDisposable
{
    private sealed record CoreProcess(Process Process, IPEndPoint Portal, string InstanceName,
        IPAddress? HostAddress, Task<int> Exit)
    {
        public CancellationTokenSource Queries { get; } = new();
    }

    private const int MaxStartAttempts = 5;
    private static readonly string[] LogTopics = ["broadcast", "WinDivert", "error", "failed", "conflict", "timeout", "panic"];
    private string? engineDirectory;
    // The lobby serializes start and stop. Each exit task belongs to one child process.
    private CoreProcess? core;

    public IPAddress? HostAddress => core?.HostAddress;
    public Task<int> Exited => core?.Exit ?? throw new InvalidOperationException("联机内核尚未启动。");

    private ProcessStartInfo Tool(string file, params string[] args)
    {
        string directory = engineDirectory ??= EngineBundle.Locate();
        string path = Path.Combine(directory, file);
        if (!File.Exists(path))
            throw new FileNotFoundException("缺少 EasyTier 内核文件，请先运行 Install-Engine.cmd。");
        var info = new ProcessStartInfo(path)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    public async Task StartAsync(Room room, bool hosting)
    {
        if (core != null) throw new InvalidOperationException("请先离开当前房间。");

        var address = hosting ? HostNetwork.Choose() : null;
        log.Write("引导节点 " + room.Bootstrap);
        if (address != null) log.Write($"房主虚拟网卡申请地址 {address}/24");

        for (int attempt = 1; attempt <= MaxStartAttempts; attempt++)
        {
            if (await TryStartAsync(room, hosting, address).ConfigureAwait(false)) return;
            if (attempt == MaxStartAttempts) break;

            log.Write($"内核监听端口已占用（第 {attempt} 次），正在重试。");
            await Task.Delay(100 << (attempt - 1)).ConfigureAwait(false);
        }
        throw new InvalidOperationException($"内核监听端口连续 {MaxStartAttempts} 次绑定失败，请稍后重试。");
    }

    private async Task<bool> TryStartAsync(Room room, bool hosting, IPAddress? address)
    {
        IPEndPoint portal;
        int fakeTcpPort;
        using (var rpcProbe = new TcpListener(IPAddress.Loopback, 0))
        using (var fakeTcpProbe = new TcpListener(IPAddress.Any, 0))
        {
            rpcProbe.Start();
            fakeTcpProbe.Start();
            portal = (IPEndPoint)rpcProbe.LocalEndpoint;
            fakeTcpPort = ((IPEndPoint)fakeTcpProbe.LocalEndpoint).Port;
        }

        string instanceName = $"game-lobby-{Guid.NewGuid():N}";
        // EasyTier 2.6.4 advertises the configured FakeTCP port verbatim, even when it is zero.
        var info = Tool("easytier-core.exe", "--network-name", room.Network, "--instance-name", instanceName,
            "--rpc-portal", portal.ToString(), "--listeners", $"faketcp:{fakeTcpPort}",
            "tcp:0", "udp:0", "wg:0", "quic:0", "ws:0", "wss:0",
            "--console-log-level", "warn", "--dhcp", hosting ? "false" : "true");
        if (address != null)
        {
            info.ArgumentList.Add("--ipv4");
            info.ArgumentList.Add($"{address}/24");
        }
        info.ArgumentList.Add("--peers");
        info.ArgumentList.Add(room.Bootstrap);
        info.Environment["ET_NETWORK_SECRET"] = room.Secret;
        if (room.Game.RelayBroadcast) info.ArgumentList.Add("--enable-udp-broadcast-relay");

        var process = new Process { StartInfo = info };
        bool listenFailed = false, portInUse = false;
        process.OutputDataReceived += (_, e) => LogEngineMessage(e.Data);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { } line) return;
            listenFailed |= line.Contains("failed to listen", StringComparison.OrdinalIgnoreCase);
            portInUse |= line.Contains("AddrInUse", StringComparison.Ordinal) ||
                line.Contains("os error 10048", StringComparison.Ordinal) ||
                line.Contains("address already in use", StringComparison.OrdinalIgnoreCase);
            LogEngineMessage(line);
        };

        bool started = false;
        bool keepProcess = false;
        Task<int>? exit = null;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException("无法启动 EasyTier 内核。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            exit = ReadExitCodeAsync(process);

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ready = WaitForPortalAsync(portal, instanceName, deadline.Token);
            if (await Task.WhenAny(ready, exit).ConfigureAwait(false) == exit)
                deadline.Cancel();

            try { await ready.ConfigureAwait(false); }
            catch (OperationCanceledException) when (exit.IsCompleted) { }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("内核管理接口启动超时，请查看诊断日志。");
            }

            if (exit.IsCompleted)
            {
                int code = await exit.ConfigureAwait(false);
                // WaitForExitAsync drains the redirected logs before we classify the failure.
                if (listenFailed && portInUse) return false;
                throw new InvalidOperationException($"EasyTier 启动时已退出（{code}），请查看诊断日志。");
            }

            core = new CoreProcess(process, portal, instanceName, address, exit);
            keepProcess = true;
            return true;
        }
        finally
        {
            if (!keepProcess)
            {
                try
                {
                    if (started && exit?.IsCompletedSuccessfully != true)
                    {
                        Kill(process);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    if (exit != null) await exit.ConfigureAwait(false);
                }
                finally { process.Dispose(); }
            }
        }
    }

    private async Task WaitForPortalAsync(IPEndPoint portal, string instanceName, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Wait for the TCP listener before launching a CLI process.
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                    await client.ConnectAsync(portal, cancellationToken).ConfigureAwait(false);
                // A successful query must address the instance started for this room.
                await QueryAsync(portal, instanceName, "peer", cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { }
            catch (InvalidOperationException) { } // The listener can precede instance registration.
            catch (TimeoutException) { }
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private void LogEngineMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        foreach (string topic in LogTopics)
        {
            if (!line.Contains(topic, StringComparison.OrdinalIgnoreCase)) continue;
            log.Write("内核 " + line);
            break;
        }
    }

    public Task<string> Query(string command)
    {
        var run = core ?? throw new InvalidOperationException("联机内核尚未启动。");
        return QueryAsync(run.Portal, run.InstanceName, command, run.Queries.Token);
    }

    private async Task<string> QueryAsync(IPEndPoint portal, string instanceName, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = Tool("easytier-cli.exe", "-p", portal.ToString(), "-n", instanceName, "-o", "json", command);
        using var cli = Process.Start(info) ?? throw new InvalidOperationException("无法启动节点查询。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var output = cli.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = cli.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await cli.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string result = (await output.ConfigureAwait(false)).Trim();
            string failure = SessionLog.Redact((await error.ConfigureAwait(false)).Trim());
            if (cli.ExitCode != 0) throw new InvalidOperationException("尚未取得节点信息。 " + failure);
            if (result.Length == 0) throw new InvalidDataException($"内核 {command} 查询返回空响应。" + failure);
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Kill(cli);
            await cli.WaitForExitAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("节点查询超时，可稍后重试。");
        }
    }

    private static async Task<int> ReadExitCodeAsync(Process process)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when ((e is InvalidOperationException or Win32Exception) && process.HasExited) { }
    }

    public void Stop()
    {
        var run = core;
        if (run == null) return;
        if (!run.Exit.IsCompletedSuccessfully) Kill(run.Process);
        run.Queries.Cancel();
        try { run.Exit.GetAwaiter().GetResult(); }
        finally
        {
            core = null;
            run.Process.Dispose();
            run.Queries.Dispose();
        }
    }

    public void Dispose() => Stop();
}
