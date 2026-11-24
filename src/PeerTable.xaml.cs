using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace GameLobby;

public partial class PeerTable : UserControl
{
    private sealed record Row(string Name, string Address, string Route, string Initial, Visibility Badge);
    private NetworkReport.Member[] previous = [];

    public PeerTable() => InitializeComponent();
    public bool Diagnostic { get; set; }

    internal void ShowNodes(NetworkReport.Member[] nodes)
    {
        routeColumn.Header = Diagnostic ? "类型" : "连接";
        if (previous.SequenceEqual(nodes)) return;
        previous = nodes;
        table.ItemsSource = nodes.Select(n => new Row(n.Name, n.Address, Diagnostic
                ? n.Local ? "本机" : n.Shared ? "共享引导节点" : "远端节点"
                : n.Route == "中转" ? "中继" : n.Route,
            n.Name.Length == 0 ? "?" : StringInfo.GetNextTextElement(n.Name),
            Diagnostic ? Visibility.Collapsed : Visibility.Visible)).ToArray();
    }
}
