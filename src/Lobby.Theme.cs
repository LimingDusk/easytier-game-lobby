using Microsoft.Win32;

namespace GameLobby;

internal sealed partial class Lobby
{
    private enum ThemeMode { System, Light, Dark }
    private const string PreferenceKey = @"HKEY_CURRENT_USER\Software\EasyTierGameLobby";
    private const string WindowsThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly AppearanceSlider appearance = new();
    private ThemeMode themeMode;
    private bool darkTheme;
    private bool themeInitialized;

    private static object? ReadSetting(string key, string value)
    {
        try { return Registry.GetValue(key, value, null); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        { return null; }
    }

    private void InitializeTheme()
    {
        themeMode = (ReadSetting(PreferenceKey, "Appearance") as string) switch
        {
            "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System
        };
        appearance.SelectedIndex = (int)themeMode;
        appearance.SelectionChanged += (_, _) => SelectTheme((ThemeMode)appearance.SelectedIndex);
        StyleTable(members, true);
        StyleTable(diagnosticNodes, false);
        void SizeRows() => rowHeight.ImageSize = new Size(1, Math.Clamp(LogicalToDeviceUnits(39), 16, 256));
        SizeRows();
        members.SmallImageList = diagnosticNodes.SmallImageList = rowHeight;
        DpiChanged += (_, _) => SizeRows();
        themeInitialized = true;
        ApplyTheme();
    }

    private void SelectTheme(ThemeMode mode)
    {
        themeMode = mode;
        ApplyTheme();
        try { Registry.SetValue(PreferenceKey, "Appearance", mode.ToString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { status.Text = "外观已切换，本次选择未能保存。"; }
    }

    private bool ShouldUseDark() => themeMode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => ReadSetting(WindowsThemeKey, "AppsUseLightTheme") is int value && value == 0
    };

    private void RefreshSystemTheme()
    {
        if (themeMode == ThemeMode.System && ShouldUseDark() != darkTheme) ApplyTheme();
    }

    private Color Surface => darkTheme ? Color.FromArgb(29,40,57) : Color.White;
    private Color SecondaryText => darkTheme ? Color.FromArgb(176,191,208) : Color.FromArgb(86,103,128);
    private Color Highlight => darkTheme ? Color.FromArgb(154,188,255) : Accent;
    private Color Tint => darkTheme ? Color.FromArgb(38,59,96) : Color.FromArgb(233,239,253);
    private Color FieldColor => darkTheme ? Color.FromArgb(37,50,70) : Color.FromArgb(241,244,249);
    private Color Edge => darkTheme ? Color.FromArgb(52,66,88) : Color.FromArgb(219,226,236);
    private Color Connected => darkTheme ? Color.FromArgb(101,213,164) : Color.FromArgb(33,132,94);

    private void StyleTable(ListView table, bool roster)
    {
        table.OwnerDraw = true;
        table.DrawColumnHeader += (_, e) =>
        {
            using var fill = new SolidBrush(FieldColor);
            e.Graphics.FillRectangle(fill,e.Bounds);
            TextRenderer.DrawText(e.Graphics,e.Header?.Text,table.Font,Rectangle.Inflate(e.Bounds,-5,0),SecondaryText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        table.DrawSubItem += (_, e) =>
        {
            if (e.Item == null || e.SubItem == null) return;
            var g = e.Graphics;
            var bounds = e.Bounds;
            bool selected = e.Item.Selected;
            using var bg = new SolidBrush(selected ? Tint : Surface);
            g.FillRectangle(bg,bounds);
            using var edge = new Pen(Edge);
            g.DrawLine(edge,bounds.Left,bounds.Bottom-1,bounds.Right,bounds.Bottom-1);
            int x = bounds.Left + LogicalToDeviceUnits(5);
            var text = e.SubItem.Text;
            if (roster && e.ColumnIndex == 0)
            {
                int size = LogicalToDeviceUnits(28);
                var badge = new Rectangle(x,bounds.Top+(bounds.Height-size)/2,size,size);
                using var path = LobbyArt.Rounded(badge, LogicalToDeviceUnits(6));
                using var fill = new SolidBrush(Tint);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillPath(fill,path);
                string initial = text.Length == 0 ? "?" : System.Globalization.StringInfo.GetNextTextElement(text);
                TextRenderer.DrawText(g,initial,table.Font,badge,Highlight,TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                x += size + LogicalToDeviceUnits(8);
            }
            if (roster && e.ColumnIndex == 2)
            {
                int size = LogicalToDeviceUnits(7);
                using var color = new SolidBrush(text == "直连" ? Connected : text == "本机" ? SecondaryText : Highlight);
                g.FillEllipse(color,x,bounds.Top+(bounds.Height-size)/2,size,size);
                x += size + LogicalToDeviceUnits(6);
            }
            var field = Rectangle.FromLTRB(x,bounds.Top,bounds.Right,bounds.Bottom);
            TextRenderer.DrawText(g,text,table.Font,field,table.ForeColor,TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (selected && table.Focused && e.ColumnIndex == 0)
                ControlPaint.DrawFocusRectangle(g,bounds,table.ForeColor,Tint);
        };
    }

    private void ApplyTheme()
    {
        darkTheme = ShouldUseDark();
        Color canvas = darkTheme ? Color.FromArgb(20,28,43) : Canvas;
        Color text = darkTheme ? Color.FromArgb(237,243,254) : Ink;
        Color action = darkTheme ? Color.FromArgb(139,178,255) : Accent;

        void PaintControl(Control c)
        {
            c.ForeColor = text;
            c.BackColor = c.Parent?.BackColor ?? canvas;
            switch (c)
            {
                case SurfacePanel panel: panel.BackColor = Surface; panel.Edge = Edge; break;
                case IconButton button:
                    bool primary = button == host || button == copy;
                    button.BackColor = primary ? action : button.Quiet ? c.BackColor : Surface;
                    button.ForeColor = primary ? (darkTheme ? Ink : Color.White) : text;
                    button.EdgeColor = primary ? action : Edge;
                    button.HoverColor = primary ? (darkTheme ? Color.LightSteelBlue : Color.RoyalBlue) : Tint;
                    break;
                case Button caption when c.Parent is WindowHeader:
                    caption.FlatAppearance.MouseOverBackColor = caption.AccessibleName == "关闭" ? Color.IndianRed : Tint;
                    break;
                case ListView: c.BackColor = Surface; break;
                case ComboBox or TextBox: c.BackColor = c == peers ? Surface : FieldColor; break;
                case Label label when label == status || label == guide || label == memberStatus || label == localAddress:
                    c.ForeColor = SecondaryText;
                    break;
            }
            if (c.Tag as string == "field") c.BackColor = FieldColor;
            if (c.Tag as string == "symbol") c.ForeColor = Highlight;
            if (c == roomState) c.ForeColor = roomState.Text.Contains("已连接") ? Connected : SecondaryText;
            if (c == countBadge) { c.BackColor = Tint; c.ForeColor = Highlight; }
            foreach (Control child in c.Controls) PaintControl(child);
        }

        SuspendLayout();
        try
        {
            PaintControl(this);
            appearance.TrackColor = FieldColor;
            appearance.ThumbColor = Surface;
            appearance.SelectedText = Highlight;
            appearance.ForeColor = SecondaryText;
            ApplyFrameColors();
            Invalidate(true);
        }
        finally { ResumeLayout(true); }
    }
}
