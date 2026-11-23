using System.Drawing.Drawing2D;
using GameLobby.Games;

namespace GameLobby;

internal sealed partial class Lobby
{
    private static readonly Color Canvas = Color.FromArgb(243,246,251);
    private static readonly Color Ink = Color.FromArgb(23,36,59);
    private static readonly Color Accent = Color.FromArgb(36,95,229);
    private readonly Label roomState = Caption("未加入房间");
    private readonly Label countBadge = Caption("0 人");
    private readonly Label emptyTitle = Caption("等待加入房间", 15, true);
    private readonly Label memberStatus = Caption("");
    private readonly Label guideTitle = Caption("");
    private readonly Panel guideBar = new() { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty };
    private readonly TableLayoutPanel emptyState = Stack();
    private readonly ListView members = NodeTable("房间成员", "设备", "虚拟 IP", "连接");
    private readonly ListView diagnosticNodes = NodeTable("节点详情", "设备", "虚拟 IP", "类型");
    private readonly Label connectorSummary = Caption("");
    private readonly Label broadcastSummary = Caption("");
    private readonly TableLayoutPanel broadcastGrid = new() { ColumnCount = 3, Dock = DockStyle.Top, AutoSize = true };
    private readonly Label captured = Caption("—",15,true), forwarded = Caption("—",15,true), failed = Caption("—",15,true);
    private readonly IconButton diagnosticsToggle = new("网络诊断", Glyph.Pulse, true);
    private readonly IconButton reveal = new("", Glyph.Eye, true);
    private readonly ToolTip tips = new();
    private readonly ImageList rowHeight = new();
    private NetworkReport.Member[] shownMembers = [], diagnosticMembers = [];
    private bool diagnosticsOpen, revealed;

