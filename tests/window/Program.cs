using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
            UpdateWindowsChecks.Run(package);
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
            void CheckGameMenu()
            {
                var menu = window.OpenGames();
                try
                {
                    Layout();
                    menu.UpdateLayout();
                    var panel = (Border)menu.Template.FindName("panel", menu);
                    var selector = (Border)window.games.Template.FindName("box", window.games);
                    Check(menu.IsOpen && Math.Abs(panel.ActualWidth - selector.ActualWidth) < 1 &&
                          panel.CornerRadius == selector.CornerRadius, "Game menu width and corners match the selector");
                    Check(((SolidColorBrush)panel.Background).Color == ((SolidColorBrush)selector.Background).Color &&
                          ((SolidColorBrush)panel.BorderBrush).Color == ((SolidColorBrush)window.games.BorderBrush).Color,
                          "Game menu uses the selector's theme colors");

                    var items = menu.Items.Cast<MenuItem>().ToArray();
                    Check(items.Select(item => (string)item.Header).SequenceEqual(GameProfile.All.Select(game => game.Name)),
                          "All game modules appear in the menu");
                    var selected = items.Single(item => item.IsChecked);
                    Check((string)selected.Header == window.gameName.Text &&
                          selected.Template.FindName("selectedDot", selected) is FrameworkElement { Visibility: Visibility.Visible },
                          "Game menu marks the current module");
                }
                finally
                {
                    menu.IsOpen = false;
                    Layout();
                }
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
            void ToggleChat()
            {
                window.chatToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout();
            }

            Layout();
            Check(window.systemMode.IsChecked == true && window.emptyState.IsVisible, "Default theme and lobby");
            Check(!window.chatAction.IsVisible, "Chat entry is hidden outside a room");
            Check(window.githubButton.IsVisible && window.updateLabel.Text == "安装更新", "GitHub entry and startup update check");
            var system = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null);
            Check(((SolidColorBrush)window.Background).Color.R == (system is 0 ? 0x14 : 0xf3), "System theme color");
            window.lightMode.IsChecked = true;
            Capture("01-empty-light");
            CheckGameMenu();
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
            window.Activate();
            Layout();
            Check(window.chatAction.IsVisible && !window.chatPanel.IsVisible && window.membersCard.IsVisible &&
                  !window.chatUnreadDot.IsVisible && !window.chatSend.IsEnabled, "Room chat starts collapsed while the network connects");
            window.ShowChatMessage(new ChatMessage("Ming-PC", "可以进游戏了。", DateTimeOffset.Now), own: false);
            Check(window.chatUnreadDot.IsVisible, "A teammate's message marks collapsed chat unread");
            ToggleChat();
            Check(window.chatPanel.IsVisible && window.membersCard.IsVisible && Grid.GetColumn(window.chatPanel) == 1 &&
                  window.chatUnreadDot.IsVisible == !window.IsActive && window.chatMessages.Items.Count == 1,
                "Wide chat opens beside members and clears the unread dot when the window is active");

            window.SetChatStatus("房间里只有你", true);
            foreach (string message in new[] { "只有我也能发消息。", "现在可以继续输入。" })
            {
                int index = window.chatMessages.Items.Count;
                window.chatInput.Text = message;
                Check(window.chatSend.IsEnabled, "Solo chat accepts consecutive submissions");
                window.chatSend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(window.chatInput.Text.Length == 0 && window.chatMessages.Items.Count == index + 1,
                    "A solo send immediately adds the message and clears the composer");
                Layout();
                Check(ChatItem<TextBox>(window, index, "messageBody").Text == message &&
                      ChatItem<TextBlock>(window, index, "deliveryStatus").Text == "仅自己可见",
                    "A solo message appears in the conversation with a local-only label");
            }
            var body = ChatItem<TextBox>(window, 0, "messageBody");
            body.Select(0, 4);
            Check(body.IsReadOnly && body.TextWrapping == TextWrapping.Wrap && body.SelectedText == "可以进游" &&
                  ApplicationCommands.Copy.CanExecute(null, body),
                "The message can be partially selected and copied in a read-only wrapping bubble");
            var lightCopy = CheckChatCopy(window);
            double viewport = window.chatScroll.ViewportWidth;
            for (int i = 0; i < 16; i++)
                window.ShowChatMessage(new ChatMessage("Ming-PC", $"滚动预览消息 {i + 1}", DateTimeOffset.Now), own: false);
            Layout();
            var lightThumb = CheckChatRail(window, viewport);
            Capture("05-room-chat-light");
            window.darkMode.IsChecked = true;
            Layout();
            Check(CheckChatRail(window, viewport) != lightThumb, "The thin chat thumb follows the lobby theme");
            Check(CheckChatCopy(window) != lightCopy, "The copy SVG follows the lobby theme");
            Capture("06-room-chat-dark");
            window.lightMode.IsChecked = true;
            int chatCount = window.chatMessages.Items.Count;

            window.chatInput.Text = "还没发送的草稿";
            ToggleChat();
            Check(!window.chatPanel.IsVisible && window.chatColumn.Width.Value == 0 && window.membersCard.IsVisible &&
                  window.chatInput.Text == "还没发送的草稿", "Collapsing chat restores member width and keeps the current draft");
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
            Layout();
            ToggleChat();
            Check(window.chatPanel.IsVisible && !window.membersCard.IsVisible && Grid.GetColumn(window.chatPanel) == 0 &&
                  window.chatMessages.Items.Count == chatCount, "Narrow chat temporarily uses the member area and keeps the room messages");
            ToggleChat();
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
            Check(!window.chatAction.IsVisible && window.chatMessages.Items.Count == 0 && window.chatInput.Text.Length == 0,
                "Leaving a room clears chat history and draft");
            CheckGameMenu();
            window.Close();
            var reopened = new Lobby();
            Check(reopened.darkMode.IsChecked == true, "Saved appearance");
            reopened.Close();
            Console.WriteLine($"Screenshots saved to {Path.GetFullPath(outputDirectory)}");
            Console.WriteLine("WPF window, themes, invites, room chat, diagnostics and 20-member scrolling passed.");
        }
        finally
        {
            if (saved == null) preferences.DeleteValue("Appearance", false);
            else preferences.SetValue("Appearance", saved);
            app.Shutdown();
        }
    }

    private static T ChatItem<T>(Lobby window, int index, string name) where T : FrameworkElement
    {
        var container = (ContentPresenter)window.chatMessages.ItemContainerGenerator.ContainerFromIndex(index)!;
        return (T)window.chatMessages.ItemTemplate.FindName(name, container);
    }

    private static Color CheckChatCopy(Lobby window)
    {
        var button = ChatItem<Button>(window, 0, "copyMessage");
        Check(button.IsEnabled && AutomationProperties.GetName(button) == "复制消息" && Equals(button.ToolTip, "复制消息"),
            "The icon-only copy button has an accessible name and tooltip");
        if (button.Content is not Glyph { Kind: "copy", Foreground: SolidColorBrush color })
            throw new InvalidOperationException("The copy button uses the existing SVG copy glyph");
        Check(color.Color == ((SolidColorBrush)button.Foreground).Color,
            "The copy icon follows the themed button foreground");
        return color.Color;
    }

    private static Color CheckChatRail(Lobby window, double viewport)
    {
        var scroll = window.chatScroll;
        var rail = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
        var track = (Track)rail.Template.FindName("PART_Track", rail);
        var thumb = track.Thumb;
        var mark = (Border)thumb.Template.FindName("thumbMark", thumb);
        var color = ((SolidColorBrush)mark.Background).Color;
        string brush = thumb.IsMouseOver || thumb.IsDragging ? "Accent" : "ChatScrollThumb";
        Check(scroll.ScrollableHeight > 0 && rail.IsVisible && mark.IsVisible &&
              Math.Abs(rail.ActualWidth - 12) < 1 && Math.Abs(mark.ActualWidth - 6) < 1 &&
              Math.Abs(scroll.ViewportWidth - viewport) < 1 && color == ((SolidColorBrush)window.FindResource(brush)).Color,
            "The chat scrollbar has a slim theme-colored thumb and keeps the conversation width stable");
        return color;
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
