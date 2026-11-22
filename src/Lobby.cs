using GameLobby.Games;

namespace GameLobby;

internal sealed class Lobby : Form
{
    private readonly Tunnel tunnel = new();
    private readonly ComboBox games = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TextBox invite = new() { Dock = DockStyle.Top, PlaceholderText = "粘贴好友的邀请码，或创建新房间" };
    private readonly Button host = ActionButton("创建房间");
    private readonly Button join = ActionButton("加入房间");
    private readonly Button copy = ActionButton("复制邀请码");
    private readonly Button leave = ActionButton("离开房间");
    private readonly Button inspect = ActionButton("刷新节点");
    private readonly Label status = new() { AutoSize = true };
    private readonly Label guide = new() { AutoSize = true, MaximumSize = new Size(650, 0) };
    private readonly TextBox peers = new() { Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private int generation;
    private bool inspecting;

    private static Button ActionButton(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3) };

    public Lobby()
    {
        Text = "游戏联机大厅";
        Font = new Font("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(725, 470);
        MinimumSize = new Size(590, 440);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1 };
        Controls.Add(layout);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control control, bool fill = false)
        {
            int row = layout.RowStyles.Count;
            layout.RowCount = row + 1;
            layout.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 3, 0, 7);
            layout.Controls.Add(control, 0, row);
        }
        games.Items.AddRange(GameProfile.All);
        games.SelectedIndex = 0;
        Row(games);
        Row(invite);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.AddRange([host, join, copy, leave]);
        Row(actions);
        Row(status);
        Row(guide);
        Row(inspect);
        Row(peers, true);

        host.Click += (_, _) => StartRoom(true);
        join.Click += (_, _) => StartRoom(false);
        leave.Click += (_, _) => EndRoom("已离开房间");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(invite.Text); status.Text = "邀请码已复制，请发给一起游玩的好友。"; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板正忙，请手动复制邀请码。"; }
        };
        inspect.Click += async (_, _) => await RefreshPeers();
        timer.Tick += (_, _) =>
        {
            if (tunnel.ExitCode is int code) EndRoom($"EasyTier 已退出（{code}）。请检查内核文件和虚拟网卡后重试。");
        };
        FormClosed += (_, _) => { generation++; timer.Dispose(); tunnel.Dispose(); };
        SetActive(false);
        status.Text = "请选择游戏，创建房间或使用好友的邀请码加入。";
    }

    private void SetActive(bool active)
    {
        games.Enabled = host.Enabled = join.Enabled = !active;
        invite.ReadOnly = active;
        leave.Enabled = copy.Enabled = active;
        inspect.Enabled = active && !inspecting;
    }

    private async void StartRoom(bool hosting)
    {
        try
        {
            var game = (GameProfile)games.SelectedItem!;
            var room = hosting ? Room.Create(game) : Room.Parse(game, invite.Text);
            tunnel.Start(room);
            generation++;
            invite.Text = room.Invite;
            SetActive(true);
            status.Text = "组网进程已启动。刷新节点可查看虚拟 IP 和连接路径。";
            guide.Text = hosting ? game.HostGuide : game.GuestGuide;
            timer.Start();
            await RefreshPeers();
        }
        catch (Exception error) { status.Text = error.Message; }
    }

    private async Task RefreshPeers()
    {
        int current = generation;
        inspecting = true;
        inspect.Enabled = false;
        try
        {
            string result = await tunnel.Peers();
            if (current == generation) peers.Text = result;
        }
        catch (Exception error) { if (current == generation) peers.Text = error.Message; }
        finally
        {
            if (current == generation) { inspecting = false; inspect.Enabled = tunnel.Running; }
        }
    }

    private void EndRoom(string message)
    {
        generation++;
        timer.Stop();
        tunnel.Stop();
        inspecting = false;
        SetActive(false);
        guide.Text = peers.Text = invite.Text = "";
        status.Text = message;
    }
}