    private static Label Caption(string text, float size = 10, bool bold = false) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left,
        Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(3,4,7,4)
    };

    private static ListView NodeTable(string name, params string[] columns)
    {
        var table = new ListView { View = View.Details, Dock = DockStyle.Fill,
            FullRowSelect = true, MultiSelect = false, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.None, AccessibleName = name, HideSelection = false };
        foreach (var column in columns) table.Columns.Add(column);
        table.Resize += (_, _) =>
        {
            int width = Math.Max(100, table.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            table.Columns[0].Width = width * 48 / 100;
            table.Columns[1].Width = width * 34 / 100;
            table.Columns[2].Width = width - table.Columns[0].Width - table.Columns[1].Width;
        };
        return table;
    }

    private sealed class SurfacePanel : TableLayoutPanel
    {
        public SurfacePanel() { DoubleBuffered = true; ResizeRedraw = true; }
        public Color Edge { get; set; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (Width < 3 || Height < 3) return;
            var g = e.Graphics;
            using var outline = LobbyArt.Rounded(new RectangleF(.5f,.5f,Width-1,Height-1), LogicalToDeviceUnits(11));
            g.Clear(Parent?.BackColor ?? BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var surface = new SolidBrush(BackColor);
            using var pen = new Pen(Edge);
            g.FillPath(surface, outline);
            g.DrawPath(pen, outline);
        }
    }

    private static TableLayoutPanel Stack(int padding = 0)
    {
        TableLayoutPanel stack = padding > 0 ? new SurfacePanel() : new TableLayoutPanel();
        stack.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        stack.Dock = DockStyle.Fill;
        stack.ColumnCount = 1;
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        stack.Margin = Padding.Empty;
        stack.Padding = new Padding(padding);
        return stack;
    }

    private static void Row(TableLayoutPanel stack, Control view, bool stretch = false)
    {
        int index = stack.RowStyles.Count;
        stack.RowCount = index + 1;
        stack.RowStyles.Add(stretch ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(view, 0, index);
    }

    // Exactly one flexible column; other controls keep their device-scaled preferred size.
    private static TableLayoutPanel Strip(int flex, params Control[] views)
    {
        var bar = new TableLayoutPanel { AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoSize = true, Dock = DockStyle.Top, Margin = Padding.Empty,
            ColumnCount = views.Length, RowCount = 1 };
        for (int i = 0; i < views.Length; i++)
        {
            bar.ColumnStyles.Add(i == flex ? new ColumnStyle(SizeType.Percent,100) : new ColumnStyle(SizeType.AutoSize));
            bar.Controls.Add(views[i], i, 0);
        }
        return bar;
    }

    private static Control Space() => new Panel { Size = Size.Empty, Margin = Padding.Empty };
    private static Control Symbol(Glyph glyph, int size)
    {
        var view = new Panel { Tag = "symbol", Size = new Size(size,size), Anchor = AnchorStyles.None };
        view.Paint += (_, e) => LobbyArt.Draw(e.Graphics, glyph, view.ClientRectangle, view.ForeColor);
        return view;
    }
    private void BuildLayout()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96,96);
        Text = $"联机大厅  {SessionLog.Version}";
        Font = new Font("Microsoft YaHei UI",10);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1050,690);
        MinimumSize = new Size(760,550);
        Icon = new Icon(typeof(Lobby),"GameLobby.ico");
        var root = Stack();
        Controls.Add(root);
        mast = new WindowHeader { Dock = DockStyle.Top, Size = new Size(ClientSize.Width,52),
            AccessibleName = "联机大厅", Font = new Font(Font, FontStyle.Bold) };
        Controls.Add(mast);
        AddWindowButtons(mast);
        var pages = new Panel { Dock = DockStyle.Fill, Padding = new Padding(27,14,27,8), Margin = Padding.Empty };
        Row(root,pages,true);
        var main = Stack();
        Row(main,Caption("游戏模块",10));
        games.AccessibleName = "切换游戏模块";
        games.FlatStyle = FlatStyle.Flat;
        games.Items.AddRange(GameProfile.All);
        games.SelectedIndex = 0;
        games.Width = 190;
        games.Anchor = AnchorStyles.Left;
        var gameBox = Strip(-1,Symbol(Glyph.Game,20),games);
        gameBox.Anchor = AnchorStyles.Left;
        gameBox.Dock = DockStyle.None;
        gameBox.Padding = new Padding(8,4,5,4);
        gameBox.Tag = "field";
        var gameRow = Strip(2,gameBox,roomState,Space(),leave);
        roomState.Margin = new Padding(18,4,0,4);
        gameRow.Margin = new Padding(0,0,0,12);
        Row(main,gameRow);

        var inviteCard = Stack(12);
        inviteCard.AutoSize = true;
        inviteCard.Margin = new Padding(0,0,0,12);
        invite.AccessibleName = "房间邀请码";
        invite.PlaceholderText = "粘贴好友发来的邀请码";
        invite.BorderStyle = BorderStyle.None;
        invite.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        var input = Strip(0,invite,reveal);
        input.Padding = new Padding(8,2,3,2);
        input.Tag = "field";
        var inviteRow = Strip(1,Caption("房间邀请码",11,true),input,join,copy,host);
        Row(inviteCard,inviteRow);
        Row(main,inviteCard);
        reveal.Click += (_, _) =>
        {
            revealed = !revealed;
            invite.UseSystemPasswordChar = !revealed;
            reveal.AccessibleName = revealed ? "隐藏邀请码" : "显示邀请码";
            tips.SetToolTip(reveal,reveal.AccessibleName);
        };

        var roster = Stack(13);
        var heading = Strip(2,Caption("房间成员",16,true),countBadge,Space(),inspect);
        countBadge.Tag = "badge";
        countBadge.Padding = new Padding(7,2,7,2);
        Row(roster,heading);
        var area = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        emptyState.RowCount = 5;
        foreach (var style in new[] { SizeType.Percent, SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent })
            emptyState.RowStyles.Add(new RowStyle(style,50));
        emptyTitle.Anchor = memberStatus.Anchor = AnchorStyles.None;
        emptyState.Controls.Add(Symbol(Glyph.People,39),0,1);
        emptyState.Controls.Add(emptyTitle,0,2);
        emptyState.Controls.Add(memberStatus,0,3);
        area.Controls.Add(members);
        area.Controls.Add(emptyState);
        Row(roster,area,true);
        guide.AutoSize = true;
        guide.Anchor = AnchorStyles.Left;
        guideBar.Controls.Add(Strip(2,Symbol(Glyph.Book,17),guideTitle,guide));
        Row(roster,guideBar);
        Row(main,roster,true);
        var diagnostics = BuildDiagnostics();
        pages.Controls.Add(main);
        pages.Controls.Add(diagnostics);
        diagnostics.Visible = false;

        status.Margin = new Padding(5,4,4,4);
        status.Anchor = AnchorStyles.Left;
        var footer = Strip(1,Symbol(Glyph.Refresh,16),status,appearance,diagnosticsToggle);
        footer.Padding = new Padding(27,5,24,8);
        Row(root,footer);
        diagnosticsToggle.Click += (_, _) =>
        {
            diagnosticsOpen = !diagnosticsOpen;
            main.Visible = !diagnosticsOpen;
            diagnostics.Visible = diagnosticsOpen;
            diagnosticsToggle.Glyph = diagnosticsOpen ? Glyph.Back : Glyph.Pulse;
            diagnosticsToggle.Text = diagnosticsOpen ? "返回大厅" : "网络诊断";
        };
        tips.SetToolTip(appearance,"跟随系统  ·  浅色  ·  深色");
        InitializeTheme();
        ResetMembers();
        ResumeLayout(true);
    }

    private TableLayoutPanel BuildDiagnostics()
    {
        var page = Stack();
        Row(page,Strip(0,Caption("网络诊断",18,true),export));
        var settings = Stack(13);
        settings.AutoSize = true;
        bootstrap.AccessibleName = "自定义引导节点";
        bootstrap.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        Row(settings,Strip(1,Caption("自定义引导节点",10,true),bootstrap));
        Row(page,settings);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Margin = new Padding(0,10,0,10) };
        foreach (var (title,symbol,value) in new[] { ("引导连接",Glyph.Link,connectorSummary), ("广播中继",Glyph.Pulse,broadcastSummary) })
        {
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            var card = Stack(12);
            card.AutoSize = true;
            card.Margin = value == connectorSummary ? new Padding(0,0,5,0) : new Padding(5,0,0,0);
            Row(card,Strip(1,Symbol(symbol,18),Caption(title,11,true)));
            value.Dock = DockStyle.Top;
            Row(card,value);
            if (value == broadcastSummary)
            {
                foreach (var (number, label) in new[] { (captured,"已捕获"),(forwarded,"已转发"),(failed,"转发失败") })
                {
                    broadcastGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));
                    var column = Stack();
                    column.AutoSize = true;
                    Row(column,number);
                    Row(column,Caption(label,9));
                    broadcastGrid.Controls.Add(column);
                }
                Row(card,broadcastGrid);
                broadcastGrid.Visible = false;
            }
            metrics.Controls.Add(card);
        }
        Row(page,metrics);
        var details = Stack(13);
        Row(details,Caption("节点详情",15,true));
        var area = new Panel { Dock = DockStyle.Fill };
        peers.AccessibleName = "节点查询状态";
        peers.BorderStyle = BorderStyle.None;
        area.Controls.Add(peers);
        area.Controls.Add(diagnosticNodes);
        Row(details,area,true);
        localAddress.Dock = DockStyle.Top;
        Row(details,localAddress);
        Row(page,details,true);
        return page;
    }

    private void ResetMembers()
    {
        broadcastGrid.Visible = false;
        broadcastSummary.Visible = true;
        roomState.ForeColor = SecondaryText;
        roomState.Text = "○  未加入房间";
        memberStatus.Text = "创建房间邀请好友，或使用好友的邀请码加入。";
        countBadge.Text = "0 人";
        members.Visible = diagnosticNodes.Visible = false;
        emptyState.Visible = peers.Visible = true;
        peers.Text = connectorSummary.Text = broadcastSummary.Text = "尚未加入房间";
        shownMembers = [];
        diagnosticMembers = [];
        members.Items.Clear();
        diagnosticNodes.Items.Clear();
    }

    private void ShowMembers(NetworkReport.PeerState? snapshot)
    {
        memberStatus.Text = snapshot?.Summary ?? "暂时无法读取成员，请查看网络诊断。";
        var next = snapshot?.Nodes.Where(n => !n.Shared).ToArray() ?? [];
        members.Visible = next.Length > 0;
        emptyState.Visible = next.Length == 0;
        countBadge.Text = snapshot == null ? "待查询" : $"{next.Length} 人";
        if (shownMembers.SequenceEqual(next)) return;
        shownMembers = next;
        FillNodes(members,next,false);
    }

    private void UpdateDiagnostics(NetworkReport.PeerState? snapshot, string report)
    {
        var next = snapshot?.Nodes ?? [];
        diagnosticNodes.Visible = next.Length != 0;
        peers.Visible = next.Length == 0;
        if (next.Length == 0 && peers.Text != report) peers.Text = report;
        if (diagnosticMembers.SequenceEqual(next)) return;
        diagnosticMembers = next;
        FillNodes(diagnosticNodes,next,true);
    }

    private static void FillNodes(ListView table, NetworkReport.Member[] nodes, bool diagnostic)
    {
        table.BeginUpdate();
        try
        {
            table.Items.Clear();
            foreach (var n in nodes)
            {
                string route = diagnostic ? (n.Local ? "本机" : n.Shared ? "共享引导节点" : "远端节点") : n.Route;
                table.Items.Add(new ListViewItem(new[] { n.Name,n.Address,route }) { Tag = n });
            }
        }
        finally { table.EndUpdate(); }
    }

    private sealed class WindowHeader : Panel
    {
        public WindowHeader() { DoubleBuffered = true; ResizeRedraw = true; }
        public bool IsDragSurface(IntPtr packedPoint)
        {
            long xy = packedPoint.ToInt64();
            var point = PointToClient(new Point((short)xy,(short)(xy>>16)));
            return ClientRectangle.Contains(point) && GetChildAtPoint(point) == null;
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest && IsDragSurface(m.LParam)) { m.Result = -1; return; }
            base.WndProc(ref m);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int size = LogicalToDeviceUnits(24), x = LogicalToDeviceUnits(26);
            if (FindForm()?.Icon is { } icon) e.Graphics.DrawIcon(icon,new Rectangle(x,(Height-size)/2,size,size));
            TextRenderer.DrawText(e.Graphics,AccessibleName,Font,new Rectangle(x+size+LogicalToDeviceUnits(10),0,Width/2,Height),
                ForeColor,TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            using var separator = new Pen(Color.FromArgb(40,ForeColor));
            e.Graphics.DrawLine(separator,0,Height-1,Width,Height-1);
        }
    }
}
