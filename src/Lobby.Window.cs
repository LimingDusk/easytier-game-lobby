namespace GameLobby;

internal sealed partial class Lobby
{
    private const int WmNcHitTest = 0x0084;
    private const int WsCaption = 0x00C00000;
    private MapHeader? mast;

    // Keep the native sizing frame, system menu and taskbar behavior. Only the
    // caption moves into the map header.
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~WsCaption;
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmNcHitTest || m.Result != 1 || mast == null) return;
        if (mast.IsDragSurface(m.LParam)) m.Result = 2; // HTCAPTION
    }

    private void AddWindowButtons(MapHeader header)
    {
        Button CaptionButton(string glyph, string name, int fromRight)
        {
            var button = new Button
            {
                Text = glyph, AccessibleName = name, Size = new Size(46, 33),
                Location = new Point(header.ClientSize.Width - 46 * fromRight, 0),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Ink, ForeColor = Color.White, UseVisualStyleBackColor = false,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe MDL2 Assets", 10),
                AccessibleRole = AccessibleRole.PushButton
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 81, 95);
            header.Controls.Add(button);
            return button;
        }

        var minimize = CaptionButton("\uE921", "最小化", 3);
        var maximize = CaptionButton("\uE922", "最大化", 2);
        var close = CaptionButton("\uE8BB", "关闭", 1);
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(187, 53, 53);
        minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        maximize.Click += (_, _) => WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;
        close.Click += (_, _) => Close();
        Resize += (_, _) =>
        {
            bool restored = WindowState == FormWindowState.Normal;
            maximize.Text = restored ? "\uE922" : "\uE923";
            maximize.AccessibleName = restored ? "最大化" : "还原";
        };
    }
}
