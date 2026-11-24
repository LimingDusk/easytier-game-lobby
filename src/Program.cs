using System.Windows;

namespace GameLobby;

internal static class Program
{
    [STAThread]
    private static void Main() => new Application().Run(new Lobby());
}
