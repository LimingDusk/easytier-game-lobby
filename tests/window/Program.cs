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

        static void Input(Control control, string method, EventArgs args) =>
            control.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [args]);

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
                var slider = Field<Control>(form, "appearance");
                if (slider.AccessibilityObject.Value != "跟随系统") throw new Exception("First launch must follow Windows.");
                void Mouse(string method, int x) => Input(slider, method,
                    new MouseEventArgs(MouseButtons.Left, 1, x, slider.Height / 2, 0));
                void Key(Keys key) => Input(slider, "OnKeyDown", new KeyEventArgs(key));
                object? system = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", null);
                bool systemDark = system is int i && i == 0;
                bool Dark() => form.BackColor.GetBrightness() < 0.5f;
                if (Dark() != systemDark) throw new Exception("Incorrect system appearance.");
                Mouse("OnMouseDown", slider.Width / 2);
                Mouse("OnMouseUp", slider.Width / 2);
                if (slider.AccessibilityObject.Value != "浅色") throw new Exception("Click did not select light mode.");
                if (Dark()) throw new Exception("Light mode not applied.");
                Mouse("OnMouseDown", slider.Width / 2);
                Mouse("OnMouseMove", slider.Width - 4);
                if (Dark()) throw new Exception("A drag should commit on release.");
                Mouse("OnMouseUp", slider.Width - 4);
                if (!Dark()) throw new Exception("Dark mode not applied.");
                Key(Keys.Home);
                if (slider.AccessibilityObject.Value != "跟随系统" || Dark() != systemDark)
                    throw new Exception("Keyboard selection did not restore system appearance.");
                Key(Keys.Right);
                Key(Keys.Right);
                if (slider.AccessibilityObject.Value != "深色") throw new Exception("Arrow selection failed.");
                form.Size = form.MinimumSize;
                Application.DoEvents();
                var diagnosticButton = Field<Button>(form, "diagnosticsToggle");
                if (slider.Bounds.IntersectsWith(diagnosticButton.Bounds) || diagnosticButton.Right > slider.Parent!.ClientSize.Width)
                    throw new Exception("Appearance selector or diagnostics button clipped at minimum size.");
                if (Field<ListView>(form, "members").Visible)
                    throw new Exception("An empty lobby should hide the member table.");
                var games = Field<ComboBox>(form, "games");
                if (!games.Visible || games.Items.Count == 0 || games.SelectedItem == null)
                    throw new Exception("Game selector missing before joining a room.");
                if (Field<Panel>(form,"mast").BackColor != slider.Parent.BackColor)
                    throw new Exception("Title bar and footer must use the same background.");
                var peerType = type.Assembly.GetType("GameLobby.NetworkReport", true)!;
                var parse = peerType.GetMethod("ReadPeers")!;
                var show = type.GetMethod("ShowMembers", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var roster = Field<ListView>(form,"members");
                foreach (var count in new[] { 2, 12, 1, 0 })
                {
                    var rows = Enumerable.Range(0,count).Select(n => new {
                        hostname = $"PC-{n}", cost = n == 0 ? "Local" : "p2p", ipv4 = $"10.0.0.{n+1}" })
                        .Append(new { hostname = "PublicServer_test", cost = "p2p", ipv4 = "" });
                    object? snapshot = parse.Invoke(null,[System.Text.Json.JsonSerializer.Serialize(rows)]);
                    show.Invoke(form,[snapshot]);
                    if (roster.Items.Count != count || roster.Visible != (count > 0) ||
                        Field<Label>(form,"countBadge").Text != $"{count} 人")
                        throw new Exception("Room member count and visible rows must track joins and departures.");
                }
                diagnosticButton.PerformClick();
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
