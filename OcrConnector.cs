using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Lingua;
using TesseractOCR;
using TesseractOCR.Enums;

namespace ScreenLingo;

/// <summary>Connects to the bundled Tesseract recognizer and local language profiles. Use on one worker at a time.</summary>
public sealed class OcrConnector : IDisposable
{
    private readonly string modelsPath;
    private readonly LanguageDetector identifier;
    private Engine? engine;
    private string loadedLanguages = "";

    public OcrConnector(string modelsPath)
    {
        this.modelsPath = modelsPath;
        identifier = LanguageDetectorBuilder.FromLanguages(Lingua.Language.English, Lingua.Language.Spanish,
            Lingua.Language.Chinese, Lingua.Language.Japanese, Lingua.Language.Korean, Lingua.Language.French,
            Lingua.Language.German, Lingua.Language.Portuguese, Lingua.Language.Russian, Lingua.Language.Italian).Build();
    }

    public ImmutableArray<TextRegion> Recognize(CapturedImage image, string sourceLanguage)
    {
        string languages = sourceLanguage == "auto"
            ? string.Join('+', TranslationRules.Languages().Select(language => language.OcrCode))
            : TranslationRules.Language(sourceLanguage).OcrCode;
        if (loadedLanguages != languages)
        {
            engine?.Dispose();
            engine = new Engine(modelsPath, languages, EngineMode.LstmOnly, [], null, false, null);
            loadedLanguages = languages;
        }
        using TesseractOCR.Pix.Image pixels = TesseractOCR.Pix.Image.LoadFromMemory(image.Png);
        using Page page = (engine ?? throw new InvalidOperationException("Text recognizer was not initialized."))
            .Process(pixels, PageSegMode.SparseText);
        ImmutableArray<TextRegion>.Builder lines = ImmutableArray.CreateBuilder<TextRegion>();
        foreach (TesseractOCR.Layout.Block block in page.Layout)
        foreach (TesseractOCR.Layout.Paragraph paragraph in block.Paragraphs)
        foreach (TesseractOCR.Layout.TextLine line in paragraph.TextLines)
        {
            string text = line.Text.Trim();
            if (line.BoundingBox is not TesseractOCR.Rect bounds || !text.Any(char.IsLetter)) continue;
            // ponytail: omit low-confidence fragments at 45%; stylized game fonts need capture-specific tuning later.
            if (line.Confidence < 45) continue;
            lines.Add(new TextRegion(text, new PixelRect(bounds.X1, bounds.Y1, bounds.Width, bounds.Height), line.Confidence));
        }
        return lines.ToImmutable();
    }

    public string DetectLanguage(ImmutableArray<TextRegion> regions)
    {
        string text = string.Join(' ', regions.Select(region => region.Text));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("There is no readable text to detect a language from.");
        Lingua.Language language = identifier.DetectLanguageOf(text);
        return language switch
        {
            Lingua.Language.English => "en", Lingua.Language.Spanish => "es", Lingua.Language.Chinese => "zh-CN",
            Lingua.Language.Japanese => "ja", Lingua.Language.Korean => "ko", Lingua.Language.French => "fr",
            Lingua.Language.German => "de", Lingua.Language.Portuguese => "pt", Lingua.Language.Russian => "ru",
            Lingua.Language.Italian => "it",
            _ => throw new InvalidOperationException("Automatic language detection is uncertain. Select the source language manually.")
        };
    }

    public void Dispose() => engine?.Dispose();
}
