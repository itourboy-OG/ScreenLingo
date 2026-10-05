using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lingua;
using TesseractOCR;
using TesseractOCR.Enums;

namespace ScreenLingo;

/// <summary>Connects to the bundled Tesseract recognizer and local language profiles. Use on one worker at a time.</summary>
public sealed class OcrConnector : IDisposable
{
    private readonly string modelsPath;
    private readonly LanguageDetector identifier;
    private readonly TextDetectionConnector textDetector;
    private Engine? engine;
    private Engine? automaticEngine;
    private string loadedLanguages = "";

    public OcrConnector(string modelsPath)
    {
        this.modelsPath = modelsPath;
        textDetector = new TextDetectionConnector(Path.Combine(modelsPath, "text-detector.onnx"));
        identifier = LanguageDetectorBuilder.FromLanguages(Lingua.Language.English, Lingua.Language.Spanish,
            Lingua.Language.Chinese, Lingua.Language.Japanese, Lingua.Language.Korean, Lingua.Language.French,
            Lingua.Language.German, Lingua.Language.Portuguese, Lingua.Language.Russian, Lingua.Language.Italian).Build();
    }

    public ImmutableArray<TextRegion> Recognize(CapturedImage image, string sourceLanguage)
    {
        Engine recognizer;
        if (sourceLanguage == "auto")
        {
            // One model per script identifies the language; the selected language refines the text afterward.
            automaticEngine ??= new Engine(modelsPath, "eng+chi_sim+jpn+kor+rus", EngineMode.LstmOnly, [], null, false, null);
            recognizer = automaticEngine;
        }
        else
        {
            string languages = TranslationRules.Language(sourceLanguage).OcrCode;
            if (loadedLanguages != languages)
            {
                engine?.Dispose();
                engine = new Engine(modelsPath, languages, EngineMode.LstmOnly, [], null, false, null);
                loadedLanguages = languages;
            }
            recognizer = engine ?? throw new InvalidOperationException("Text recognizer was not initialized.");
        }
        byte[] prepared = PrepareImage(image);
        using TesseractOCR.Pix.Image original = TesseractOCR.Pix.Image.LoadFromMemory(prepared);
        using TesseractOCR.Pix.Image pixels = original.Scale(1.5f, 1.5f);
        ImmutableArray<TextRegion> scattered;
        using (Page page = recognizer.Process(pixels, PageSegMode.SparseText)) scattered = ReadRegions(page);
        if (sourceLanguage == "auto") return textDetector.Filter(image, scattered);
        // Suppress button backgrounds and read their isolated characters as rows, then merge missing regions.
        using TesseractOCR.Pix.Image buttons = TesseractOCR.Pix.Image.LoadFromMemory(PrepareButtonImage(prepared));
        using TesseractOCR.Pix.Image enlargedButtons = buttons.Scale(1.5f, 1.5f);
        using Page rows = recognizer.Process(enlargedButtons, PageSegMode.SingleBlock);
        return textDetector.Filter(image, scattered.AddRange(ReadRegions(rows).Where(region => region.Confidence >= 80 &&
            !scattered.Any(existing => Overlaps(existing.Bounds, region.Bounds)))));
    }

    private static byte[] PrepareImage(CapturedImage image)
    {
        using MemoryStream input = new(image.Png);
        BitmapFrame frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap gray = new(frame, PixelFormats.Gray8, null, 0);
        byte[] data = new byte[checked(gray.PixelWidth * gray.PixelHeight)];
        gray.CopyPixels(data, gray.PixelWidth, 0);
        // Normalize predominantly dark captures to dark lettering on a light background before segmentation.
        long brightness = data.Sum(value => (long)value);
        bool darkBackground = brightness < data.Length * 128L;
        for (int index = 0; index < data.Length; index++)
        {
            int contrast = Math.Clamp((data[index] - 128) * 2 + 128, 0, 255);
            data[index] = (byte)(darkBackground ? 255 - contrast : contrast);
        }
        return EncodeImage(gray.PixelWidth, gray.PixelHeight, data);
    }

