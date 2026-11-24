using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using GameLobby.Games;
using Microsoft.Win32;

namespace GameLobby;

public partial class Lobby : Window
{
    private readonly SessionLog log = new(Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "EasyTierGameLobby", "Logs"));
    private readonly Tunnel tunnel;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private GameProfile selectedGame = GameProfile.All[0];
    private Room? currentRoom;
    private string customBootstrap = "";
    private string lastPeerReport = "";
    private int generation;
    private bool inspecting, readingDiagnostics, starting, closeAfterStart, revealed;
    private long nextDiagnostics;
    private bool diagnosticsOpen => diagnosticsPage.Visibility == Visibility.Visible;

    public Lobby()
    {
        tunnel = new(log);
        InitializeComponent();
        InitializeAppearance();
        gameName.Text = selectedGame.Name;
        games.Click += (_, _) => OpenGames();
        host.Click += (_, _) => StartRoom(true);
        join.Click += (_, _) => StartRoom(false);
        leave.Click += (_, _) => EndRoom("已离开房间");
        eye.Click += (_, _) => { revealed = !revealed; ShowInvite(); };
        copy.Click += (_, _) =>
        {
            if (currentRoom == null) return;
            try { Clipboard.SetText(currentRoom.Invite); status.Text = "邀请码已复制，请发给一起游玩的好友。"; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板正忙，可显示邀请码后手动复制。"; }
        };
        invite.TextChanged += (_, _) => inviteHint.Visibility = Visible(currentRoom == null && invite.Text.Length == 0);
        bootstrapHint.Text = Room.DefaultBootstrap;
        bootstrap.TextChanged += (_, _) => bootstrapHint.Visibility = Visible(bootstrap.Text.Length == 0);
        inspect.Click += async (_, _) => await RefreshPeers();
        togglePage.Click += async (_, _) =>
        {
            SwitchPage(!diagnosticsOpen);
            if (diagnosticsOpen && timer.IsEnabled) await RefreshDiagnostics();
        };
        minimize.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        maximize.Click += (_, _) =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        };
        closeWindow.Click += (_, _) => Close();
        StateChanged += (_, _) =>
        {
            maximizeGlyph.Kind = WindowState == WindowState.Maximized ? "restore" : "maximize";
            frame.Margin = WindowState == WindowState.Maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        };
        export.Click += (_, _) => ExportLog();
        timer.Tick += async (_, _) =>
        {
            if (tunnel.ExitCode is int code) EndRoom($"EasyTier 已退出（{code}）。请检查内核文件和虚拟网卡后重试。");
            else if (tunnel.Running) await RefreshRoom();
        };
        Closing += OnClosing;
        Closed += (_, _) => { generation++; timer.Stop(); tunnel.Dispose(); log.Write("关闭大厅"); };
        SetRoom(null);
        ResetMembers();
        status.Text = "未加入房间";
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!starting) return;
        closeAfterStart = true;
        e.Cancel = true;
        status.Text = "正在完成准备并关闭大厅…";
    }

    private void OpenGames()
    {
        var menu = new ContextMenu { PlacementTarget = games, Placement = PlacementMode.Bottom, MinWidth = games.ActualWidth };
        menu.Background = (System.Windows.Media.Brush)FindResource("Surface");
        menu.Foreground = Foreground;
        foreach (var game in GameProfile.All)
        {
            var item = new MenuItem { Header = game.Name, IsChecked = selectedGame == game };
            item.Click += (_, _) => { selectedGame = game; gameName.Text = game.Name; };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal void SwitchPage(bool diagnostic)
    {
        mainPage.Visibility = Visible(!diagnostic);
        diagnosticsPage.Visibility = Visible(diagnostic);
        pageIcon.Kind = diagnostic ? "back" : "pulse";
        pageLabel.Text = diagnostic ? "返回大厅" : "网络诊断";
        AutomationProperties.SetName(togglePage, pageLabel.Text);
    }

    internal void SetRoom(Room? room, bool hosting = false)
    {
        if (room != null && currentRoom == null) customBootstrap = bootstrap.Text;
        if (room != null) bootstrap.Text = room.Bootstrap;
        else if (currentRoom != null) bootstrap.Text = customBootstrap;
        currentRoom = room;
        bool active = room != null;
        bootstrap.IsEnabled = games.IsEnabled = !active;
        host.Visibility = join.Visibility = Visible(!active);
        leave.Visibility = copy.Visibility = eye.Visibility = inspect.Visibility = guideBar.Visibility = Visible(active);
        inspect.IsEnabled = active && !inspecting;
        invite.IsReadOnly = active;
        revealed = false;
        ShowInvite();
        bootstrapHint.Text = room?.Bootstrap ?? Room.DefaultBootstrap;
        if (room != null)
        {
            guideTitle.Text = hosting ? "房主开房步骤" : "加入游戏步骤";
            guide.Text = hosting ? room.Game.HostGuide : room.Game.GuestGuide;
            guide.ToolTip = guide.Text;
        }
        emptyTitle.Text = active ? "正在查询房间成员" : "等待加入房间";
        statusIcon.Kind = active ? "refresh" : "people";
        SetConnection(false);
    }

    private void ShowInvite()
    {
        if (currentRoom != null) invite.Text = revealed ? currentRoom.Invite : "•••• •••• ••••";
        inviteHint.Visibility = Visible(currentRoom == null && invite.Text.Length == 0);
        string label = revealed ? "隐藏邀请码" : "显示邀请码";
        eye.ToolTip = label;
        AutomationProperties.SetName(eye, label);
    }

    private void SetConnection(bool ready)
    {
        roomState.Text = ready ? "●  房间已连接" : currentRoom == null ? "●  未加入房间" : "●  正在组网";
        roomState.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Green" : "Muted");
    }

    private void ResetMembers()
    {
        ShowPeers(null, "尚未加入房间");
        ShowDiagnostics("尚未加入房间", "尚未加入房间", null);
        memberStatus.Text = "创建房间邀请好友，或使用好友的邀请码加入。";
        countBadge.Text = "0 人";
        localAddress.Text = "";
    }

    internal void ShowPeers(NetworkReport.PeerState? snapshot, string report)
    {
        var rows = snapshot?.Nodes ?? [];
        var players = rows.Where(n => !n.Shared).ToArray();
        members.ShowNodes(players);
        diagnosticNodes.ShowNodes(rows);
        members.Visibility = Visible(players.Length != 0);
        emptyState.Visibility = Visible(players.Length == 0);
        diagnosticNodes.Visibility = Visible(rows.Length != 0);
        peerNotice.Visibility = Visible(rows.Length == 0);
        peerNotice.Text = rows.Length == 0 ? report : "";
        memberStatus.Text = snapshot?.Summary ?? "暂时无法读取成员，请查看网络诊断。";
        countBadge.Text = snapshot == null ? "待查询" : $"{players.Length} 人";
        SetConnection(rows.Any(n => n.Local && n.Ip.Length > 0));
    }

    internal void ShowDiagnostics(string connectors, string broadcast, NetworkReport.BroadcastCounts? counts)
    {
        connectorSummary.Text = connectors;
        connectorSummary.ToolTip = connectors;
        broadcastSummary.Text = broadcast;
        broadcastSummary.ToolTip = broadcast;
        broadcastSummary.Visibility = Visible(counts == null);
        broadcastGrid.Visibility = Visible(counts != null);
        if (counts != null)
            (captured.Text, forwarded.Text, failed.Text) = (counts.Captured, counts.Forwarded, counts.Failed);
    }

    private async void StartRoom(bool hosting)
    {
        if (starting || timer.IsEnabled) return;
        starting = true;
        host.IsEnabled = join.IsEnabled = bootstrap.IsEnabled = games.IsEnabled = invite.IsEnabled = false;
        try
        {
            var game = selectedGame;
            var room = hosting ? Room.Create(game, bootstrap.Text) : Room.Parse(game, invite.Text, bootstrap.Text);
            log.Write($"{(hosting ? "创建" : "加入")} {game.Id} 房间标识 {room.Network}；请求开启 UDP 广播中继");
            status.Text = "正在准备联机引擎…";
            await Task.Run(() => tunnel.Start(room, hosting));
            if (closeAfterStart) return;
            lastPeerReport = "";
            nextDiagnostics = 0;
            generation++;
            SetRoom(room, hosting);
            connectorSummary.Text = broadcastSummary.Text = "正在查询…";
            status.Text = "组网进程已启动，正在查询节点。";
            memberStatus.Text = "正在查找房间成员…";
            timer.Start();
        }
        catch (Exception error)
        {
            SetRoom(null);
            status.Text = error.Message;
            log.Write("启动失败 " + error.Message);
        }
        finally
        {
            starting = false;
            host.IsEnabled = join.IsEnabled = invite.IsEnabled = true;
            if (closeAfterStart) Close();
        }
        if (!closeAfterStart && timer.IsEnabled) await RefreshRoom();
    }

    private Task RefreshRoom()
    {
        var peersTask = RefreshPeers();
        return diagnosticsOpen || Environment.TickCount64 >= nextDiagnostics
            ? Task.WhenAll(peersTask, RefreshDiagnostics())
            : peersTask;
    }

    private async Task<string> Fetch(string command, Func<string, string> render)
    {
        try { return render(await tunnel.Query(command)); }
        catch (Exception error) { return $"查询失败（{command}） {error.Message}"; }
    }

    private async Task RefreshPeers()
    {
        if (inspecting) return;
        int current = generation;
        inspecting = true;
        inspect.IsEnabled = false;
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
            string report = await Fetch("peer", json =>
            {
                snapshot = NetworkReport.ReadPeers(json);
                return snapshot.Details;
            });
            if (current != generation) return;
            ShowPeers(snapshot, report);
            status.Text = "每 4 秒刷新";
            if (report != lastPeerReport)
            {
                log.Write(report);
                lastPeerReport = report;
            }
        }
        finally
        {
            if (current == generation) { inspecting = false; inspect.IsEnabled = tunnel.Running; }
        }
    }

    private async Task RefreshDiagnostics()
    {
        if (readingDiagnostics) return;
        readingDiagnostics = true;
        int current = generation;
        nextDiagnostics = Environment.TickCount64 + 32_000;
        try
        {
            NetworkReport.BroadcastCounts? counts = null;
            var reports = await Task.WhenAll(Fetch("connector", NetworkReport.Connectors), Fetch("stats", json =>
            {
                counts = NetworkReport.ReadBroadcast(json);
                return counts?.ToString() ?? "内核尚未报告广播计数。";
            }));
            if (current != generation) return;
            if (connectorSummary.Text != reports[0] || broadcastSummary.Text != reports[1])
                log.Write($"引导节点  {reports[0]}  广播中继  {reports[1]}");
            ShowDiagnostics(reports[0], reports[1], counts);
        }
        finally
        {
            if (current == generation) readingDiagnostics = false;
        }
    }


    private void ExportLog()
    {
        var dialog = new SaveFileDialog { Filter = "诊断日志 (*.txt)|*.txt", FileName = $"GameLobby-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != true) return;
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
        inspecting = readingDiagnostics = false;
        SetRoom(null);
        invite.Clear();
        ResetMembers();
        status.Text = message;
    }
}
