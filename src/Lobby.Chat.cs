using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameLobby;

public partial class Lobby
{
    private const double ChatWideContentWidth = 850;
    private const double ChatPanelWidth = 320;
    private const double ChatPanelGap = 12;
    private const int ChatHistoryLimit = 120;

    private sealed record ChatLine(string Header, string Text, bool Own);

    private bool chatActive, chatExpanded, chatUnread, chatCanSend, chatSending;
    private int chatUiGeneration;
    private string chatStatusText = "";

    private bool ViewingChat => chatPanel.IsVisible && IsActive && WindowState != WindowState.Minimized;
    private bool AtLatestChatMessage => chatScroll.ScrollableHeight - chatScroll.VerticalOffset <= 1;

    private void InitializeChat()
    {
        chatInput.MaxLength = RoomChat.MaxMessageLength;
        chatToggle.Click += (_, _) =>
        {
            chatExpanded = !chatExpanded;
            RefreshChatVisibility();
            if (chatExpanded && chatCanSend) chatInput.Focus();
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
        chatScroll.ScrollChanged += (_, _) =>
        {
            if (chatUnread && ViewingChat && AtLatestChatMessage) SetChatUnread(false);
        };
        roomContent.SizeChanged += (_, _) => LayoutChatPanel();
        Activated += (_, _) => ShowLatestUnreadChat();
        StateChanged += (_, _) => ShowLatestUnreadChat();
    }

    private void ResetChatUi(bool active)
    {
        chatUiGeneration++;
        chatActive = active;
        chatExpanded = chatSending = false;
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
        if (ViewingChat) ViewLatestChat();
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

    private void ShowLatestUnreadChat()
    {
        if (chatUnread && ViewingChat) ViewLatestChat();
    }

    private void ViewLatestChat()
    {
        chatScroll.ScrollToEnd();
        if (AtLatestChatMessage) SetChatUnread(false);
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
        chatStatusText = text;
        UpdateChatComposer();
    }

    private void UpdateChatComposer()
    {
        chatStatus.Text = chatSending ? "正在发送…" : chatStatusText;
        chatInput.IsReadOnly = !chatCanSend;
        chatInputHint.Visibility = Visible(chatInput.Text.Length == 0 && !chatInput.IsKeyboardFocusWithin);
        chatSend.IsEnabled = chatCanSend && !chatSending && !string.IsNullOrWhiteSpace(chatInput.Text);
    }

    internal void ShowChatMessage(ChatMessage message, bool own)
    {
        if (!chatActive) return;
        bool follow = ViewingChat && (own || AtLatestChatMessage);
        string name = own ? $"我（{message.Name}）" : message.Name;
        chatMessages.Items.Add(new ChatLine($"{name}　{message.SentAt.ToLocalTime():HH:mm}", message.Text, own));
        chatEmpty.Visibility = Visibility.Collapsed;
        if (chatMessages.Items.Count > ChatHistoryLimit) chatMessages.Items.RemoveAt(0);
        if (follow) chatScroll.ScrollToEnd();
        else if (!own) SetChatUnread(true);
    }

    private async Task SubmitChatAsync()
    {
        if (!chatSend.IsEnabled) return;
        int current = chatUiGeneration;
        string draft = chatInput.Text;
        chatSending = true;
        UpdateChatComposer();
        try
        {
            if (await SendChatAsync(draft.Trim()) && current == chatUiGeneration && chatInput.Text == draft)
                chatInput.Clear();
        }
        finally
        {
            if (current == chatUiGeneration)
            {
                chatSending = false;
                UpdateChatComposer();
            }
        }
    }
}
