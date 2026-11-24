using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Microsoft.Win32;

namespace GameLobby;

public partial class Lobby
{
    private enum Appearance { System, Light, Dark }
    private const string PreferenceKey = @"HKEY_CURRENT_USER\Software\EasyTierGameLobby";
    private const string WindowsTheme = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private Appearance mode;
    private bool dark;

    private static object? ReadSetting(string path, string key)
    {
        try { return Registry.GetValue(path, key, null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private void InitializeAppearance()
    {
        mode = (ReadSetting(PreferenceKey, "Appearance") as string) switch
        {
            "Dark" => Appearance.Dark, "Light" => Appearance.Light, _ => Appearance.System
        };
        ApplyAppearance();
        systemMode.Checked += (_, _) => SelectMode(Appearance.System);
        lightMode.Checked += (_, _) => SelectMode(Appearance.Light);
        darkMode.Checked += (_, _) => SelectMode(Appearance.Dark);

        // Commit pointer selection on release so a drag can cross all three segments.
        modeBar.PreviewMouseLeftButtonDown += (_, e) =>
        {
            modeBar.CaptureMouse();
            e.Handled = true;
        };
        modeBar.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!modeBar.IsMouseCaptured) return;
            double x = e.GetPosition(modeBar).X;
            modeBar.ReleaseMouseCapture();
            var index = Math.Clamp((int)(3 * x / modeBar.ActualWidth), 0, 2);
            SelectMode((Appearance)index);
            FocusMode();
            e.Handled = true;
        };
        modeBar.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
            SelectMode(e.Key switch
            {
                Key.Home => Appearance.System, Key.End => Appearance.Dark,
                _ => (Appearance)Math.Clamp((int)mode + (e.Key == Key.Left ? -1 : 1), 0, 2)
            });
            FocusMode();
            e.Handled = true;
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(ThemeChanged);
            ApplySystemCorners(hwnd);
        };
    }

    private static class Dwm
    {
        internal const int WindowCornerPreference = 33;
        internal const int Round = 2;

        [DllImport("dwmapi.dll", ExactSpelling = true)]
        internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }

    private void ApplySystemCorners(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        int preference = Dwm.Round;
        if (Dwm.DwmSetWindowAttribute(hwnd, Dwm.WindowCornerPreference, ref preference, sizeof(int)) < 0) return;

        // A zero glass margin makes WindowChrome install a rectangular HWND region.
        // Leave the outline to DWM so it can also handle maximize, snap and restore.
        WindowChrome.GetWindowChrome(this).GlassFrameThickness = new Thickness(1);
    }

    private void FocusMode() => (mode switch
    {
        Appearance.Light => lightMode, Appearance.Dark => darkMode, _ => systemMode
    }).Focus();

    private IntPtr ThemeChanged(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (mode == Appearance.System && message is 0x001A or 0x031A) ApplyAppearance();
        return IntPtr.Zero;
    }

    private void SelectMode(Appearance next)
    {
        if (mode == next) return;
        mode = next;
        ApplyAppearance();
        try { Registry.SetValue(PreferenceKey, "Appearance", mode.ToString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { status.Text = "外观已切换，本次选择未能保存。"; }
    }

    private void ApplyAppearance()
    {
        bool next = mode == Appearance.Dark || mode == Appearance.System && ReadSetting(WindowsTheme, "AppsUseLightTheme") is 0;
        if (next != dark)
        {
            var name = next ? "Dark" : "Light";
            Resources.MergedDictionaries[0] = new ResourceDictionary
                { Source = new Uri($"/GameLobby;component/Themes/{name}.xaml", UriKind.Relative) };
            dark = next;
        }
        (mode switch { Appearance.Dark => darkMode, Appearance.Light => lightMode, _ => systemMode }).IsChecked = true;
    }
}
