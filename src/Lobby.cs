using GameLobby.Games;

namespace GameLobby;

internal sealed partial class Lobby : Form
{
    private readonly SessionLog log = new(Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "EasyTierGameLobby", "Logs"));
    private readonly Tunnel tunnel;
    private readonly TextBox bootstrap = new() { Dock = DockStyle.Top, PlaceholderText = "可选：房主自定义引导节点。留空使用 DreamLife，加入时自动跟随新邀请码" };
    private readonly Button export = ActionButton("导出日志");
    private string lastReport = "";
    private readonly ComboBox games = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TextBox invite = new() { Dock = DockStyle.Top, PlaceholderText = "粘贴好友的邀请码，或创建新房间" };
    private readonly Button host = ActionButton("创建房间");
    private readonly Button join = ActionButton("加入房间");
    private readonly Button copy = ActionButton("复制邀请码");
    private readonly Button leave = ActionButton("离开房间");
    private readonly Button inspect = ActionButton("刷新节点");
    private readonly Label status = new() { AutoSize = true };
    private readonly Label localAddress = new() { AutoSize = true };
    private readonly Label guide = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly TextBox peers = new() { Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 4000 };
    private int generation;
    private bool inspecting;

    public Lobby()
    {
        tunnel = new(log);
        BuildLayout();

        host.Click += (_, _) => StartRoom(true);
        join.Click += (_, _) => StartRoom(false);
        leave.Click += (_, _) => EndRoom("已离开房间");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(invite.Text); status.Text = "邀请码已复制，请发给一起游玩的好友。"; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板正忙，请手动复制邀请码。"; }
        };
        inspect.Click += async (_, _) => await RefreshPeers();
        export.Click += (_, _) => ExportLog();
        timer.Tick += async (_, _) =>
        {
            if (tunnel.ExitCode is int code) EndRoom($"EasyTier 已退出（{code}）。请检查内核文件和虚拟网卡后重试。");
            else if (!inspecting && tunnel.Running) await RefreshPeers();
        };
        FormClosed += (_, _) => { generation++; timer.Dispose(); tunnel.Dispose(); log.Write("关闭大厅"); };
        SetActive(false);
        status.Text = "未加入房间";
    }

    private void SetActive(bool active)
    {
        bootstrap.Enabled = games.Enabled = !active;
        host.Visible = join.Visible = !active;
        leave.Visible = copy.Visible = active;
        invite.ReadOnly = active;
        leave.Enabled = copy.Enabled = active;
        inspect.Enabled = active && !inspecting;
    }

    private async void StartRoom(bool hosting)
    {
        try
        {
            var game = (GameProfile)games.SelectedItem!;
            var room = hosting ? Room.Create(game, bootstrap.Text) : Room.Parse(game, invite.Text, bootstrap.Text);
            log.Write($"{(hosting ? "创建" : "加入")} {game.Id} 房间标识 {room.Network}；请求开启 UDP 广播中继");
            lastReport = "";
            tunnel.Start(room, hosting);
            generation++;
            invite.Text = room.Invite;
            SetActive(true);
            status.Text = "组网进程已启动，正在查询节点。";
            roomState.Text = hosting ? "房主" : "已加入房间";
            memberStatus.Text = "正在查找房间成员…";
            guide.Text = hosting ? game.HostGuide : game.GuestGuide;
            timer.Start();
            await RefreshPeers();
        }
        catch (Exception error) { status.Text = error.Message; log.Write("启动失败 " + error.Message); }
    }

    private async Task RefreshPeers()
    {
        int current = generation;
        inspecting = true;
        inspect.Enabled = false;
        async Task<string> Fetch(string command, Func<string, string> render)
        {
            try { return render(await tunnel.Query(command)); }
            catch (Exception error) { return $"查询失败（{command}） {error.Message}"; }
        }
        try
        {
            if (tunnel.HostAddress is { } address)
            {
                string message;
                try
                {
                    message = HostNetwork.IsAssigned(address)
                        ? $"本机虚拟 IP　{address}（Windows 网卡已就绪）"
                        : $"正在配置本机虚拟网卡　{address}";
                }
                catch (System.Net.NetworkInformation.NetworkInformationException e)
                {
                    message = $"正在核实本机网卡地址 {address}（{e.ErrorCode}）";
                }
                if (message != localAddress.Text) { localAddress.Text = message; log.Write(message); }
            }
            NetworkReport.PeerState? snapshot = null;
            var reports = await Task.WhenAll(Fetch("peer", json =>
            {
                snapshot = NetworkReport.ReadPeers(json);
                return snapshot.Details;
            }),
                Fetch("connector", NetworkReport.Connectors), Fetch("stats", NetworkReport.Broadcast));
            if (current != generation) return;
            ShowMembers(snapshot);
            var text = $"{reports[0]}{Environment.NewLine}{Environment.NewLine}引导节点  {reports[1]}{Environment.NewLine}广播中继  {reports[2]}";
            status.Text = $"最近查询 {DateTime.Now:HH:mm:ss}，每 4 秒自动刷新。";
            if (peers.Text != text) peers.Text = text;
            if (text != lastReport)
            {
                log.Write(text);
                lastReport = text;
            }
        }
        finally
        {
            if (current == generation) { inspecting = false; inspect.Enabled = tunnel.Running; }
        }
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog { Filter = "诊断日志 (*.txt)|*.txt", FileName =
            $"GameLobby-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, log.Snapshot(), System.Text.Encoding.UTF8);
            status.Text = "已导出最近的诊断记录。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { status.Text = e.Message; }
    }

    private void EndRoom(string message)
    {
        generation++;
        timer.Stop();
        log.Write(message);
        tunnel.Stop();
        inspecting = false;
        SetActive(false);
        ResetMembers();
        localAddress.Text = guide.Text = peers.Text = invite.Text = "";
        status.Text = message;
    }
}
