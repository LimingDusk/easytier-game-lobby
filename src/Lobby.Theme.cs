using Microsoft.Win32;

namespace GameLobby;

internal sealed partial class Lobby
{
    private enum ThemeMode { System, Light, Dark }
    private const string PreferenceKey = @"HKEY_CURRENT_USER\Software\EasyTierGameLobby";
    private const string WindowsThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private readonly Button themeButton = ActionButton("外观");
    private readonly ContextMenuStrip themeMenu = new();
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
        foreach (var (name, mode) in new[] { ("跟随系统", ThemeMode.System), ("浅色", ThemeMode.Light), ("深色", ThemeMode.Dark) })
        {
            var item = new ToolStripMenuItem(name) { Tag = mode };
            item.Click += (_, _) => SelectTheme(mode);
            themeMenu.Items.Add(item);
        }
        themeButton.AccessibleName = "选择外观";
        themeButton.Click += (_, _) => themeMenu.Show(themeButton, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
        FormClosed += (_, _) => themeMenu.Dispose();
        members.OwnerDraw = true;
        members.DrawItem += (_, e) => e.DrawDefault = true;
        members.DrawSubItem += (_, e) => e.DrawDefault = true;
        members.DrawColumnHeader += (_, e) =>
        {
            using var background = new SolidBrush(darkTheme ? Color.FromArgb(43, 59, 70) : Canvas);
            e.Graphics.FillRectangle(background, e.Bounds);
            var bounds = Rectangle.Inflate(e.Bounds, -4, 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, members.Font, bounds, members.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        games.FlatStyle = FlatStyle.Flat;
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

    private void ApplyTheme()
    {
        darkTheme = ShouldUseDark();
        Color canvas = darkTheme ? Color.FromArgb(20, 30, 38) : Canvas;
        Color surface = darkTheme ? Color.FromArgb(31, 44, 54) : Color.White;
        Color text = darkTheme ? Color.FromArgb(232, 239, 242) : Ink;
        Color muted = darkTheme ? Color.FromArgb(183, 201, 209) : Muted;
        Color accent = darkTheme ? Color.FromArgb(132, 214, 210) : Accent;
        Color field = darkTheme ? Color.FromArgb(23, 35, 43) : Color.White;

        void PaintControl(Control c)
        {
            // The branded header keeps its own contrast and caption-button treatments.
            if (c is MapHeader) return;
            c.ForeColor = text;
            switch (c)
            {
                case Button b:
                    bool primary = b == host || b == copy;
                    b.BackColor = primary ? accent : surface;
                    b.ForeColor = primary ? (darkTheme ? Ink : Color.White) : text;
                    b.FlatAppearance.BorderColor = darkTheme ? Muted : Ink;
                    break;
                case ListView:
                    c.BackColor = surface;
                    break;
                case TextBox or ComboBox:
                    c.BackColor = c == peers ? canvas : field;
                    break;
                case Label:
                    c.ForeColor = c == localAddress || c == roomState ? accent :
                        c == status || c == guide || c == memberStatus ? muted : text;
                    break;
                default:
                    c.BackColor = Equals(c.Tag, "surface") ? surface : c.Parent?.BackColor ?? canvas;
                    break;
            }
            foreach (Control child in c.Controls) PaintControl(child);
        }

        SuspendLayout();
        try
        {
            PaintControl(this);
            themeMenu.BackColor = surface;
            themeMenu.ForeColor = text;
            themeMenu.RenderMode = ToolStripRenderMode.System;
            foreach (ToolStripMenuItem item in themeMenu.Items)
            {
                item.Checked = Equals(item.Tag, themeMode);
                item.BackColor = surface;
                item.ForeColor = text;
                if (item.Checked) themeButton.Text = $"外观 · {item.Text}";
            }
            ApplyFrameColors();
            Invalidate(true);
        }
        finally { ResumeLayout(true); }
    }
}
