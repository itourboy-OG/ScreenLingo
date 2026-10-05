using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace ScreenLingo;

public sealed record LanguageOption(string Code, string Name, string OcrCode, string DetectionCode);
public sealed record PixelRect(int X, int Y, int Width, int Height);
public enum CaptureKind { Window, Monitor }
public sealed record CaptureTarget(nint Handle, CaptureKind Kind, PixelRect Bounds, string Title);
public sealed record CapturedImage(byte[] Png, int Width, int Height);
public sealed record TextRegion(string Text, PixelRect Bounds, float Confidence);
public sealed record TranslatedRegion(TextRegion Source, string Translation);
public sealed record TranslationKey(string Provider, string Source, string Target, string Text);
public sealed record ScreenTranslation(string SourceLanguage, int RecognizedCount, ImmutableArray<TranslatedRegion> Regions);

[JsonConverter(typeof(JsonStringEnumConverter<TranslationProvider>))]
public enum TranslationProvider { MyMemory, Ollama }
[JsonConverter(typeof(JsonStringEnumConverter<CaptureScope>))]
public enum CaptureScope { ActiveWindow, WindowUnderPointer, MonitorUnderPointer }
[JsonConverter(typeof(JsonStringEnumConverter<TranslationMode>))]
public enum TranslationMode { Live, Screenshot }
[JsonConverter(typeof(JsonStringEnumConverter<ColorSkin>))]
public enum ColorSkin { Copper, Paper, Plum }

public sealed record AppSettings
{
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required TranslationProvider Provider { get; init; }
    public required CaptureScope Scope { get; init; }
    public required TranslationMode Mode { get; init; }
    public required int ScanIntervalMs { get; init; }
    public required int OverlayFontSize { get; init; }
    public required double OverlayOpacity { get; init; }
    public required bool ReduceMotion { get; init; }
    public required string OllamaModel { get; init; }
    public required ColorSkin ColorSkin { get; init; }
}

public static class TranslationRules
{
    public static ImmutableArray<LanguageOption> Languages() =>
    [
        new("en", "English", "eng", "eng"),
        new("es", "Español / Spanish", "spa", "spa"),
        new("zh-CN", "Chinese · Simplified", "chi_sim", "zho"),
        new("fr", "French", "fra", "fra"),
        new("de", "German", "deu", "deu"),
        new("it", "Italian", "ita", "ita"),
        new("ja", "Japanese", "jpn", "jpn"),
        new("ko", "Korean", "kor", "kor"),
        new("pt", "Portuguese", "por", "por"),
        new("ru", "Russian", "rus", "rus")
    ];

    public static AppSettings InitialSettings() => new()
    {
        SourceLanguage = "auto", TargetLanguage = "es", Provider = TranslationProvider.MyMemory,
        Scope = CaptureScope.ActiveWindow, Mode = TranslationMode.Live, ScanIntervalMs = 750, OverlayFontSize = 16,
        OverlayOpacity = 0.94, ReduceMotion = false, OllamaModel = "qwen3:4b", ColorSkin = ColorSkin.Copper
    };

    public static LanguageOption Language(string code) => Languages().FirstOrDefault(item => item.Code == code)
        ?? throw new ArgumentException($"Unsupported language '{code}'. Choose one of the listed languages.", nameof(code));

    public static AppSettings ValidateSettings(AppSettings settings)
    {
        if (settings.SourceLanguage != "auto") _ = Language(settings.SourceLanguage);
        _ = Language(settings.TargetLanguage);
        if (!Enum.IsDefined(settings.Provider) || !Enum.IsDefined(settings.Scope) || !Enum.IsDefined(settings.Mode))
            throw new ArgumentException("Settings contain an unsupported translation service, capture scope or translation mode.");
        if (!Enum.IsDefined(settings.ColorSkin))
            throw new ArgumentException("Settings contain an unsupported color skin. Choose Copper, Paper or Plum.");
        if (settings.ScanIntervalMs is < 250 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(settings), "Scan interval must be between 250 and 5000 milliseconds.");
        if (settings.OverlayFontSize is < 12 or > 32 || !double.IsFinite(settings.OverlayOpacity) || settings.OverlayOpacity is < 0.5 or > 1)
            throw new ArgumentOutOfRangeException(nameof(settings), "Overlay font size must be 12–32 and opacity 0.5–1.");
        if (string.IsNullOrWhiteSpace(settings.OllamaModel) || settings.OllamaModel.Any(char.IsControl))
            throw new ArgumentException("Choose a valid local Ollama model name in Settings.");
        return settings;
    }

    public static bool NeedsTranslation(string source, string target) => !string.Equals(source, target, StringComparison.OrdinalIgnoreCase);

    public static ImmutableArray<string> SplitForMyMemory(string text)
    {
        // MyMemory limits each request to 500 UTF-8 bytes; splitting preserves every Unicode character.
        ImmutableArray<string>.Builder chunks = ImmutableArray.CreateBuilder<string>();
        StringBuilder chunk = new();
        int bytes = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 480)
            {
                chunks.Add(chunk.ToString());
                chunk.Clear();
                bytes = 0;
            }
            chunk.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        if (chunk.Length > 0) chunks.Add(chunk.ToString());
        return chunks.ToImmutable();
    }

    public static ImmutableDictionary<TranslationKey, string> MergeCache(
        ImmutableDictionary<TranslationKey, string> cache, ImmutableArray<TranslationKey> keys,
        ImmutableArray<string> translations)
    {
        if (keys.Length != translations.Length) throw new ArgumentException("Translation result count does not match the request.");
        ImmutableDictionary<TranslationKey, string> updated = cache.SetItems(keys.Zip(translations,
            (key, value) => System.Collections.Generic.KeyValuePair.Create(key, value)));
        return updated;
    }
}
