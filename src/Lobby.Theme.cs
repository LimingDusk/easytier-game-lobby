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
                    b.FlatAppearance.BorderColor = darkTheme ? Color.FromArgb(86, 112, 120) : Color.FromArgb(176, 198, 202);
                    b.FlatAppearance.MouseOverBackColor = primary
                        ? (darkTheme ? Color.FromArgb(156, 226, 220) : Color.FromArgb(7, 83, 92))
                        : (darkTheme ? Color.FromArgb(48, 65, 75) : Canvas);
                    break;
                case ListView:
                    c.BackColor = surface;
                    break;
                case TextBox or ComboBox:
                    c.BackColor = c == peers ? canvas : c == invite ? (darkTheme ? field : Canvas) : field;
                    break;
                case Label:
                    c.ForeColor = c == localAddress || c == roomState ? accent :
                        c == status || c == guide || c == memberStatus ? muted : text;
                    break;
                default:
                    c.BackColor = c.Tag switch
                    {
                        "surface" => surface, "field" => darkTheme ? field : Canvas, _ => c.Parent?.BackColor ?? canvas
                    };
                    break;
            }
            foreach (Control child in c.Controls) PaintControl(child);
        }

        SuspendLayout();
        try
        {
            PaintControl(this);
            appearance.TrackColor = darkTheme ? Color.FromArgb(51, 68, 79) : Color.FromArgb(214, 226, 230);
            appearance.ThumbColor = accent;
            appearance.SelectedText = darkTheme ? Ink : Color.White;
            appearance.Invalidate();
            ApplyFrameColors();
            Invalidate(true);
        }
        finally { ResumeLayout(true); }
    }
}
