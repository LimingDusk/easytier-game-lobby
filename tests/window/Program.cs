using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameLobby;
using GameLobby.Games;
using Microsoft.Win32;

internal static class WindowChecks
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--update-package", var package])
        {
            UpdateWindowsChecks.RunAsync(package).GetAwaiter().GetResult();
            return;
        }
        string outputDirectory = args.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "previews") : args[0];
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var preferences = Registry.CurrentUser.CreateSubKey(@"Software\EasyTierGameLobby");
        var saved = preferences.GetValue("Appearance");
        try
        {
            preferences.DeleteValue("Appearance", false);
            var window = UpdateWindowsChecks.CreatePreviewLobby();
            window.Show();
            void Layout()
            {
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            }
            void Capture(string name)
            {
                Layout();
                var element = window.frame;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
                    (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
                    drawing.DrawRectangle(window.Background, null, bounds);
                    drawing.DrawRectangle(new VisualBrush(element), null, bounds);
                }
                bitmap.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(outputDirectory);
                using var output = File.Create(Path.Combine(outputDirectory, name + ".png"));
                encoder.Save(output);
            }
            void Roster(int count)
            {
                string[] names = ["Li", "Ming–PC", "Nova", "Atlas", "Kai", "Echo"];
                var nodes = Enumerable.Range(0, count).Select(i => new NetworkReport.Member(
                    i < names.Length ? names[i] : $"PC-{i+1}", $"10.126.126.{i+1}",
                    i == 0 ? "Local" : i == 4 ? "relay(2)" : "p2p"))
                    .Append(new("PublicServer_DreamLife", "", "p2p")).ToArray();
                var snapshot = new NetworkReport.PeerState("", nodes);
                window.ShowPeers(snapshot, snapshot.Details);
                Layout();
                Check(window.members.table.Items.Count == count && window.countBadge.Text == $"{count} 人", "Roster size");
                Check(window.diagnosticNodes.table.Items.Count == count + 1, "Diagnostic roster");
            }

            Layout();
            Check(window.systemMode.IsChecked == true && window.emptyState.IsVisible, "Default theme and lobby");
            Check(window.githubButton.IsVisible && window.updateLabel.Text == "安装更新", "GitHub entry and startup update check");
            var system = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null);
            Check(((SolidColorBrush)window.Background).Color.R == (system is 0 ? 0x14 : 0xf3), "System theme color");
            window.lightMode.IsChecked = true;
            Capture("01-empty-light");
            var room = Room.Create(GameProfile.All[0]);
            window.joinInvite.Focus();
            Layout();
            Check(window.joinInvite.IsKeyboardFocused && !window.inviteHint.IsVisible, "Focused prompt is hidden");
            window.joinInvite.Password = room.Invite;
            window.join.Focus();
            Check(!window.inviteHint.IsVisible && !window.invite.IsVisible &&
                  Room.Parse(GameProfile.All[0], window.joinInvite.Password).Network == room.Network, "Masked invitation can join");
            window.joinInvite.Clear();
            Check(window.inviteHint.IsVisible, "Empty unfocused prompt");
            window.SetRoom(room, true);
            window.updateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.status.Text == "离开房间后可以安装更新。", "An update cannot interrupt a room");
            window.status.Text = "每 4 秒刷新";
            Check(!window.invite.Text.Contains(room.Secret) && !window.host.IsVisible && window.copy.IsVisible, "Private invitation");
            window.eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.invite.Text == room.Invite, "Reveal");
            window.eye.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Roster(6);
            Capture("02-six-light");
            window.darkMode.IsChecked = true;
            Roster(2);
            Capture("03-two-dark");
            window.ShowDiagnostics("已连接　PublicServer_DreamLife", "", new("137", "28", "0"));
            window.togglePage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.diagnosticsPage.IsVisible && window.broadcastGrid.IsVisible, "Diagnostics navigation");
            Capture("04-diagnostics-dark");
            window.togglePage.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            foreach (int count in new[] { 20, 19, 20 }) Roster(count);
            var table = window.members.table;
            var scroll = FindScroll(table) ?? throw new InvalidOperationException("Missing roster viewport");
            Check(scroll.ScrollableHeight > 0, "Scrollable roster at minimum size");
            table.ScrollIntoView(table.Items[19]);
            Layout();
            Check(window.guideBar.IsVisible && window.togglePage.IsVisible && window.githubButton.IsVisible &&
                  window.updateButton.IsVisible && window.status.ActualWidth >= 80 && scroll.VerticalOffset > 0,
                  "Fixed guide and footer at minimum size");
            window.SetRoom(null);
            window.Close();
            var reopened = new Lobby();
            Check(reopened.darkMode.IsChecked == true, "Saved appearance");
            reopened.Close();
            Console.WriteLine($"Screenshots saved to {Path.GetFullPath(outputDirectory)}");
            Console.WriteLine("WPF window, themes, invites, diagnostics and 20-member scrolling passed.");
        }
        finally
        {
            if (saved == null) preferences.DeleteValue("Appearance", false);
            else preferences.SetValue("Appearance", saved);
            app.Shutdown();
        }
    }

    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScroll(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
    }
}
