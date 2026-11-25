using System.Windows;

namespace GameLobby;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var ready = UpdateRestart.OpenReadySignal(args);
        var installation = UpdateInstallation.ForRelease();
        using var relaunched = ready is null ? installation?.TryLaunchInstalledUpdate() : null;
        if (relaunched is not null) return;

        var app = new Application();
        var window = new Lobby(installation);
        if (ready is not null) window.ContentRendered += (_, _) => ready.Set();
        app.Run(window);
    }
}
