using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameLobby;

public partial class Lobby
{
    private const double ChatWideContentWidth = 850;
    private const double ChatPanelWidth = 320;
    private const double ChatPanelGap = 12;
    private const int ChatHistoryLimit = 120;

    private sealed class ChatLine(string header, string text, bool own, string delivery) : INotifyPropertyChanged
    {
        public string Header { get; } = header;
        public string Text { get; } = text;
        public bool Own { get; } = own;
        public string Delivery
        {
            get => delivery;
            set
            {
                delivery = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Delivery)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private bool chatActive, chatExpanded, chatUnread, chatCanSend;
    private int chatUiGeneration;

    private bool ViewingChat => chatPanel.IsVisible && IsActive && WindowState != WindowState.Minimized;
    private bool AtLatestChatMessage => chatScroll.ScrollableHeight - chatScroll.VerticalOffset <= 1;

    private void InitializeChat()
    {
        chatInput.MaxLength = RoomChat.MaxMessageLength;
        chatToggle.Click += (_, _) =>
        {
            chatExpanded = !chatExpanded;
            RefreshChatVisibility();
            if (chatExpanded)
            {
                chatScroll.ScrollToBottom();
                if (chatCanSend) chatInput.Focus();
            }
        };
        chatSend.Click += (_, _) => _ = RunActionAsync(SubmitChatAsync, "发送聊天消息失败");
        chatInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            if (!e.IsRepeat) _ = RunActionAsync(SubmitChatAsync, "发送聊天消息失败");
        };
        chatInput.TextChanged += (_, _) => UpdateChatComposer();
        chatInput.IsKeyboardFocusWithinChanged += (_, _) => UpdateChatComposer();
        chatScroll.ScrollChanged += (_, _) => MarkChatReadIfVisible();
        roomContent.SizeChanged += (_, _) => LayoutChatPanel();
        Activated += (_, _) => MarkChatReadIfVisible();
        StateChanged += (_, _) => MarkChatReadIfVisible();
    }

    private void ResetChatUi(bool active)
    {
        chatUiGeneration++;
        chatActive = active;
        chatExpanded = false;
        chatMessages.Items.Clear();
        chatScroll.ScrollToTop();
        chatEmpty.Visibility = Visibility.Visible;
        chatInput.Clear();
        SetChatUnread(false);
        SetChatStatus(active ? "正在组网…" : "", false);
        RefreshChatVisibility();
    }

    private void RefreshChatVisibility()
    {
        chatAction.Visibility = Visible(chatActive);
        LayoutChatPanel();
        chatChevron.Angle = chatExpanded ? 180 : 0;
        chatToggle.SetResourceReference(Control.BackgroundProperty, chatExpanded ? "Tint" : "Surface");
        chatToggle.SetResourceReference(Control.BorderBrushProperty, chatExpanded ? "Accent" : "Edge");
        chatToggle.SetResourceReference(Control.ForegroundProperty, chatExpanded ? "Accent" : "Ink");
        UpdateChatToggleLabel();
        MarkChatReadIfVisible();
    }

    private void LayoutChatPanel()
    {
        bool wide = chatExpanded && roomContent.ActualWidth >= ChatWideContentWidth;
        chatColumn.Width = new GridLength(wide ? ChatPanelWidth + ChatPanelGap : 0);
        Grid.SetColumn(chatPanel, wide ? 1 : 0);
        chatPanel.Margin = new Thickness(wide ? ChatPanelGap : 0, 0, 0, 0);
        membersCard.Visibility = Visible(!chatExpanded || wide);
        chatPanel.Visibility = Visible(chatExpanded);
    }

    private void MarkChatReadIfVisible()
    {
        if (chatUnread && ViewingChat && AtLatestChatMessage) SetChatUnread(false);
    }

    private void SetChatUnread(bool unread)
    {
        chatUnread = unread;
        chatUnreadDot.Visibility = Visible(unread);
        UpdateChatToggleLabel();
    }

    private void UpdateChatToggleLabel()
    {
        string action = chatExpanded ? "收起房间聊天" : "展开房间聊天";
        string label = chatUnread ? $"{action}，有未读消息" : action;
        chatToggle.ToolTip = label;
        AutomationProperties.SetName(chatToggle, label);
    }

    internal void SetChatStatus(string text, bool canSend)
    {
        chatCanSend = canSend;
        chatStatus.Text = text;
        UpdateChatComposer();
    }

    private void UpdateChatComposer()
    {
        chatInput.IsReadOnly = !chatCanSend;
        chatInputHint.Visibility = Visible(chatInput.Text.Length == 0 && !chatInput.IsKeyboardFocusWithin);
        chatSend.IsEnabled = chatCanSend && !string.IsNullOrWhiteSpace(chatInput.Text);
    }

    internal void ShowChatMessage(ChatMessage message, bool own)
    {
        if (chatActive) AppendChatLine(message, own, "");
    }

    private ChatLine AppendChatLine(ChatMessage message, bool own, string delivery)
    {
        bool follow = ViewingChat && AtLatestChatMessage && !chatMessages.IsKeyboardFocusWithin;
        string name = own ? $"我（{message.Name}）" : message.Name;
        var line = new ChatLine($"{name}　{message.SentAt.ToLocalTime():HH:mm}", message.Text, own, delivery);
        chatMessages.Items.Add(line);
        chatEmpty.Visibility = Visibility.Collapsed;
        if (chatMessages.Items.Count > ChatHistoryLimit)
        {
            double removedHeight = (chatMessages.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement)?.ActualHeight ?? 0;
            chatMessages.Items.RemoveAt(0);
            if (!follow && removedHeight > 0) chatScroll.ScrollToVerticalOffset(Math.Max(0, chatScroll.VerticalOffset - removedHeight));
        }
        if (follow) chatScroll.ScrollToBottom();
        else if (!own) SetChatUnread(true);
        return line;
    }

    private async Task SubmitChatAsync()
    {
        if (!chatSend.IsEnabled) return;
        int current = chatUiGeneration;
        if (SubmitChat(chatInput.Text.Trim()) is not { } submission) return;
        var line = AppendChatLine(submission.Message, own: true, submission.Recipients == 0 ? "仅自己可见" : "发送中");
        chatInput.Clear();
        chatInput.Focus();
        if (submission.Recipients == 0) return;

        try
        {
            int delivered = await submission.Delivery;
            if (current == chatUiGeneration)
                line.Delivery = delivered > 0 ? $"已送达 {delivered}/{submission.Recipients}" : "未获送达确认";
        }
        catch
        {
            if (current == chatUiGeneration) line.Delivery = "未获送达确认";
            throw;
        }
    }

    private async void CopyChatMessage(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChatLine line, Content: Glyph { Kind: "copy" } icon } button) return;
        bool copied = true;
        try { Clipboard.SetText(line.Text); }
        catch (ExternalException) { copied = false; }

        icon.Kind = copied ? "check" : "close";
        string feedback = copied ? "消息已复制" : "剪贴板正忙，请重试";
        button.ToolTip = feedback;
        button.SetResourceReference(Control.BackgroundProperty, "Tint");
        button.SetResourceReference(Control.ForegroundProperty, copied ? "Green" : "Ink");
        button.SetResourceReference(Control.BorderBrushProperty, copied ? "Green" : "Muted");
        AutomationProperties.SetName(button, feedback);
        UIElementAutomationPeer.CreatePeerForElement(button)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        await Task.Delay(1600);
        icon.Kind = "copy";
        button.ToolTip = "复制消息";
        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
        AutomationProperties.SetName(button, "复制消息");
    }
}
