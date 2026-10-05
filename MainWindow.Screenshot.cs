using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ScreenLingo;

public partial class MainWindow
{
    private ScreenshotView? screenshotView;
    private Rect panelBounds;
    internal ScreenshotView? CurrentScreenshot => screenshotView;
    private bool countingDown;

    private void ModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized) return;
        settings = settings with { Mode = Enum.Parse<TranslationMode>((string)Mode.SelectedValue) };
        store.Save(settings);
        Pause();
    }

    private void UpdateModeControls()
    {
        bool screenshot = settings.Mode == TranslationMode.Screenshot;
        ToggleTranslation.Content = screenshot ? "Capture in 3 seconds" : enabled ? "Pause translation" : "Enable translation";
        ShortcutCaption.Text = screenshot ? "Ctrl + Alt + T  ·  capture your current app" : "Ctrl + Alt + T  ·  toggle from any app";
        if (screenshot) SetStatus("Screenshot mode", "Switch to your game and press Ctrl+Alt+T. The capture stays open while you read.");
    }

    private async void CaptureAfterCountdown()
    {
        if (busy || countingDown) { SetStatus("Capture in progress", "Wait for it to finish, or press Ctrl+Alt+Esc to cancel."); return; }
        InvalidateCapture();
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        countingDown = true;
        ToggleTranslation.IsEnabled = false;
        try
        {
            for (int seconds = 3; seconds > 0; seconds--)
            {
                SetStatus($"Capturing in {seconds}…", "Switch to your game or application now. Ctrl+Alt+Esc cancels.");
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        finally { countingDown = false; ToggleTranslation.IsEnabled = true; }
        CaptureScreenshot();
    }

    private async void CaptureScreenshot()
    {
        CaptureTarget? selected = ReadTarget();
        if (selected is not CaptureTarget target)
        {
            SetStatus("Choose the window to capture", "Switch to the game and press Ctrl+Alt+T, or use Capture in 3 seconds and switch before it finishes.");
            return;
        }
        await CaptureScreenshotAsync(target);
    }

    internal async Task CaptureScreenshotAsync(CaptureTarget target)
    {
        if (busy || countingDown) { SetStatus("Capture in progress", "Wait for it to finish, or press Ctrl+Alt+Esc to cancel."); return; }
        InvalidateCapture();
        CloseScreenshot();
        busy = true;
        int startedGeneration = generation;
        AppSettings snapshot = settings;
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Stopwatch timer = Stopwatch.StartNew();
        ToggleTranslation.IsEnabled = false;
        try
        {
            using CaptureConnector screenshotCapture = target.Kind == CaptureKind.Window
                ? CaptureConnector.ForWindow(target) : CaptureConnector.ForMonitor(target);
            CapturedImage image = await screenshotCapture.SnapshotAsync(token);
            token.ThrowIfCancellationRequested();
            ScreenshotView viewer = new(image, target.Title);
            OpenScreenshot(viewer);
            ScreenTranslation result = await TranslateImageAsync(image, snapshot, token, detail =>
            {
                viewer.SetProgress(detail);
                SetStatus("Translating screenshot", detail);
            });
            if (generation != startedGeneration) return;
            viewer.Present(result, snapshot, timer.Elapsed);
            SetStatus("Screenshot ready", $"{result.Regions.Length} / {result.RecognizedCount} labels translated · {timer.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* Closing the viewer or pausing explicitly cancels this capture. */ }
        catch (Exception error) when (error is HttpRequestException or JsonException or TimeoutException or COMException or InvalidOperationException or IOException)
        {
            if (generation != startedGeneration) return;
            SetStatus("Screenshot translation stopped", error.Message);
            screenshotView?.SetError(error.Message);
            ShowError("Screenshot translation stopped", error);
        }
        finally
        {
            busy = false;
            ToggleTranslation.IsEnabled = true;
            if (closing) { DisposeConnectors(); Close(); }
        }
    }

    internal void OpenScreenshot(ScreenshotView viewer)
    {
        CloseScreenshot();
        panelBounds = new Rect(Left, Top, Width, Height);
        screenshotView = viewer;
        viewer.CloseRequested += CloseScreenshot;
        HomeView.Visibility = Visibility.Collapsed;
        AboutView.Visibility = Visibility.Collapsed;
        DetailView.Content = viewer;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 740;
        Width = Math.Min(1060, SystemParameters.WorkArea.Width);
        Height = Math.Min(760, SystemParameters.WorkArea.Height);
        Left = Math.Clamp(panelBounds.X + (panelBounds.Width - Width) / 2, SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width);
        Top = Math.Clamp(panelBounds.Y, SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - Height);
        ShowPanel();
        MotionConnector.Enter(DetailView, TimeSpan.FromMilliseconds(167), 4);
    }

    private void CloseScreenshot()
    {
        if (screenshotView is not ScreenshotView viewer) return;
        viewer.CloseRequested -= CloseScreenshot;
        screenshotView = null;
        InvalidateCapture();
        DetailView.Content = null;
        MinWidth = 400;
        Width = panelBounds.Width; Height = panelBounds.Height;
        Left = panelBounds.X; Top = panelBounds.Y;
        ResizeMode = ResizeMode.CanMinimize;
        ShowHome();
    }
}
