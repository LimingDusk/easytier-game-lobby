using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;

namespace GameLobby;

public partial class Lobby
{
    private readonly CancellationTokenSource updateLifetime = new();
    private CancellationTokenSource? updateCancellation;
    private UpdateInstallation? updateInstallation;
    private GitHubUpdates? updateSource;
    private UpdatePackage? offeredUpdate;
    private bool checkingUpdate, applyingUpdate, closeAfterUpdate;

    private void InitializeUpdates(UpdateInstallation? installation, GitHubUpdates? source)
    {
        updateInstallation = installation;
        updateSource = installation is null ? null : source ?? new();
        updateButton.IsEnabled = installation is not null;
        updateButton.Click += async (_, _) => await UpdateClickedAsync();
        githubButton.Click += (_, _) =>
        {
            try
            {
                using var browser = Process.Start(new ProcessStartInfo(GitHubUpdates.RepositoryUrl) { UseShellExecute = true });
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                ReportFailure("无法打开 GitHub", error);
            }
        };
        if (installation is not null) Loaded += CheckOnLoad;
        Closed += (_, _) =>
        {
            updateLifetime.Cancel();
            updateSource?.Dispose();
            updateLifetime.Dispose();
        };
    }

    private async void CheckOnLoad(object sender, RoutedEventArgs e)
    {
        Loaded -= CheckOnLoad;
        await CheckUpdatesAsync(manual: false);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdate || updateInstallation is null || updateSource is null) return;
        var token = updateLifetime.Token;
        checkingUpdate = true;
        UpdateButton("检查中…", "正在检查 GitHub 发行版", enabled: false);
        string hint = $"当前版本 {SessionLog.Version}";
        try
        {
            offeredUpdate = await updateSource.CheckAsync(updateInstallation.Current, token);
            if (manual && offeredUpdate is null && !token.IsCancellationRequested)
                status.Text = "当前已是最新版本。";
        }
        catch (Exception error) when (UpdateFailure(error))
        {
            if (token.IsCancellationRequested) return;
            hint = "检查更新失败，点击重试";
            if (manual) status.Text = "暂时无法检查更新，请稍后重试。";
            log.Write($"检查更新失败（{error.GetType().Name}）");
        }
        finally
        {
            checkingUpdate = false;
            if (!token.IsCancellationRequested) ShowUpdateOffer(hint);
        }
    }

    private async Task UpdateClickedAsync()
    {
        if (applyingUpdate)
        {
            updateCancellation?.Cancel();
            return;
        }
        if (offeredUpdate is not { } package || updateInstallation is null || updateSource is null)
        {
            await CheckUpdatesAsync(manual: true);
            return;
        }
        if (starting || currentRoom is not null)
        {
            status.Text = "离开房间后可以安装更新。";
            return;
        }
        var answer = MessageBox.Show(this, $"下载 {package.Tag} 并重新打开联机大厅？",
            "安装更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        applyingUpdate = true;
        closeAfterUpdate = false;
        host.IsEnabled = join.IsEnabled = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        updateCancellation = cancellation;
        UpdateButton("取消更新", "取消下载更新", enabled: true);
        status.Text = "正在下载更新…";
        var progress = new Progress<int>(percent =>
        {
            if (applyingUpdate && !cancellation.IsCancellationRequested)
                status.Text = percent == 100 ? "正在校验并启动新版…" : $"正在下载更新 {percent}%";
        });
        bool restarted = false;
        try
        {
            await Task.Run(() => updateInstallation.ApplyAsync(updateSource, package, progress, cancellation.Token));
            restarted = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status.Text = "已取消更新。";
        }
        catch (Exception error) when (UpdateFailure(error))
        {
            offeredUpdate = null;
            status.Text = error is InvalidDataException or InvalidOperationException
                ? error.Message : "更新未完成，请重试或从 GitHub 下载。";
            log.Write($"安装更新失败（{error.GetType().Name}）");
        }
        finally
        {
            applyingUpdate = false;
            updateCancellation = null;
            host.IsEnabled = join.IsEnabled = true;
            ShowUpdateOffer($"当前版本 {SessionLog.Version}");
        }
        if (restarted || closeAfterUpdate) Close();
    }

    private bool CancelUpdateForClose()
    {
        if (!applyingUpdate) return false;
        closeAfterUpdate = true;
        updateCancellation?.Cancel();
        status.Text = "正在结束更新…";
        return true;
    }

    private void ShowUpdateOffer(string hint)
    {
        if (offeredUpdate is { } package)
        {
            updateButton.SetResourceReference(ForegroundProperty, "Accent");
            UpdateButton("安装更新", $"发现新版本 {package.Tag}", enabled: true);
        }
        else
        {
            updateButton.ClearValue(ForegroundProperty);
            UpdateButton("检查更新", hint, enabled: true);
        }
    }

    private void UpdateButton(string label, string hint, bool enabled)
    {
        updateLabel.Text = label;
        updateButton.ToolTip = hint;
        updateButton.IsEnabled = enabled;
        AutomationProperties.SetName(updateButton, label);
    }

    private static bool UpdateFailure(Exception error) => error is HttpRequestException or IOException or
        InvalidDataException or JsonException or OperationCanceledException or UnauthorizedAccessException or
        Win32Exception or InvalidOperationException;
}
