using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenLingo;

public static partial class SmokeCheck
{
    /// <summary>Exercises native update animations with a real public installer download; never starts Setup.</summary>
    public static async Task<int> RunMotionAsync(string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        MainWindow? control = null;
        try
        {
            SettingsConnector store = new(Path.Combine(reportDirectory, "preferences"));
            AppSettings initial = TranslationRules.InitialSettings();
            store.Save(initial);
            control = new MainWindow(store, initial);
            control.Show();
            await Task.Delay(300);
            using UpdateConnector connector = new(Path.Combine(store.StorageDirectory, "Updates"), store.Warn);
            UpdateRelease release = await connector.CheckAsync(new Version(0, 2, 0), CancellationToken.None)
                ?? throw new InvalidOperationException("The public feed must contain an installer newer than 0.2.0.");
            Task<string> check = control.CheckForUpdatesAsync();
            Require(control.UpdateSpinner.IsVisible, "An actual update check must show its in-app activity indicator.");
            await check;
            await Task.Delay(200);
            Require(!control.UpdateSpinner.IsVisible, "The activity animation must stop when checking finishes.");
            control.ShowAvailableUpdate(release);
            await Task.Delay(70);
            if (SystemParameters.ClientAreaAnimation)
                Require(control.UpdateBanner.Opacity is > 0 and < 1, "The update card must fade in before reaching its final opacity.");
            await Task.Delay(250);
            Require(control.UpdateBanner.Opacity == 1 && ((TranslateTransform)control.UpdateBanner.RenderTransform).Y == 0,
                "The update entrance must finish at the normal layout position and opacity.");
            Require(control.UpdateLater.IsVisible && (string)control.UpdateLater.Content == "Download later",
                "An available update must offer Download later before downloading.");
            SaveVisual(control.UpdateBanner, Path.Combine(reportDirectory, "update-available.png"));
            control.UpdateLater.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            Require(!control.UpdateBanner.IsVisible, "Download later must dismiss the banner without downloading or installing.");
            control.ShowAvailableUpdate(release);
            Task download = control.DownloadUpdateAsync(release);
            int frame = 0;
            while (!download.IsCompleted)
            {
                await Task.Delay(150);
                if (!download.IsCompleted) SaveVisual(control.UpdateBanner, Path.Combine(reportDirectory, $"download-{frame++:D3}.png"));
            }
            await download;
            await Task.Delay(250);
            Require(control.UpdateProgress.Value == 100 && control.UpdateComplete.IsVisible && (string)control.UpdateAction.Content == "Install now",
                "A verified real download must finish at 100% and show the ready state inside the app.");
            ReadyUpdate ready = connector.LoadReady(new Version(0, 2, 0))
                ?? throw new InvalidOperationException("The real download must leave a verified installer ready for later.");
            SaveVisual(control.UpdateBanner, Path.Combine(reportDirectory, "update-ready.png"));
            control.UpdateLater.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(200);
            Require(!control.UpdateBanner.IsVisible && connector.LoadReady(new Version(0, 2, 0)) == ready,
                "Install later must dismiss the card while retaining the verified installer.");
            control.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettingsView preferences = (SettingsView)control.DetailView.Content;
            preferences.ReducedMotion.IsChecked = true;
            preferences.SaveChanges.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            control.ShowAvailableUpdate(release);
            Require(store.Load().ReduceMotion && !MotionConnector.GetEnabled(control.UpdateAction) && control.UpdateBanner.Opacity == 1 &&
                ((TranslateTransform)control.UpdateBanner.RenderTransform).Y == 0,
                "Saving Reduce motion must disable inherited control animations and make the entrance immediate.");
            try { control.PresentDownloadProgress(new DownloadProgress(-1, release.Size)); }
            catch (ArgumentOutOfRangeException error) { control.ShowError("Download progress rejected", error); }
            await Task.Delay(100);
            Require(control.Notice.IsVisible && !control.HomeView.IsEnabled &&
                Application.Current.Windows.OfType<Window>().Count(window => window.IsVisible) == 1,
                "Errors must retain their root cause inside the single app window, with background controls disabled.");
            SaveVisual(control, Path.Combine(reportDirectory, "in-app-error.png"));
            control.DismissNotice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!control.Notice.IsVisible && control.HomeView.IsEnabled, "Dismissing an error must restore the same app page.");
            File.WriteAllText(Path.Combine(reportDirectory, "motion-report.json"), JsonSerializer.Serialize(new CheckResult("native motion and in-app update flow", true,
                "Actual GitHub check and installer download, banner entrance/exit, real progress, ready checkmark, Download later, Install later, saved reduced motion and in-app error handling passed. Setup was never started.")));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(reportDirectory, "motion-report.json"), JsonSerializer.Serialize(new CheckResult("native motion and in-app update flow", false, error.ToString())));
            return 1;
        }
        finally { control?.Close(); }
    }
}
