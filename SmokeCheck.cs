using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLingo;

/// <summary>Runs real capture, OCR, language-detection, online translation and overlay checks against synthetic menu windows.</summary>
public static partial class SmokeCheck
{
    /// <summary>Exercises the actual preferences dialog, live theme resources, overlays and saved-settings migration without network calls.</summary>
    public static async Task<int> RunAppearanceAsync(string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        ImmutableArray<CheckResult>.Builder results = ImmutableArray.CreateBuilder<CheckResult>();
        MainWindow? control = null;
        OverlayWindow? overlay = null;
        try
        {
            SettingsConnector store = new(Path.Combine(reportDirectory, "preferences"));
            AppSettings initial = TranslationRules.InitialSettings();
            store.Save(initial);
            JsonObject legacy = JsonNode.Parse(JsonSerializer.Serialize(initial)) as JsonObject
                ?? throw new JsonException("Could not construct the legacy preferences check.");
            legacy.Remove(nameof(AppSettings.ColorSkin));
            legacy.Remove(nameof(AppSettings.Mode));
            File.WriteAllText(Path.Combine(reportDirectory, "preferences", "settings.json"), legacy.ToJsonString());
            Require(store.Load() == initial, "Earlier preferences must retain all choices and acquire the Copper skin and Live mode.");
            results.Add(new CheckResult("previous preferences migration", true, "Existing language, service and accessibility choices preserved."));

            control = new MainWindow(store, store.Load());
            control.Show();
            control.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettingsView modeView = (SettingsView)control.DetailView.Content;
            modeView.Mode.SelectedValue = nameof(TranslationMode.Screenshot);
            modeView.SaveChanges.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(store.Load().Mode == TranslationMode.Screenshot && (string)control.Mode.SelectedValue == nameof(TranslationMode.Screenshot),
                "Saving Screenshot mode must persist the choice and update the main panel.");
            Require((string)control.ToggleTranslation.Content == "Capture in 3 seconds", "Screenshot mode must offer a capture button instead of a live toggle.");
            control.Mode.SelectedValue = nameof(TranslationMode.Live);
            Require(store.Load().Mode == TranslationMode.Live, "The main mode selector must save Live mode too.");
            results.Add(new CheckResult("screenshot mode preferences", true, "Mode saved through Settings and the main panel; existing preferences preserved."));
            await Task.Delay(200);
            Color originalBackground = ((SolidColorBrush)control.Background).Color;
            foreach (ColorSkin skin in Enum.GetValues<ColorSkin>())
            {
                control.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SettingsView dialog = (SettingsView)control.DetailView.Content;
                dialog.Skin.SelectedValue = skin.ToString();
                dialog.SaveChanges.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AppSettings saved = store.Load();
                Require(saved.ColorSkin == skin, $"The actual Settings dialog must save the {skin} skin.");
                Require(saved.SourceLanguage == initial.SourceLanguage && saved.TargetLanguage == initial.TargetLanguage,
                    "Changing skins must preserve language choices.");
                await Task.Delay(100);
                if (!SystemParameters.HighContrast && skin != ColorSkin.Copper)
                    Require(((SolidColorBrush)control.Background).Color != originalBackground, "Saving a new skin must change the open control window immediately.");
                SaveVisual(control, Path.Combine(reportDirectory, skin.ToString().ToLowerInvariant() + "-preview.png"));

                control.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SettingsView preview = (SettingsView)control.DetailView.Content;
                Require(Application.Current.Windows.OfType<Window>().Count(window => window.IsVisible) == 1,
                    "Settings must stay inside the only visible application window.");
                await Task.Delay(100);
                Require((string)preview.Skin.SelectedValue == skin.ToString(), "Reopening Settings must select the saved skin.");
                SaveVisual(control, Path.Combine(reportDirectory, skin.ToString().ToLowerInvariant() + "-settings.png"));
                preview.SettingsScroll.ScrollToEnd();
                await Task.Delay(100);
                Require(preview.SettingsScroll.VerticalOffset > 0, "Settings must scroll to the accessibility and update controls.");
                IRangeValueProvider range = new SliderAutomationPeer(preview.TextSize).GetPattern(PatternInterface.RangeValue) as IRangeValueProvider
                    ?? throw new InvalidOperationException("The styled text-size slider must expose its native accessibility range.");
                range.SetValue(20);
                Require(preview.TextSize.Value == 20, "The styled slider must accept changes through its native control behavior.");
                IRangeValueProvider opacity = new SliderAutomationPeer(preview.BackgroundOpacity).GetPattern(PatternInterface.RangeValue) as IRangeValueProvider
                    ?? throw new InvalidOperationException("The styled opacity slider must expose its native accessibility range.");
                opacity.SetValue(0.85);
                Require(preview.BackgroundOpacity.Value == 0.85, "The styled opacity slider must accept its native range value.");
                IToggleProvider toggle = new CheckBoxAutomationPeer(preview.ReducedMotion).GetPattern(PatternInterface.Toggle) as IToggleProvider
                    ?? throw new InvalidOperationException("The styled checkbox must expose its native accessibility toggle.");
                toggle.Toggle();
                Require(preview.ReducedMotion.IsChecked == !saved.ReduceMotion, "The styled checkbox must toggle its actual value.");
                await Task.Delay(100);
                if (skin == ColorSkin.Copper) SaveVisual(control, Path.Combine(reportDirectory, "copper-settings-bottom.png"));
                SaveVisual(control, Path.Combine(reportDirectory, skin.ToString().ToLowerInvariant() + "-controls.png"));
                preview.Provider.SelectedValue = nameof(TranslationProvider.Ollama);
                preview.SettingsScroll.ScrollToHome();
                await Task.Delay(100);
                IValueProvider modelName = new TextBoxAutomationPeer(preview.LocalModel).GetPattern(PatternInterface.Value) as IValueProvider
                    ?? throw new InvalidOperationException("The styled model-name field must expose its native accessible value.");
                modelName.SetValue("qwen3:8b");
                Require(preview.LocalModel.Text == "qwen3:8b", "The styled model-name field must remain editable.");
                modelName.SetValue(saved.OllamaModel);
                if (skin == ColorSkin.Copper) SaveVisual(control, Path.Combine(reportDirectory, "copper-offline-settings.png"));
                control.ShowHome();
                results.Add(new CheckResult(skin + " preferences and live appearance", true, "Saved through the real dialog; choice retained; styled sliders, checkboxes and scrolling remain accessible and functional."));
            }
            control.Close();
            control = new MainWindow(store, store.Load());
            control.Show();
            control.AboutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(control.AboutView.IsVisible && Application.Current.Windows.OfType<Window>().Count(window => window.IsVisible) == 1,
                "About must open within the main application window.");
            SaveVisual(control, Path.Combine(reportDirectory, "about-preview.png"));
            control.ShowHome();
            results.Add(new CheckResult("embedded About and Settings", true, "About and Settings use the single main window."));
            await Task.Delay(100);
            Require(((SolidColorBrush)control.Background).Color == ((SolidColorBrush)Application.Current.Resources["Backdrop"]).Color,
                "A new application window must use the persisted skin.");
            results.Add(new CheckResult("appearance after relaunch", true, "New window loads the persisted skin."));

            CaptureTarget target = WindowsConnector.TestWindowTarget(new WindowInteropHelper(control).Handle);
            overlay = new OverlayWindow();
            overlay.Present(target, new CapturedImage([], target.Bounds.Width, target.Bounds.Height),
                [new(new TextRegion("Settings", new PixelRect(30, 30, 120, 24), 1), "Configuración")], store.Load());
            Border label = ((Canvas)overlay.Content).Children.OfType<Border>().Single();
            Require(((SolidColorBrush)((TextBlock)label.Child).Foreground).Color == ((SolidColorBrush)Application.Current.Resources["Ink"]).Color,
                "Translation labels must use the selected skin's text color.");
            Require(((SolidColorBrush)label.Background).Color == ((SolidColorBrush)Application.Current.Resources["Card"]).Color,
                "Translation label backgrounds must use the selected skin.");
            results.Add(new CheckResult("overlay appearance", true, "Translation text and background follow the saved skin."));
            File.WriteAllText(Path.Combine(reportDirectory, "appearance-report.json"), JsonSerializer.Serialize(results.ToImmutable(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            results.Add(new CheckResult("failure", false, error.ToString()));
            File.WriteAllText(Path.Combine(reportDirectory, "appearance-report.json"), JsonSerializer.Serialize(results.ToImmutable(), new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally
        {
            overlay?.Close();
            control?.Close();
        }
    }

    public static async Task<int> RunAsync(string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        ImmutableArray<CheckResult>.Builder results = ImmutableArray.CreateBuilder<CheckResult>();
        Window? targetWindow = null;
        MainWindow? control = null;
        OverlayWindow? overlay = null;
        ScreenshotWindow? screenshot = null;
        try
        {
            Require(!TranslationRules.NeedsTranslation("en", "en"), "Text in the target language must skip translation.");
            Require(TranslationRules.NeedsTranslation("en", "es"), "English text must translate to Spanish.");
            string longText = string.Concat(Enumerable.Repeat("设置🎮acción ", 100));
            ImmutableArray<string> parts = TranslationRules.SplitForMyMemory(longText);
            Require(string.Concat(parts) == longText && parts.All(part => Encoding.UTF8.GetByteCount(part) <= 500), "UTF-8 request chunks must preserve characters and respect the API byte limit.");
            ImmutableDictionary<TranslationKey, string> original = ImmutableDictionary<TranslationKey, string>.Empty;
            TranslationKey key = new("test", "en", "es", "Settings");
            ImmutableDictionary<TranslationKey, string> merged = TranslationRules.MergeCache(original, [key], ["Configuración"]);
            Require(original.IsEmpty && merged[key] == "Configuración", "Cache updates must not modify the input cache.");
            results.Add(new CheckResult("translation rules and Unicode", true, "Passed"));

            SettingsConnector store = new(Path.Combine(reportDirectory, "preferences"));
            AppSettings settings = TranslationRules.InitialSettings();
            store.Save(settings);
            Require(store.Load() == settings, "Preferences must survive saving and reloading.");
            control = new MainWindow(store, settings);
            control.Show();
            await Task.Delay(250);
            SaveVisual(control, Path.Combine(reportDirectory, "screenlingo-preview.png"));
            control.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100);
            SaveVisual(control, Path.Combine(reportDirectory, "settings-preview.png"));
            control.ShowHome();
            control.Hide();
            results.Add(new CheckResult("interface, tray and global shortcuts", true, "Main window and preferences rendered; shortcuts registered."));

            targetWindow = MenuWindow("ScreenLingo English menu check", ["Settings", "Audio volume", "Graphics quality", "Controls", "Back"]);
            targetWindow.Show();
            await Task.Delay(300);
            CaptureTarget target = WindowsConnector.TestWindowTarget(new WindowInteropHelper(targetWindow).Handle);
            using OcrConnector ocr = new(Path.Combine(AppContext.BaseDirectory, "Models"));
            CapturedImage image;
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (CaptureConnector capture = CaptureConnector.ForWindow(target))
            {
                image = await capture.SnapshotAsync(CancellationToken.None);
                File.WriteAllBytes(Path.Combine(reportDirectory, "captured-english-menu.png"), image.Png);
                ImmutableArray<TextRegion> english = await Task.Run(() => ocr.Recognize(image, "en"));
                Require(english.Any(region => region.Text.Contains("Settings", StringComparison.OrdinalIgnoreCase)), "Real window capture must recognize the Settings label.");
                results.Add(new CheckResult("window capture and English OCR", true, $"{image.Width}×{image.Height}; {english.Length} lines; {stopwatch.Elapsed.TotalSeconds:0.00}s"));
                TextBlock moving = ((StackPanel)targetWindow.Content).Children.OfType<TextBlock>().First();
                moving.RenderTransform = new TranslateTransform(40, 0);
                await Task.Delay(200);
                CapturedImage shifted = await capture.SnapshotAsync(CancellationToken.None);
                ImmutableArray<TextRegion> stationary = TextStability.StationaryRegions(image, shifted, english);
                Require(!stationary.Any(region => region.Text.Contains("Settings", StringComparison.OrdinalIgnoreCase)) &&
                    stationary.Any(region => region.Text.Contains("Audio", StringComparison.OrdinalIgnoreCase)),
                    "Live motion filtering must reject moved text while keeping stationary menu labels.");
                moving.RenderTransform = Transform.Identity;
                results.Add(new CheckResult("moving-text rejection", true, "Real capture pair rejects a moved Settings label and retains stationary Audio text before translation."));

                stopwatch.Restart();
                ImmutableArray<TextRegion> automatic = await Task.Run(() => ocr.Recognize(image, "auto"));
                string detected = ocr.DetectLanguage(automatic);
                Require(detected == "en", $"Automatic detection must recognize the English menu, received {detected}.");
                results.Add(new CheckResult("automatic English recognition", true, $"{automatic.Length} lines; {stopwatch.Elapsed.TotalSeconds:0.00}s"));

                using HttpConnector http = new(TimeSpan.FromSeconds(12), store.Warn);
                MyMemoryConnector translator = new(http);
                ImmutableArray<TextRegion> labels = english.Where(region => !region.Text.Replace(" ", "", StringComparison.Ordinal).Contains("ScreenLingo", StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
                stopwatch.Restart();
                ImmutableArray<string> translations = await translator.TranslateAsync(labels.Select(region => region.Text).ToImmutableArray(), "en", "es", CancellationToken.None);
                Require(translations.Length == labels.Length && translations.Any((string text) => text != "Settings"), "Online translation must return all labels.");
                results.Add(new CheckResult("real English to Spanish API", true, $"{labels.Length} labels; {stopwatch.Elapsed.TotalSeconds:0.00}s; {string.Join(" / ", translations)}"));
                overlay = new OverlayWindow();
                overlay.Present(target, image, labels.Zip(translations, (line, text) => new TranslatedRegion(line, text)).ToImmutableArray(), settings);
                await Task.Delay(200);
                Require(WindowsConnector.RootWindowAt(target.Bounds.X + 50, target.Bounds.Y + 80) == target.Handle,
                    "The native hit test must see the menu window through the translation overlay.");
                SaveVisual(overlay, Path.Combine(reportDirectory, "spanish-overlay.png"));
                CapturedImage recaptured = await capture.SnapshotAsync(CancellationToken.None);
                ImmutableArray<TextRegion> originalLines = await Task.Run(() => ocr.Recognize(recaptured, "en"));
                Require(originalLines.Any(region => region.Text.Contains("Settings", StringComparison.OrdinalIgnoreCase)), "The overlay must be excluded from capture so recognition still sees the original text.");
                results.Add(new CheckResult("click-through overlay and capture exclusion", true, "Native overlay displayed; recapture still recognizes the original Settings label."));
                overlay.Hide();
                screenshot = new ScreenshotWindow(image, target.Title);
                screenshot.Show();
                ImmutableArray<TranslatedRegion> screenshotLabels = labels.Zip(translations, (line, text) => new TranslatedRegion(line, text))
                    .Where(region => !string.Equals(region.Source.Text, region.Translation, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
                screenshot.Present(new ScreenTranslation("en", labels.Length, screenshotLabels), settings, stopwatch.Elapsed);
                await Task.Delay(100);
                ImageSource frozenImage = screenshot.CaptureImage.Source;
                targetWindow.Activate();
                await Task.Delay(100);
                Require(screenshot.IsVisible && ReferenceEquals(frozenImage, screenshot.CaptureImage.Source),
                    "Changing focus must keep the captured image and translated reading window visible.");
                Require(screenshot.TranslationList.Children.Count == screenshotLabels.Length, "The reading list must contain every translated menu line.");
                screenshot.Zoom.SelectedValue = "1";
                Require(screenshot.ImageView.Width == image.Width, "100% zoom must show the original captured width.");
                screenshot.Zoom.SelectedValue = "2";
                await Task.Delay(100);
                screenshot.PanTo(new Point(100, 140));
                await Task.Delay(100);
                Require(screenshot.Viewport.HorizontalOffset == 100 && screenshot.Viewport.VerticalOffset == 140,
                    "Screenshot panning must move the native viewport on both axes.");
                Point pointer = new(screenshot.Viewport.ViewportWidth / 2, screenshot.Viewport.ViewportHeight / 2);
                Point anchored = screenshot.Viewport.TranslatePoint(pointer, screenshot.CaptureSurface);
                screenshot.ZoomAt(3, pointer);
                await Task.Delay(100);
                Require(screenshot.ImageView.Width == image.Width * 3 &&
                    (screenshot.Viewport.TranslatePoint(pointer, screenshot.CaptureSurface) - anchored).Length < 2,
                    "Pointer zoom must preserve the image location under the pointer.");
                screenshot.PanTo(new Point(-100, -100));
                await Task.Delay(100);
                Require(screenshot.Viewport.HorizontalOffset == 0 && screenshot.Viewport.VerticalOffset == 0,
                    "Panning must stop at the captured image bounds.");
                screenshot.ShowLabels.IsChecked = false;
                Require(screenshot.Labels.Visibility == Visibility.Collapsed, "The original screenshot must remain available without overlay labels.");
                screenshot.ShowLabels.IsChecked = true;
                screenshot.Zoom.SelectedValue = "fit";
                SaveVisual(screenshot, Path.Combine(reportDirectory, "screenshot-preview.png"));
                screenshot.Close();
                screenshot = null;
                results.Add(new CheckResult("frozen screenshot reading window", true, "Real capture and translations retained across focus changes; pointer-anchored zoom, two-axis panning, bounds and original-image controls passed."));
            }

            targetWindow.Close();
            targetWindow = MenuWindow("ScreenLingo Chinese menu check", ["设置", "音量", "画质", "游戏设置", "选择语言", "返回", "开", "关"]);
            targetWindow.Show();
            await Task.Delay(200);
            target = WindowsConnector.TestWindowTarget(new WindowInteropHelper(targetWindow).Handle);
            using (CaptureConnector capture = CaptureConnector.ForWindow(target))
            {
                image = await capture.SnapshotAsync(CancellationToken.None);
                File.WriteAllBytes(Path.Combine(reportDirectory, "captured-chinese-menu.png"), image.Png);
                ImmutableArray<TextRegion> chinese = await Task.Run(() => ocr.Recognize(image, "zh-CN"));
                Require(chinese.Any(region => region.Text.Replace(" ", "", StringComparison.Ordinal).Contains("设置", StringComparison.Ordinal)), "Chinese OCR must recognize the Settings label.");
                Require(chinese.Count(region => region.Text is "开" or "关") == 2,
                    "Chinese OCR must recognize both isolated single-character On/Off labels without duplicates.");
                ImmutableArray<TextRegion> automatic = await Task.Run(() => ocr.Recognize(image, "auto"));
                string detected = ocr.DetectLanguage(automatic);
                Require(detected == "zh-CN", $"Automatic detection must recognize the Chinese menu: detected={detected}; text={string.Join(" / ", automatic.Select(region => region.Text))}.");
                using HttpConnector http = new(TimeSpan.FromSeconds(12), store.Warn);
                ImmutableArray<string> translated = await new MyMemoryConnector(http).TranslateAsync(["设置", "音量", "游戏设置"], "zh-CN", "en", CancellationToken.None);
                Require(translated[0].Equals("Settings", StringComparison.OrdinalIgnoreCase), $"Chinese menu label 设置 must translate as Settings; received '{translated[0]}'.");
                results.Add(new CheckResult("Chinese OCR, automatic detection and real translation", true, $"{chinese.Length} lines; 设置 → {translated[0]}"));
            }

            targetWindow.WindowStyle = WindowStyle.None;
            targetWindow.WindowState = WindowState.Maximized;
            await Task.Delay(200);
            target = WindowsConnector.TestWindowTarget(new WindowInteropHelper(targetWindow).Handle);
            using (CaptureConnector capture = CaptureConnector.ForWindow(target))
            {
                image = await capture.SnapshotAsync(CancellationToken.None);
                Require(image.Width > 400 && image.Height > 400, "Borderless capture must return the visible menu.");
                results.Add(new CheckResult("borderless window capture", true, $"{image.Width}×{image.Height}; exclusive full-screen games still require live testing."));
            }
            using (CaptureConnector capture = CaptureConnector.ForMonitor(WindowsConnector.PointerMonitorTarget()))
            {
                CapturedImage monitorImage = await capture.SnapshotAsync(CancellationToken.None);
                Require(monitorImage.Width > 0 && monitorImage.Height > 0, "Monitor capture must return screen pixels.");
                results.Add(new CheckResult("monitor capture", true, $"{monitorImage.Width}×{monitorImage.Height}; monitor contents were not saved or sent to translation."));
            }
            File.WriteAllText(Path.Combine(reportDirectory, "smoke-report.json"), JsonSerializer.Serialize(results.ToImmutable(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            results.Add(new CheckResult("failure", false, error.ToString()));
            File.WriteAllText(Path.Combine(reportDirectory, "smoke-report.json"), JsonSerializer.Serialize(results.ToImmutable(), new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally
        {
            overlay?.Close();
            screenshot?.Close();
            targetWindow?.Close();
            control?.Close();
        }
    }

    private static Window MenuWindow(string title, ImmutableArray<string> labels)
    {
        StackPanel content = new() { Margin = new Thickness(32) };
        foreach (string label in labels) content.Children.Add(new TextBlock
        { Text = label, FontSize = 28, Foreground = Brushes.White, Margin = new Thickness(0, 10, 0, 12) });
        return new Window { Title = title, Width = 590, Height = 590, Left = 80, Top = 80, Background = new SolidColorBrush(Color.FromRgb(21, 31, 48)), Content = content };
    }

    private static void SaveVisual(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        DpiScale dpi = VisualTreeHelper.GetDpi(element);
        RenderTargetBitmap bitmap = new(checked((int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX)),
            checked((int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        if (element is Window window)
        {
            DrawingVisual composited = new();
            using (DrawingContext drawing = composited.RenderOpen())
            {
                drawing.DrawRectangle(window.Background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                drawing.DrawImage(bitmap, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            }
            RenderTargetBitmap combined = new(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Pbgra32);
            combined.Render(composited);
            bitmap = combined;
        }
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record CheckResult(string Check, bool Passed, string Detail);
}
