using System.ComponentModel;
using System.Diagnostics;
using System.Net;
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
            LogUpdateFailure("检查更新", error);
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
        var answer = MessageBox.Show(this, $"安装 {package.Tag} 并重新打开联机大厅？",
            "安装更新", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        applyingUpdate = true;
        closeAfterUpdate = false;
        host.IsEnabled = join.IsEnabled = false;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        updateCancellation = cancellation;
        UpdateButton("暂停更新", "暂停并保留更新进度", enabled: true);
        status.Text = "正在准备更新…";
        var progress = new Progress<UpdateProgress>(update =>
        {
            if (!ReferenceEquals(updateCancellation, cancellation) || cancellation.IsCancellationRequested) return;
            status.Text = update.Phase switch
            {
                UpdatePhase.Retrying => $"下载中断，正在重试 {update.Retry}/{GitHubUpdates.DownloadRetries}（{update.Percent}%）…",
                UpdatePhase.Verifying => "正在校验完整更新包…",
                UpdatePhase.Starting => "正在打开新版联机大厅…",
                _ when update.DownloadBytes == 0 => $"正在复用本机数据 {update.Percent}%",
                _ when update.DownloadBytes is long bytes => $"正在增量更新 {update.Percent}%（需下载 {UpdateSize(bytes)}）",
                _ => $"正在下载更新 {update.Percent}%"
            };
        });
        bool restarted = false;
        string nextAction = "安装更新";
        try
        {
            await Task.Run(() => updateInstallation.ApplyAsync(updateSource, package, progress, cancellation.Token));
            restarted = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            nextAction = "继续更新";
            status.Text = "更新已暂停，再次点击可继续。";
        }
        catch (Exception error) when (UpdateFailure(error))
        {
            nextAction = "重试更新";
            if (error is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
                offeredUpdate = null;
            status.Text = UpdateFailureMessage(error);
            LogUpdateFailure("安装更新", error);
        }
        finally
        {
            applyingUpdate = false;
            updateCancellation = null;
            host.IsEnabled = join.IsEnabled = true;
            ShowUpdateOffer($"当前版本 {SessionLog.Version}", nextAction);
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

    private static string UpdateSize(long bytes) => bytes < 1024 * 1024
        ? $"{bytes / 1024d:0.#} KiB"
        : $"{bytes / (1024d * 1024):0.#} MiB";

    private void ShowUpdateOffer(string hint, string nextAction = "安装更新")
    {
        if (offeredUpdate is { } package)
        {
            updateButton.SetResourceReference(ForegroundProperty, "Accent");
            UpdateButton(nextAction, $"新版本 {package.Tag}", enabled: true);
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

    private static string UpdateFailureMessage(Exception error) => error switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone } =>
            "该发行包已不可用，请重新检查更新。",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "GitHub 暂时拒绝下载，请稍后重试。",
        HttpRequestException => "更新下载中断，点击「重试更新」继续。",
        InvalidDataException or InvalidOperationException => error.Message,
        UnauthorizedAccessException => "无法访问更新目录，请重试或从 GitHub 下载。",
        IOException => "更新文件读写失败，请检查磁盘空间后重试。",
        Win32Exception => "新版联机大厅无法启动，请重试。",
        _ => "更新没有安装完成，请重试。"
    };

    private void LogUpdateFailure(string operation, Exception error)
    {
        string detail = error switch
        {
            HttpRequestException { StatusCode: { } statusCode } => $"HTTP {(int)statusCode}",
            HttpRequestException { InnerException: { } reason } => $"{reason.GetType().Name} 0x{reason.HResult:X8}",
            Win32Exception windows => $"Win32 {windows.NativeErrorCode}",
            _ => $"{error.GetType().Name} 0x{error.HResult:X8}"
        };
        log.Write($"{operation}失败（{detail}）");
    }
}
