using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenLingo;

public static partial class SmokeCheck
{
    /// <summary>Saves two captures of one requested window for local recognition and motion diagnosis without translation requests.</summary>
    public static async Task<int> RunCaptureAsync(int processId, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        using Process game = Process.GetProcessById(processId);
        Require(game.MainWindowHandle != 0, $"Process {processId} must have a visible main window.");
        using CaptureConnector capture = CaptureConnector.ForWindow(WindowsConnector.TestWindowTarget(game.MainWindowHandle));
        CapturedImage first = await capture.SnapshotAsync(System.Threading.CancellationToken.None);
        File.WriteAllBytes(Path.Combine(reportDirectory, "before.png"), first.Png);
        await Task.Delay(500);
        CapturedImage second = await capture.SnapshotAsync(System.Threading.CancellationToken.None);
        File.WriteAllBytes(Path.Combine(reportDirectory, "after.png"), second.Png);
        return await RunRecognitionAsync(Path.Combine(reportDirectory, "before.png"), "zh-CN", reportDirectory);
    }

    /// <summary>Exercises screenshot capture and translation against an explicitly supplied process, independent of focus changes.</summary>
    public static async Task<int> RunGameAsync(int processId, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        SettingsConnector store = new(Path.Combine(reportDirectory, "preferences"));
        AppSettings settings = TranslationRules.InitialSettings() with { Mode = TranslationMode.Screenshot, TargetLanguage = "en" };
        MainWindow control = new(store, settings);
        try
        {
            using Process game = Process.GetProcessById(processId);
            Require(game.MainWindowHandle != 0, $"Process {processId} must have a visible main window.");
            CaptureTarget target = WindowsConnector.TestWindowTarget(game.MainWindowHandle);
            control.Show();
            await control.CaptureScreenshotAsync(target);
            ScreenshotWindow viewer = Application.Current.Windows.OfType<ScreenshotWindow>().Single();
            File.WriteAllBytes(Path.Combine(reportDirectory, "captured-game.png"), viewer.CapturedImage.Png);
            Require(viewer.Translation is not null, "The actual screenshot capture and translation must complete.");
            File.WriteAllText(Path.Combine(reportDirectory, "game-report.json"), JsonSerializer.Serialize(viewer.Translation,
                new JsonSerializerOptions { WriteIndented = true }));
            SaveVisual(viewer, Path.Combine(reportDirectory, "game-viewer.png"));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(reportDirectory, "game-error.txt"), error.ToString());
            return 1;
        }
        finally { control.Close(); }
    }

    /// <summary>Measures cold and warm recognition of an explicitly supplied image. Text and images remain local.</summary>
    public static async Task<int> RunRecognitionAsync(string imagePath, string sourceLanguage, string reportDirectory)
    {
        _ = TranslationRules.Language(sourceLanguage);
        Directory.CreateDirectory(reportDirectory);
        BitmapFrame frame = BitmapFrame.Create(new Uri(imagePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        CapturedImage image = new(File.ReadAllBytes(imagePath), frame.PixelWidth, frame.PixelHeight);
        using OcrConnector ocr = new(Path.Combine(AppContext.BaseDirectory, "Models"));
        ImmutableArray<RecognitionResult>.Builder results = ImmutableArray.CreateBuilder<RecognitionResult>();
        foreach (string source in new[] { "auto", sourceLanguage, "auto", sourceLanguage })
        {
            Stopwatch timer = Stopwatch.StartNew();
            ImmutableArray<TextRegion> regions = await Task.Run(() => ocr.Recognize(image, source));
            string detected = regions.IsEmpty ? "no text" : ocr.DetectLanguage(regions);
            results.Add(new RecognitionResult(source, detected, timer.Elapsed.TotalSeconds, regions));
        }
        File.WriteAllText(Path.Combine(reportDirectory, "recognition-report.json"), JsonSerializer.Serialize(results.ToImmutable(),
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private sealed record RecognitionResult(string Source, string Detected, double Seconds, ImmutableArray<TextRegion> Regions);
}
