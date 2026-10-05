using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenLingo;

/// <summary>Connects the desktop interface to capture, recognition and translation. Domain values remain immutable.</summary>
public partial class MainWindow : Window
{
    private readonly SettingsConnector store;
    private readonly OcrConnector ocr;
    private readonly HttpConnector onlineHttp;
    private readonly HttpConnector offlineHttp;
    private readonly DispatcherTimer targetTimer;
    private readonly DispatcherTimer scanTimer;
    private readonly OverlayWindow overlay = new();
    private readonly System.Windows.Forms.NotifyIcon tray;
    private AppSettings settings;
    private ImmutableDictionary<TranslationKey, string> cache = ImmutableDictionary<TranslationKey, string>.Empty;
    private CaptureTarget? currentTarget;
    private CaptureConnector? capture;
    private CancellationTokenSource? cancellation;
    private byte[] lastImageHash = [];
    private int generation;
    private bool enabled;
    private bool busy;
    private bool initialized;
    private bool closing;
    private bool fatalError;
    private readonly System.Collections.Generic.List<int> shortcuts = [];

    public MainWindow(SettingsConnector store, AppSettings settings)
    {
        this.store = store;
        this.settings = settings;
        updates = new UpdateConnector(Path.Combine(store.StorageDirectory, "Updates"), store.Warn);
        ThemeConnector.Apply(settings.ColorSkin);
        MotionConnector.ApplyPreferences(settings.ReduceMotion);
        ocr = new OcrConnector(Path.Combine(AppContext.BaseDirectory, "Models"));
        onlineHttp = new HttpConnector(TimeSpan.FromSeconds(12), store.Warn);
        offlineHttp = new HttpConnector(TimeSpan.FromSeconds(90), store.Warn);
        InitializeComponent();
        VersionCaption.Text = ApplicationIdentity.Version + " · preview";
        SourceLanguage.ItemsSource = new[] { new LanguageOption("auto", "Detect automatically", "", "") }.Concat(TranslationRules.Languages());
        TargetLanguage.ItemsSource = TranslationRules.Languages();
        SourceLanguage.SelectedValue = settings.SourceLanguage;
        TargetLanguage.SelectedValue = settings.TargetLanguage;
        Mode.SelectedValue = settings.Mode.ToString();
        initialized = true;
        UpdateServiceCaption();
        InitializeUpdates();
        targetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        targetTimer.Tick += ObserveTarget;
        scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(settings.ScanIntervalMs) };
        scanTimer.Tick += ScanTick;
        System.Windows.Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open ScreenLingo", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        menu.Items.Add("Toggle translation · Ctrl+Alt+T", null, (_, _) => Dispatcher.Invoke(Toggle));
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Close));
        tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "ScreenLingo · translation off", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not locate the application executable."))
                ?? throw new InvalidOperationException("Could not read the application icon."),
            ContextMenuStrip = menu, Visible = true
        };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPanel);
        UpdateModeControls();
        SourceInitialized += (_, _) =>
        {
            nint window = new WindowInteropHelper(this).Handle;
            WindowsConnector.ExcludeFromCapture(window);
            HwndSource.FromHwnd(window).AddHook(WindowMessage);
            foreach ((int id, uint key) in new[] { (1, (uint)'T'), (2, (uint)'H'), (3, (uint)0x1B) })
            {
                WindowsConnector.RegisterShortcut(window, id, 0x3, key);
                shortcuts.Add(id);
            }
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            if (Notice.IsVisible) DismissNoticeClicked(this, args);
            else if (screenshotView is not null) CloseScreenshot();
            else if (DetailView.IsVisible || AboutView.IsVisible) ShowHome();
            else if (enabled) Pause();
            else return;
            args.Handled = true;
        };
        Closing += OnClosing;
        SystemParameters.StaticPropertyChanged += WindowsPreferencesChanged;
        targetTimer.Start();
        scanTimer.Start();
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0312) return 0;
        handled = true;
        switch ((int)wParam) { case 1: if (!Notice.IsVisible) Toggle(); break; case 2: ShowPanel(); break; case 3: Pause(); break; }
        return 0;
    }

    private void ShowPanel() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ToggleClicked(object sender, RoutedEventArgs args)
    {
        if (settings.Mode == TranslationMode.Screenshot) CaptureAfterCountdown();
        else Toggle();
    }

    private void Toggle()
    {
        if (settings.Mode == TranslationMode.Screenshot) { CaptureScreenshot(); return; }
        if (enabled) { Pause(); return; }
        enabled = true;
        ToggleTranslation.Content = "Pause translation";
        tray.Text = "ScreenLingo · translation on";
        SetStatus("Translation enabled", "Switch to a game or app. Ctrl+Alt+T pauses; Ctrl+Alt+H opens this panel.");
        InvalidateCapture();
    }

    private void Pause()
    {
        enabled = false;
        InvalidateCapture();
        CloseScreenshot();
        UpdateModeControls();
        tray.Text = "ScreenLingo · translation off";
        if (settings.Mode == TranslationMode.Live)
            SetStatus("Translation paused", "Your shortcut can enable it again whenever you need it.");
    }

    private CaptureTarget? ReadTarget() => settings.Scope switch
    {
        CaptureScope.ActiveWindow => WindowsConnector.ForegroundTarget(),
        CaptureScope.WindowUnderPointer => WindowsConnector.PointerWindowTarget(),
        CaptureScope.MonitorUnderPointer => WindowsConnector.PointerMonitorTarget(),
        _ => throw new ArgumentOutOfRangeException(nameof(settings.Scope))
    };

    private void ObserveTarget(object? sender, EventArgs args)
    {
        if (!enabled) return;
        CaptureTarget? target = ReadTarget();
        if (target == currentTarget) return;
        InvalidateCapture();
        currentTarget = target;
        SetStatus(target is null ? "Waiting for an application" : "Reading the screen",
            target is null ? "Switch to your game or another app to begin." : target.Title);
    }

    private void InvalidateCapture()
    {
        generation++;
        cancellation?.Cancel();
        lastImageHash = [];
        overlay.Hide();
        if (!busy) { capture?.Dispose(); capture = null; }
    }

    private async void ScanTick(object? sender, EventArgs args)
    {
        if (!enabled || busy || currentTarget is not CaptureTarget target) return;
        busy = true;
        int startedGeneration = generation;
        AppSettings snapshot = settings;
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            capture ??= target.Kind == CaptureKind.Window ? CaptureConnector.ForWindow(target) : CaptureConnector.ForMonitor(target);
            CapturedImage image = await capture.SnapshotAsync(token);
            byte[] hash = SHA256.HashData(image.Png);
            if (hash.AsSpan().SequenceEqual(lastImageHash)) return;
            Action<string> progress = detail => SetStatus("Translating", target.Title + " · " + detail);
            (string source, ImmutableArray<TextRegion> regions) = await RecognizeImageAsync(image, snapshot, token, progress);
            CapturedImage latest = await capture.SnapshotAsync(token);
            ImmutableArray<TextRegion> stationary = await Task.Run(() => TextStability.StationaryRegions(image, latest, regions), token);
            overlay.RetainStationary(stationary);
            ScreenTranslation result = await TranslateRegionsAsync(source, stationary, snapshot, token, progress);
            if (generation != startedGeneration || ReadTarget() != target) return;
            CapturedImage presentImage = await capture.SnapshotAsync(token);
            ImmutableArray<TextRegion> stillVisible = await Task.Run(() => TextStability.StationaryRegions(image, presentImage, stationary), token);
            result = result with { Regions = result.Regions.Where(region => stillVisible.Contains(region.Source)).ToImmutableArray() };
            if (result.RecognizedCount == 0)
            {
                overlay.Hide();
                lastImageHash = hash;
                SetStatus("No readable text", $"{target.Title} · watching for changes");
                return;
            }
            if (!TranslationRules.NeedsTranslation(result.SourceLanguage, snapshot.TargetLanguage))
            {
                overlay.Hide();
                lastImageHash = hash;
                SetStatus("Already in your language", $"{target.Title} · {TranslationRules.Language(result.SourceLanguage).Name} · watching for changes");
                return;
            }
            overlay.Present(target, image, result.Regions, snapshot);
            lastImageHash = hash;
            SetStatus("Translation is on", $"{target.Title} · {result.Regions.Length} / {result.RecognizedCount} labels translated · {stopwatch.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* Window changes and the toggle explicitly cancel stale work. */ }
        catch (Exception error) when (error is HttpRequestException or JsonException or TimeoutException or COMException or InvalidOperationException or IOException)
        {
            if (generation != startedGeneration) return;
            Pause();
            SetStatus("Translation stopped", error.Message);
            ShowError("Translation stopped", error);
        }
        finally
        {
            busy = false;
            if (generation != startedGeneration) { capture?.Dispose(); capture = null; }
            if (closing) { DisposeConnectors(); Close(); }
        }
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!initialized) return;
        settings = settings with { SourceLanguage = (string)SourceLanguage.SelectedValue, TargetLanguage = (string)TargetLanguage.SelectedValue };
        store.Save(settings);
        InvalidateCapture();
        CloseScreenshot();
    }

    private void SettingsClicked(object sender, RoutedEventArgs args)
    {
        SettingsView view = new(settings, CheckForUpdatesAsync);
        view.Saved += SavePreferences;
        view.Cancelled += ShowHome;
        HomeView.Visibility = Visibility.Collapsed;
        DetailView.Content = view;
        MotionConnector.Enter(DetailView, TimeSpan.FromMilliseconds(167), 4);
    }

    private void SavePreferences(AppSettings updated)
    {
        settings = updated;
        store.Save(settings);
        scanTimer.Interval = TimeSpan.FromMilliseconds(settings.ScanIntervalMs);
        ThemeConnector.Apply(settings.ColorSkin);
        MotionConnector.ApplyPreferences(settings.ReduceMotion);
        Mode.SelectedValue = settings.Mode.ToString();
        currentTarget = null;
        InvalidateCapture();
        CloseScreenshot();
        UpdateModeControls();
        UpdateServiceCaption();
        ShowHome();
    }

    internal void ShowHome()
    {
        DetailView.Content = null;
        DetailView.Visibility = Visibility.Collapsed;
        AboutView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        MotionConnector.Enter(HomeView, TimeSpan.FromMilliseconds(167), 0);
    }

    private void BackClicked(object sender, RoutedEventArgs args) => ShowHome();

    private void UpdateServiceCaption() => ServiceCaption.Text = settings.Provider == TranslationProvider.MyMemory
        ? "Online · recognized text goes to MyMemory\nFree daily limit applies. Screenshots stay local."
        : "Offline · local Ollama model\nText and screenshots stay on your PC.";

    private void AboutClicked(object sender, RoutedEventArgs args)
    {
        HomeView.Visibility = Visibility.Collapsed;
        AboutVersion.Text = ApplicationIdentity.Name + " " + ApplicationIdentity.Version + " · preview";
        MotionConnector.Enter(AboutView, TimeSpan.FromMilliseconds(167), 4);
    }

    private void SetStatus(string title, string detail) { StatusTitle.Text = title; StatusDetail.Text = detail; }

    private void WindowsPreferencesChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            Dispatcher.Invoke(() =>
            {
                MotionConnector.ApplyPreferences(settings.ReduceMotion);
                if (UpdateSpinner.IsVisible) MotionConnector.StartSpinner(UpdateSpinner);
            });
    }

    internal void ShowError(string title, Exception error)
    {
        NoticeTitle.Text = title;
        NoticeMessage.Text = error.Message;
        NoticeDetails.Text = error.ToString();
        NoticeDetails.Visibility = Visibility.Collapsed;
        DetailsToggle.Content = "Show technical details";
        HomeView.IsEnabled = false;
        DetailView.IsEnabled = false;
        AboutView.IsEnabled = false;
        MotionConnector.Enter(Notice, TimeSpan.FromMilliseconds(167), 0);
        ShowPanel();
        DismissNotice.Focus();
    }

    internal void ShowFatalError(Exception error)
    {
        fatalError = true;
        enabled = false;
        targetTimer.Stop(); scanTimer.Stop(); updateTimer.Stop();
        cancellation?.Cancel(); updateCancellation.Cancel(); overlay.Hide();
        ShowError("ScreenLingo needs to close", error);
        DismissNotice.Content = "Close ScreenLingo";
    }

    private void DetailsClicked(object sender, RoutedEventArgs args)
    {
        NoticeDetails.Visibility = NoticeDetails.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        DetailsToggle.Content = NoticeDetails.IsVisible ? "Hide technical details" : "Show technical details";
    }

    private void CopyErrorClicked(object sender, RoutedEventArgs args) => Clipboard.SetText(NoticeDetails.Text);

    private void DismissNoticeClicked(object sender, RoutedEventArgs args)
    {
        if (fatalError) { Close(); return; }
        Notice.Visibility = Visibility.Collapsed;
        HomeView.IsEnabled = true;
        DetailView.IsEnabled = true;
        AboutView.IsEnabled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (closing) return;
        closing = true;
        SystemParameters.StaticPropertyChanged -= WindowsPreferencesChanged;
        enabled = false;
        targetTimer.Stop();
        scanTimer.Stop();
        updateTimer.Stop();
        updateCancellation.Cancel();
        cancellation?.Cancel();
        overlay.Close();
        CloseScreenshot();
        tray.Dispose();
        nint window = new WindowInteropHelper(this).Handle;
        foreach (int shortcut in shortcuts) WindowsConnector.RemoveShortcut(window, shortcut);
        if (busy) { args.Cancel = true; Hide(); return; }
        DisposeConnectors();
    }

    private void DisposeConnectors()
    {
        capture?.Dispose(); capture = null;
        ocr.Dispose(); onlineHttp.Dispose(); offlineHttp.Dispose(); cancellation?.Dispose();
        updates.Dispose(); updateCancellation.Dispose();
    }
}
