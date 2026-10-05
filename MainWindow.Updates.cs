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
        UpdateAction.Visibility = Visibility.Collapsed;
        UpdateLater.Visibility = Visibility.Collapsed;
        UpdateComplete.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateTitle.Text = "Checking for updates…";
        UpdateDetail.Text = "Looking for a new ScreenLingo release on GitHub.";
        RevealUpdateBanner();
        MotionConnector.StartSpinner(UpdateSpinner);
        try
        {
            availableUpdate = null;
            availableUpdate = await updates.CheckAsync(Version.Parse(ApplicationIdentity.Version), updateCancellation.Token);
            if (availableUpdate is null)
            {
                if (readyUpdate is not null) ShowReadyUpdate();
                else MotionConnector.Dismiss(UpdateBanner);
                return "You are up to date · ScreenLingo " + ApplicationIdentity.Version;
            }
            if (readyUpdate is not null && readyUpdate.Version == availableUpdate.Version) { ShowReadyUpdate(); return "Update downloaded · choose Install now on the main screen."; }
            readyUpdate = null;
            ShowAvailableUpdate(availableUpdate);
            return UpdateTitle.Text + " · download it from the main screen.";
        }
        catch (OperationCanceledException) when (closing || fatalError) { return "Update check cancelled because ScreenLingo is stopping."; }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or JsonException or IOException or InvalidDataException or ArgumentException)
        {
            ShowUpdateError("Update check failed", error);
            return "Update check failed: " + error.Message;
        }
        finally { MotionConnector.StopSpinner(UpdateSpinner); updateBusy = false; UpdateAction.IsEnabled = true; }
    }

    internal void ShowAvailableUpdate(UpdateRelease release)
    {
        availableUpdate = release;
        readyUpdate = null;
        UpdateTitle.Text = "ScreenLingo " + release.Version + " is available";
        UpdateDetail.Text = "Download it now, or come back later. Installation is your choice.";
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateComplete.Visibility = Visibility.Collapsed;
        UpdateAction.Content = "Download update";
        UpdateAction.Visibility = Visibility.Visible;
        UpdateLater.Content = "Download later";
        UpdateLater.Visibility = Visibility.Visible;
        RevealUpdateBanner();
    }

    private void RevealUpdateBanner()
    {
        if (UpdateBanner.Visibility != Visibility.Visible) MotionConnector.Enter(UpdateBanner, TimeSpan.FromMilliseconds(250), 8);
        else MotionConnector.Enter(UpdateContent, TimeSpan.FromMilliseconds(167), 0);
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
        await DownloadUpdateAsync(release);
    }

    internal async Task DownloadUpdateAsync(UpdateRelease release)
    {
        updateBusy = true;
        UpdateAction.IsEnabled = false;
        UpdateLater.Visibility = Visibility.Collapsed;
        UpdateComplete.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        UpdateProgress.Value = 0;
        UpdateTitle.Text = "Downloading ScreenLingo " + release.Version;
        UpdateAction.Content = "Downloading…";
        IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(PresentDownloadProgress);
        try
        {
            readyUpdate = await updates.DownloadAsync(release, progress, updateCancellation.Token);
            ShowReadyUpdate();
        }
        catch (OperationCanceledException) when (closing) { /* Closing explicitly cancels the download. */ }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or IOException or InvalidDataException or JsonException or ArgumentException)
        { ShowUpdateError("Update download failed", error); }
        finally { updateBusy = false; UpdateAction.IsEnabled = true; }
    }

    internal void PresentDownloadProgress(DownloadProgress progress)
    {
        if (progress.Total <= 0 || progress.Received < 0 || progress.Received > progress.Total)
            throw new ArgumentOutOfRangeException(nameof(progress), "Download progress must be within the published installer size.");
        double percent = progress.Received * 100.0 / progress.Total;
        MotionConnector.ShowProgress(UpdateProgress, percent);
        UpdateDetail.Text = progress.Received == progress.Total ? "Download complete · verifying the installer…"
            : $"{progress.Received / 1048576.0:0.0} / {progress.Total / 1048576.0:0.0} MB · {percent:0}%";
    }

    private void ShowReadyUpdate()
    {
        ReadyUpdate ready = readyUpdate ?? throw new InvalidOperationException("No verified update is ready to install.");
        RevealUpdateBanner();
        UpdateTitle.Text = "ScreenLingo " + ready.Version + " is ready";
        UpdateDetail.Text = "Open Setup now, or keep the installer for later. Your preferences are preserved.";
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateAction.Content = "Install now";
        UpdateAction.Visibility = Visibility.Visible;
        UpdateLater.Content = "Install later";
        UpdateLater.Visibility = Visibility.Visible;
        MotionConnector.Enter(UpdateComplete, TimeSpan.FromMilliseconds(167), 0);
    }

    private void UpdateLaterClicked(object sender, RoutedEventArgs args) => MotionConnector.Dismiss(UpdateBanner);

    private void ShowUpdateError(string title, Exception error)
    {
        store.Warn("update_error", "github.com", error.HResult);
        RevealUpdateBanner();
        UpdateTitle.Text = title;
        UpdateDetail.Text = error.Message;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateComplete.Visibility = Visibility.Collapsed;
        UpdateLater.Content = "Close";
        UpdateLater.Visibility = Visibility.Visible;
        UpdateAction.Visibility = Visibility.Visible;
        UpdateAction.Content = readyUpdate is null ? "Try again" : "Install now";
    }
}
