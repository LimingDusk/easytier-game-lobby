using Microsoft.Win32;
using System.Reflection;

internal static class WindowChecks
{
    // Runs on the Windows CI desktop; never changes the Windows theme.
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        const string key = @"Software\EasyTierGameLobby";
        using var preferences = Registry.CurrentUser.CreateSubKey(key);
        var previous = preferences.GetValue("Appearance");
        var type = Assembly.Load("GameLobby").GetType("GameLobby.Lobby", throwOnError: true)!;
        Form Open()
        {
            var form = (Form)Activator.CreateInstance(type)!;
            form.Show();
            Application.DoEvents();
            return form;
        }

        static T Field<T>(Form form, string name) where T : class =>
            (T)form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

        static void CheckTop(Form form)
        {
            var header = Field<Panel>(form, "mast");
            if (header.PointToScreen(Point.Empty).Y != form.Bounds.Top)
                throw new Exception("A non-client strip remains above the map header.");
        }

        try
        {
            preferences.DeleteValue("Appearance", false);
            using (var form = Open())
            {
                CheckTop(form);
                var menu = Field<ContextMenuStrip>(form, "themeMenu");
                var options = menu.Items.OfType<ToolStripMenuItem>().ToDictionary(i => i.Text!);
                if (!options["跟随系统"].Checked) throw new Exception("First launch must follow Windows.");
                object? system = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null);
                bool systemDark = system is int i && i == 0;
                bool Dark() => form.BackColor.GetBrightness() < 0.5f;
                if (Dark() != systemDark) throw new Exception("Incorrect system appearance.");
                options["浅色"].PerformClick();
                if (Dark()) throw new Exception("Light mode not applied.");
                options["深色"].PerformClick();
                if (!Dark()) throw new Exception("Dark mode not applied.");
                Field<Button>(form, "diagnosticsToggle").PerformClick();
                if (Field<TextBox>(form, "peers").BackColor.GetBrightness() >= 0.5f)
                    throw new Exception("Diagnostics did not adopt the dark palette.");
                form.WindowState = FormWindowState.Maximized;
                Application.DoEvents();
                form.WindowState = FormWindowState.Normal;
                Application.DoEvents();
                CheckTop(form);
                form.Close();
            }
            using var reopened = Open();
            if (reopened.BackColor.GetBrightness() >= 0.5f)
                throw new Exception("Manual appearance did not survive restart.");
            reopened.Close();
            Console.WriteLine("Window frame, default appearance, switching and persistence passed.");
        }
        finally
        {
            if (previous is null) preferences.DeleteValue("Appearance", false);
            else preferences.SetValue("Appearance", previous);
        }
    }
}
