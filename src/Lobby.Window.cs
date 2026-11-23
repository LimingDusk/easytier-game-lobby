using System.Runtime.InteropServices;

namespace GameLobby;

internal sealed partial class Lobby
{
    private const int WmNcHitTest = 0x0084;
    private const int WsCaption = 0x00C00000;
    private WindowHeader? mast;

    // Keep the native sizing frame, system menu and taskbar behavior. Only the
    // caption moves into the header.
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
        // The default WS_THICKFRAME reserves a visible strip even without WS_CAPTION.
        // Keep the native side and bottom borders, but give the header the top pixels.
        const int WmNcCalcSize = 0x0083;
        if (m.Msg == WmNcCalcSize && m.LParam != IntPtr.Zero)
        {
            // Both NCCALCSIZE_PARAMS and RECT start with the proposed window RECT.
            int windowTop = Marshal.ReadInt32(m.LParam, sizeof(int));
            base.WndProc(ref m);
            if (!IsZoomed(m.HWnd))
            {
                Marshal.WriteInt32(m.LParam, sizeof(int), windowTop);
                m.Result = IntPtr.Zero;
            }
            return;
        }

        base.WndProc(ref m);
        if (m.Msg is 0x001A or 0x031A && themeInitialized && !IsDisposed && !Disposing)
            RefreshSystemTheme();
        if (m.Msg != WmNcHitTest || m.Result != 1 || mast == null) return;
        if (!mast.IsDragSurface(m.LParam)) return;
        long xy = m.LParam.ToInt64();
        var point = PointToClient(new Point((short)xy, (short)(xy >> 16)));
        int grip = LogicalToDeviceUnits(5);
        if (WindowState == FormWindowState.Normal && point.Y < grip)
            m.Result = point.X < grip ? 13 : point.X >= ClientSize.Width - grip ? 14 : 12; // top corners or top
        else m.Result = 2; // HTCAPTION
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFrameColors();
    }

    private void ApplyFrameColors()
    {
        if (!IsHandleCreated) return;
        int dark = darkTheme ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        // Older Windows versions ignore this Windows 11-only border attribute.
        int noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
    }

    private void AddWindowButtons(WindowHeader header)
    {
        Button CaptionButton(string glyph, string name, int fromRight)
        {
            var button = new Button
            {
                Text = glyph, AccessibleName = name, Size = new Size(46, 33),
                Location = new Point(header.ClientSize.Width - 46 * fromRight, 5),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = Canvas, ForeColor = Ink, UseVisualStyleBackColor = false,
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
