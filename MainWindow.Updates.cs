using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ScreenLingo;

public partial class MainWindow
{
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly UpdateConnector updates;
    private UpdateRelease? availableUpdate;
    private ReadyUpdate? readyUpdate;
    private bool updateBusy;

    private void InitializeUpdates()
    {
        readyUpdate = updates.LoadReady(Version.Parse(ApplicationIdentity.Version));
        if (readyUpdate is not null) ShowReadyUpdate();
        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        updateTimer.Start();
    }

    public async Task<string> CheckForUpdatesAsync()
    {
        if (updateBusy) return "An update operation is already running.";
        updateBusy = true;
        UpdateAction.IsEnabled = false;
        try
        {
            availableUpdate = null;
            availableUpdate = await updates.CheckAsync(Version.Parse(ApplicationIdentity.Version), updateCancellation.Token);
            if (availableUpdate is null)
            {
                if (readyUpdate is not null) ShowReadyUpdate();
                else UpdateBanner.Visibility = Visibility.Collapsed;
                return "You are up to date · ScreenLingo " + ApplicationIdentity.Version;
            }
            if (readyUpdate is not null && readyUpdate.Version == availableUpdate.Version) { ShowReadyUpdate(); return "Update downloaded · choose Install now on the main screen."; }
            readyUpdate = null;
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateTitle.Text = "ScreenLingo " + availableUpdate.Version + " is available";
            UpdateDetail.Text = "Download it, then choose when to install.";
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateAction.Content = "Download update";
            UpdateLater.Visibility = Visibility.Collapsed;
            return UpdateTitle.Text + " · download it from the main screen.";
        }
        catch (OperationCanceledException) when (closing) { return "Update check cancelled because ScreenLingo is closing."; }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or JsonException or IOException or InvalidDataException or ArgumentException)
        {
            ShowUpdateError("Update check failed", error);
            return "Update check failed: " + error.Message;
        }
        finally { updateBusy = false; UpdateAction.IsEnabled = true; }
    }

    private async void UpdateActionClicked(object sender, RoutedEventArgs args)
    {
        if (updateBusy) return;
        if (readyUpdate is ReadyUpdate ready)
        {
            updateBusy = true;
            UpdateAction.IsEnabled = false;
            try
            {
                Pause();
                UpdateDetail.Text = "Verifying the installer. ScreenLingo will close and Setup will guide you through installation.";
                using Process installer = await updates.StartInstallAsync(ready, AppContext.BaseDirectory, updateCancellation.Token);
                Close();
            }
            catch (OperationCanceledException) when (closing) { /* Closing cancels installation preparation before the helper starts. */ }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or ArgumentException)
            { ShowUpdateError("Installation could not start", error); }
            finally { updateBusy = false; UpdateAction.IsEnabled = true; }
            return;
        }
        if (availableUpdate is not UpdateRelease release) { await CheckForUpdatesAsync(); return; }
        updateBusy = true;
        UpdateAction.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateTitle.Text = "Downloading ScreenLingo " + release.Version;
        IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(value =>
        {
            UpdateProgress.Value = value.Received * 100.0 / value.Total;
            UpdateDetail.Text = $"{value.Received / 1048576.0:0.0} / {value.Total / 1048576.0:0.0} MB · {UpdateProgress.Value:0}%";
        });
        try
        {
            readyUpdate = await updates.DownloadAsync(release, progress, updateCancellation.Token);
            ShowReadyUpdate();
            tray.ShowBalloonTip(5000, "ScreenLingo update downloaded", "Open ScreenLingo to install now or later.", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (OperationCanceledException) when (closing) { /* Closing explicitly cancels the download. */ }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or IOException or InvalidDataException or JsonException or ArgumentException)
        { ShowUpdateError("Update download failed", error); }
        finally { updateBusy = false; UpdateAction.IsEnabled = true; }
    }

    private void ShowReadyUpdate()
    {
        ReadyUpdate ready = readyUpdate ?? throw new InvalidOperationException("No verified update is ready to install.");
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateTitle.Text = "ScreenLingo " + ready.Version + " is ready";
        UpdateDetail.Text = "Open Setup now, or keep the installer for later. Your preferences are preserved.";
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateAction.Content = "Install now";
        UpdateLater.Content = "Install later";
        UpdateLater.Visibility = Visibility.Visible;
    }

    private void UpdateLaterClicked(object sender, RoutedEventArgs args) => UpdateBanner.Visibility = Visibility.Collapsed;

    private void ShowUpdateError(string title, Exception error)
    {
        store.Warn("update_error", "github.com", error.HResult);
        UpdateBanner.Visibility = Visibility.Visible;
        UpdateTitle.Text = title;
        UpdateDetail.Text = error.Message;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateLater.Visibility = Visibility.Collapsed;
        UpdateAction.Content = readyUpdate is null ? "Try again" : "Install now";
    }
}
