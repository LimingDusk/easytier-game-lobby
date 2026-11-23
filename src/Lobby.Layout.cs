using System.Drawing.Drawing2D;
using GameLobby.Games;

namespace GameLobby;

internal sealed partial class Lobby
{
    private static readonly Color Ink = Color.FromArgb(22, 53, 70);
    private static readonly Color Accent = Color.FromArgb(9, 100, 111);
    private static readonly Color Canvas = Color.FromArgb(237, 243, 245);
    private static readonly Color Muted = Color.FromArgb(68, 92, 105);
    private readonly Label memberStatus = new() { AutoSize = true, ForeColor = Muted };
    private readonly Label roomState = new() { AutoSize = true, ForeColor = Accent };
    private readonly ListView members = new() { View = View.Details, Dock = DockStyle.Fill,
        FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None, AccessibleName = "房间成员" };
    private readonly Button diagnosticsToggle = ActionButton("网络诊断");
    private NetworkReport.Member[] shownMembers = [];
    private bool diagnosticsOpen;

    private static Button ActionButton(string text) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Ink,
        BackColor = Color.White, Padding = new Padding(10, 4, 10, 4),
        Margin = new Padding(0, 3, 8, 3), UseVisualStyleBackColor = false
    };

    private static void Primary(Button button)
    {
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
    }

    private static Label Heading(string text, float size) => new()
    {
        Text = text, AutoSize = true, ForeColor = Ink,
        Font = new Font("Microsoft YaHei UI", size, FontStyle.Bold),
        Margin = new Padding(0, 0, 0, 12)
    };

    private static TableLayoutPanel Stack(Color background, int padding = 0) => new()
    {
        ColumnCount = 1, Dock = DockStyle.Fill, BackColor = background,
        Margin = Padding.Empty, Padding = new Padding(padding),
        ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) }
    };

    private static void Row(TableLayoutPanel stack, Control view, bool stretch = false)
    {
        int index = stack.RowStyles.Count;
        stack.RowCount = index + 1;
        stack.RowStyles.Add(stretch ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(view, 0, index);
    }

    private static FlowLayoutPanel Actions(params Control[] buttons)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
        bar.Controls.AddRange(buttons);
        return bar;
    }

    private void BuildLayout()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Text = $"游戏联机大厅  {SessionLog.Version}";
        Font = new Font("Microsoft YaHei UI", 10);
        ForeColor = Ink;
        BackColor = Canvas;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(926, 600);
        MinimumSize = new Size(720, 510);

        Icon = new Icon(typeof(Lobby), "GameLobby.ico");
        var root = Stack(Canvas);
        Controls.Add(root);
        mast = new MapHeader { Dock = DockStyle.Top, Size = new Size(ClientSize.Width, 92),
            Margin = Padding.Empty, AccessibleName = "局域网联机大厅", Font = new Font(Font.FontFamily, 23, FontStyle.Bold) };
        Controls.Add(mast);
        AddWindowButtons(mast);

        var pages = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 12), Margin = Padding.Empty };
        Row(root, pages, true);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var setup = Stack(Color.White, 17);
        setup.AutoScroll = true;
        setup.Margin = new Padding(0, 0, 6, 0);
        main.Controls.Add(setup, 0, 0);
        Row(setup, Heading("联机房间", 16));
        games.AccessibleName = "游戏";
        games.Dock = DockStyle.Top;
        games.Items.AddRange(GameProfile.All);
        games.SelectedIndex = 0;
        Row(setup, games);
        invite.AccessibleName = "房间邀请码";
        invite.PlaceholderText = "粘贴好友发来的邀请码";
        invite.Margin = new Padding(0, 4, 0, 8);
        var inviteLabel = Heading("邀请码", 10);
        inviteLabel.Margin = new Padding(0, 17, 0, 0);
        Row(setup, inviteLabel);
        Row(setup, invite);
        Primary(host);
        Primary(copy);
        Row(setup, Actions(host, join, copy, leave));
        roomState.Margin = new Padding(0, 16, 0, 2);
        Row(setup, roomState);
        guide.Dock = DockStyle.Top;
        guide.ForeColor = Muted;
        Row(setup, guide);

        var team = Stack(Color.White, 17);
        team.Margin = new Padding(6, 0, 0, 0);
        main.Controls.Add(team, 1, 0);
        Row(team, Heading("房间成员", 16));
        memberStatus.Dock = DockStyle.Top;
        memberStatus.Margin = new Padding(0, 0, 0, 14);
        Row(team, memberStatus);
        members.Columns.Add("设备");
        members.Columns.Add("虚拟 IP");
        members.Columns.Add("连接");
        members.Resize += (_, _) =>
        {
            int unit = LogicalToDeviceUnits(1);
            int width = Math.Max(120, members.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - unit);
            members.Columns[0].Width = width * 45 / 100;
            members.Columns[1].Width = width * 36 / 100;
            members.Columns[2].Width = width - members.Columns[0].Width - members.Columns[1].Width;
        };
        Row(team, members, true);
        localAddress.Dock = DockStyle.Top;
        localAddress.ForeColor = Accent;
        Row(team, localAddress);
        Row(team, Actions(inspect));

        var diagnostics = Stack(Color.White, 17);
        Row(diagnostics, Heading("网络诊断", 16));
        Row(diagnostics, Heading("自定义引导节点", 10));
        bootstrap.AccessibleName = "自定义引导节点";
        bootstrap.Margin = new Padding(0, 0, 0, 12);
        Row(diagnostics, bootstrap);
        peers.AccessibleName = "节点和广播诊断";
        peers.BorderStyle = BorderStyle.FixedSingle;
        peers.BackColor = Canvas;
        peers.ForeColor = Ink;
        Row(diagnostics, peers, true);
        Row(diagnostics, Actions(export));
        diagnostics.Visible = false;
        pages.Controls.Add(main);
        pages.Controls.Add(diagnostics);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            Padding = new Padding(23, 0, 14, 9), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        status.Anchor = AnchorStyles.Left;
        status.ForeColor = Muted;
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(diagnosticsToggle, 1, 0);
        Row(root, footer);
        diagnosticsToggle.Click += (_, _) =>
        {
            diagnosticsOpen = !diagnosticsOpen;
            main.Visible = !diagnosticsOpen;
            diagnostics.Visible = diagnosticsOpen;
            diagnosticsToggle.Text = diagnosticsOpen ? "返回联机大厅" : "网络诊断";
        };
        ResetMembers();
        ResumeLayout(true);
    }

    private void ResetMembers()
    {
        roomState.Text = "";
        memberStatus.Text = "建房或加入后，可在这里查看队友的连接。";
        shownMembers = [];
        members.Items.Clear();
    }

    private void ShowMembers(NetworkReport.PeerState? snapshot)
    {
        memberStatus.Text = snapshot?.Summary ?? "暂时无法读取成员，请查看网络诊断。";
        var next = snapshot?.Nodes.Where(n => !n.Shared).ToArray() ?? [];
        if (shownMembers.SequenceEqual(next)) return;
        shownMembers = next;
        members.BeginUpdate();
        try
        {
            members.Items.Clear();
            foreach (var n in next)
                members.Items.Add(new ListViewItem(new[] { n.Name, n.Address, n.Route }));
        }
        finally { members.EndUpdate(); }
    }

    // Game-map tiles and a route trace keep the header sharp at different display scales.
    private sealed class MapHeader : Panel
    {
        public MapHeader() { BackColor = Ink; DoubleBuffered = true; ResizeRedraw = true; }

        public bool IsDragSurface(IntPtr packedPoint)
        {
            long xy = packedPoint.ToInt64();
            var local = PointToClient(new Point((short)xy, (short)(xy >> 16)));
            return ClientRectangle.Contains(local) && GetChildAtPoint(local) == null;
        }

        // Let Windows hit-test the form underneath the painted header. The three
        // child buttons keep their normal client hit tests and keyboard behavior.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest && IsDragSurface(m.LParam)) { m.Result = -1; return; } // HTTRANSPARENT
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = DeviceDpi / 96f;
            if (FindForm()?.Icon is { } icon)
                g.DrawIcon(icon, Rectangle.Round(new RectangleF(20*u, 27*u, 38*u, 38*u)));
            TextRenderer.DrawText(g, AccessibleName, Font,
                Rectangle.Round(new RectangleF(66*u, 0, 300*u, Height)), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            g.TranslateTransform(Width - 429 * u, 0);
            g.ScaleTransform(u, u);
            using var grid = new Pen(Color.FromArgb(57, 91, 105), 1);
            for (int col = 0; col < 9; col++)
            for (int row = -1; row < 4; row++)
            {
                float x = col * 36, y = row * 41.57f + (col % 2) * 20.78f;
                PointF[] corners = [new(x-24,y), new(x-12,y-20.78f), new(x+12,y-20.78f),
                    new(x+24,y),new(x+12,y+20.78f),new(x-12,y+20.78f)];
                g.DrawPolygon(grid, corners);
            }
            using var route = new Pen(Color.FromArgb(238, 196, 108), 2);
            PointF[] path = [new(36,62.35f), new(108,62.35f), new(144,41.57f), new(216,41.57f), new(252,20.78f)];
            g.DrawLines(route, path);
            foreach (var p in new[] { path[0], path[^1] })
            {
                g.FillEllipse(Brushes.White, p.X-3, p.Y-3, 6, 6);
                g.DrawEllipse(route, p.X-7, p.Y-7, 14, 14);
            }
        }
    }
}
