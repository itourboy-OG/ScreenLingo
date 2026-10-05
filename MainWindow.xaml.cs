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
    private readonly System.Collections.Generic.List<int> shortcuts = [];

    public MainWindow(SettingsConnector store, AppSettings settings)
    {
        this.store = store;
        this.settings = settings;
        updates = new UpdateConnector(Path.Combine(store.StorageDirectory, "Updates"), store.Warn);
        ThemeConnector.Apply(settings.ColorSkin);
        ocr = new OcrConnector(Path.Combine(AppContext.BaseDirectory, "Models"));
        onlineHttp = new HttpConnector(TimeSpan.FromSeconds(12), store.Warn);
        offlineHttp = new HttpConnector(TimeSpan.FromSeconds(90), store.Warn);
        InitializeComponent();
        VersionCaption.Text = ApplicationIdentity.Version + " · preview";
        SourceLanguage.ItemsSource = new[] { new LanguageOption("auto", "Detect automatically", "", "") }.Concat(TranslationRules.Languages());
        TargetLanguage.ItemsSource = TranslationRules.Languages();
        SourceLanguage.SelectedValue = settings.SourceLanguage;
        TargetLanguage.SelectedValue = settings.TargetLanguage;
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
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && enabled) { Pause(); args.Handled = true; } };
        Closing += OnClosing;
        targetTimer.Start();
        scanTimer.Start();
    }

    private nint WindowMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0312) return 0;
        handled = true;
        switch ((int)wParam) { case 1: Toggle(); break; case 2: ShowPanel(); break; case 3: Pause(); break; }
        return 0;
    }

    private void ShowPanel() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void ToggleClicked(object sender, RoutedEventArgs args) => Toggle();

    private void Toggle()
    {
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
        ToggleTranslation.Content = "Enable translation";
        tray.Text = "ScreenLingo · translation off";
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
            overlay.Hide();
            ImmutableArray<TextRegion> regions = await Task.Run(() => ocr.Recognize(image, snapshot.SourceLanguage), token);
            token.ThrowIfCancellationRequested();
            if (regions.IsEmpty)
            {
                lastImageHash = hash;
                SetStatus("No readable text", $"{target.Title} · watching for changes");
                return;
            }
            string source = snapshot.SourceLanguage == "auto" ? await Task.Run(() => ocr.DetectLanguage(regions), token) : snapshot.SourceLanguage;
            if (snapshot.SourceLanguage == "auto" && source is "zh-CN" or "ja" or "ko")
                regions = await Task.Run(() => ocr.Recognize(image, source), token);
            token.ThrowIfCancellationRequested();
            if (!TranslationRules.NeedsTranslation(source, snapshot.TargetLanguage))
            {
                lastImageHash = hash;
                SetStatus("Already in your language", $"{target.Title} · {TranslationRules.Language(source).Name} · watching for changes");
                return;
            }
            string provider = snapshot.Provider == TranslationProvider.Ollama ? "Ollama:" + snapshot.OllamaModel : "MyMemory";
            ImmutableArray<TranslationKey> keys = regions.Select(region => new TranslationKey(provider, source, snapshot.TargetLanguage, region.Text)).ToImmutableArray();
            ImmutableArray<TranslationKey> missing = keys.Distinct().Where(key => !cache.ContainsKey(key)).ToImmutableArray();
            if (!missing.IsEmpty)
            {
                SetStatus("Translating", $"{target.Title} · {TranslationRules.Language(source).Name} → {TranslationRules.Language(snapshot.TargetLanguage).Name}");
                ITranslator translator = snapshot.Provider == TranslationProvider.MyMemory
                    ? new MyMemoryConnector(onlineHttp) : new OllamaConnector(offlineHttp, snapshot.OllamaModel);
                ImmutableArray<string> translated = await translator.TranslateAsync(missing.Select(key => key.Text).ToImmutableArray(), source, snapshot.TargetLanguage, token);
                token.ThrowIfCancellationRequested();
                cache = TranslationRules.MergeCache(cache, missing, translated);
            }
            if (generation != startedGeneration || ReadTarget() != target) return;
            ImmutableArray<TranslatedRegion> labels = regions.Select((region, index) => new TranslatedRegion(region, cache[keys[index]]))
                .Where(region => !string.Equals(region.Source.Text, region.Translation, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
            overlay.Present(target, image, labels, snapshot);
            // ponytail: keep 512 session labels; add LRU only if real game sessions exhaust this cache.
            cache = cache.Take(512).ToImmutableDictionary();
            lastImageHash = hash;
            SetStatus("Translation is on", $"{target.Title} · {labels.Length} labels · {stopwatch.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* Window changes and the toggle explicitly cancel stale work. */ }
        catch (Exception error) when (error is HttpRequestException or JsonException or TimeoutException or COMException or InvalidOperationException or IOException)
        {
            if (generation != startedGeneration) return;
            Pause();
            SetStatus("Translation stopped", error.Message);
            MessageBox.Show(this, error.ToString(), "ScreenLingo · Translation error", MessageBoxButton.OK, MessageBoxImage.Error);
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
    }

    private void SettingsClicked(object sender, RoutedEventArgs args)
    {
        SettingsWindow dialog = new(settings, CheckForUpdatesAsync) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not AppSettings updated) return;
        settings = updated;
        store.Save(settings);
        scanTimer.Interval = TimeSpan.FromMilliseconds(settings.ScanIntervalMs);
        ThemeConnector.Apply(settings.ColorSkin);
        currentTarget = null;
        InvalidateCapture();
        UpdateServiceCaption();
    }

    private void UpdateServiceCaption() => ServiceCaption.Text = settings.Provider == TranslationProvider.MyMemory
        ? "Online · recognized text goes to MyMemory\nFree daily limit applies. Screenshots stay local."
        : "Offline · local Ollama model\nText and screenshots stay on your PC.";

    private void AboutClicked(object sender, RoutedEventArgs args) => MessageBox.Show(this,
        ApplicationIdentity.Name + " " + ApplicationIdentity.Version + " · local preview\n\nTranslate games and applications with Ctrl+Alt+T.\nCtrl+Alt+H opens the panel. Ctrl+Alt+Esc pauses.\nMinimize to keep the app in the system tray.\n\nAutomatic source detection uses the visible text. Short labels can be ambiguous; select a source language manually when needed.\n\nWindowed and borderless games are initial test targets. Exclusive full-screen visibility depends on the game and must be tested. Protected content can block capture.",
        "About ScreenLingo", MessageBoxButton.OK, MessageBoxImage.Information);

    private void SetStatus(string title, string detail) { StatusTitle.Text = title; StatusDetail.Text = detail; }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (closing) return;
        closing = true;
        enabled = false;
        targetTimer.Stop();
        scanTimer.Stop();
        updateTimer.Stop();
        updateCancellation.Cancel();
        cancellation?.Cancel();
        overlay.Close();
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