    private static byte[] PrepareButtonImage(byte[] png)
    {
        using MemoryStream input = new(png);
        BitmapFrame frame = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        FormatConvertedBitmap gray = new(frame, PixelFormats.Gray8, null, 0);
        byte[] data = new byte[checked(gray.PixelWidth * gray.PixelHeight)];
        gray.CopyPixels(data, gray.PixelWidth, 0);
        for (int index = 0; index < data.Length; index++) data[index] = data[index] < 160 ? (byte)0 : (byte)255;
        return EncodeImage(gray.PixelWidth, gray.PixelHeight, data);
    }

    private static byte[] EncodeImage(int width, int height, byte[] data)
    {
        BitmapSource prepared = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, data, width);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(prepared));
        using MemoryStream output = new();
        encoder.Save(output);
        return output.ToArray();
    }

    private static ImmutableArray<TextRegion> ReadRegions(Page page)
    {
        const float scale = 1.5f;
        ImmutableArray<TextRegion>.Builder lines = ImmutableArray.CreateBuilder<TextRegion>();
        foreach (TesseractOCR.Layout.Block block in page.Layout)
        foreach (TesseractOCR.Layout.Paragraph paragraph in block.Paragraphs)
        foreach (TesseractOCR.Layout.TextLine line in paragraph.TextLines)
        {
            ImmutableArray<TextRegion>.Builder words = ImmutableArray.CreateBuilder<TextRegion>();
            foreach (TesseractOCR.Layout.Word word in line.Words)
            {
                string value = word.Text.Trim();
                if (word.Confidence < 30 || !value.Any(char.IsLetterOrDigit) || word.BoundingBox is not TesseractOCR.Rect bounds) continue;
                TextRegion region = new(value, new PixelRect((int)(bounds.X1 / scale), (int)(bounds.Y1 / scale),
                    (int)Math.Ceiling(bounds.Width / scale), (int)Math.Ceiling(bounds.Height / scale)), word.Confidence);
                if (words.Count > 0)
                {
                    TextRegion previous = words[^1];
                    int gap = region.Bounds.X - previous.Bounds.X - previous.Bounds.Width;
                    if (gap > Math.Max(20, Math.Max(previous.Bounds.Height, region.Bounds.Height) * 2))
                    {
                        lines.Add(CreateRegion(words.ToImmutable()));
                        words.Clear();
                    }
                }
                words.Add(region);
            }
            if (words.Count > 0) lines.Add(CreateRegion(words.ToImmutable()));
        }
        return lines.Where(region => region.Text.Any(char.IsLetter)).ToImmutableArray();
    }

    private static TextRegion CreateRegion(ImmutableArray<TextRegion> words)
    {
        string text = Regex.Replace(string.Join(' ', words.Select(word => word.Text)),
            @"(?<=[\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af])\s+(?=[\u3400-\u9fff\u3040-\u30ff\uac00-\ud7af])", "");
        int left = words.Min(word => word.Bounds.X);
        int top = words.Min(word => word.Bounds.Y);
        int right = words.Max(word => word.Bounds.X + word.Bounds.Width);
        int bottom = words.Max(word => word.Bounds.Y + word.Bounds.Height);
        return new TextRegion(text, new PixelRect(left, top, right - left, bottom - top), words.Average(word => word.Confidence));
    }

    private static bool Overlaps(PixelRect first, PixelRect second)
    {
        int width = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        int height = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return width * (long)height > Math.Min(first.Width * (long)first.Height, second.Width * (long)second.Height) / 2;
    }

    public string DetectLanguage(ImmutableArray<TextRegion> regions)
    {
        string text = string.Join(' ', regions.Select(region => region.Text));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("There is no readable text to detect a language from.");
        int han = text.Count(character => character is >= '\u3400' and <= '\u9fff');
        int kana = text.Count(character => character is >= '\u3040' and <= '\u30ff');
        int hangul = text.Count(character => character is >= '\uac00' and <= '\ud7af');
        if (hangul >= 2 && hangul > han) return "ko";
        if (han >= 2) return kana >= Math.Max(2, han / 5) ? "ja" : "zh-CN";
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

    public void Dispose() { engine?.Dispose(); automaticEngine?.Dispose(); textDetector.Dispose(); }
}
