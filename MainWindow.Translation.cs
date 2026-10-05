using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLingo;

public partial class MainWindow
{
    private async Task<ScreenTranslation> TranslateImageAsync(CapturedImage image, AppSettings snapshot,
        CancellationToken token, Action<string> progress)
    {
        (string source, ImmutableArray<TextRegion> regions) = await RecognizeImageAsync(image, snapshot, token, progress);
        return await TranslateRegionsAsync(source, regions, snapshot, token, progress);
    }

    private async Task<(string Source, ImmutableArray<TextRegion> Regions)> RecognizeImageAsync(CapturedImage image,
        AppSettings snapshot, CancellationToken token, Action<string> progress)
    {
        progress("Recognizing text…");
        ImmutableArray<TextRegion> regions = await Task.Run(() => ocr.Recognize(image, snapshot.SourceLanguage), token);
        token.ThrowIfCancellationRequested();
        if (regions.IsEmpty) return (snapshot.SourceLanguage, []);
        string source = snapshot.SourceLanguage == "auto" ? await Task.Run(() => ocr.DetectLanguage(regions), token) : snapshot.SourceLanguage;
        if (snapshot.SourceLanguage == "auto")
            regions = await Task.Run(() => ocr.Recognize(image, source), token);
        token.ThrowIfCancellationRequested();
        return (source, regions);
    }

    private async Task<ScreenTranslation> TranslateRegionsAsync(string source, ImmutableArray<TextRegion> regions,
        AppSettings snapshot, CancellationToken token, Action<string> progress)
    {
        if (regions.IsEmpty) return new ScreenTranslation(source, 0, []);
        if (!TranslationRules.NeedsTranslation(source, snapshot.TargetLanguage))
            return new ScreenTranslation(source, regions.Length, []);
        string provider = snapshot.Provider == TranslationProvider.Ollama ? "Ollama:" + snapshot.OllamaModel : "MyMemory";
        ImmutableArray<TranslationKey> keys = regions.Select(region => new TranslationKey(provider, source, snapshot.TargetLanguage, region.Text)).ToImmutableArray();
        ImmutableArray<TranslationKey> missing = keys.Distinct().Where(key => !cache.ContainsKey(key)).ToImmutableArray();
        if (!missing.IsEmpty)
        {
            progress($"{regions.Length} labels found · {TranslationRules.Language(source).Name} → {TranslationRules.Language(snapshot.TargetLanguage).Name}");
            ITranslator translator = snapshot.Provider == TranslationProvider.MyMemory
                ? new MyMemoryConnector(onlineHttp) : new OllamaConnector(offlineHttp, snapshot.OllamaModel);
            ImmutableArray<string> translated = await translator.TranslateAsync(missing.Select(key => key.Text).ToImmutableArray(), source, snapshot.TargetLanguage, token);
            token.ThrowIfCancellationRequested();
            cache = TranslationRules.MergeCache(cache, missing, translated);
        }
        ImmutableArray<TranslatedRegion> labels = regions.Select((region, index) => new TranslatedRegion(region, cache[keys[index]]))
            .Where(region => !string.Equals(region.Source.Text, region.Translation, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
        // ponytail: keep 512 session labels; add LRU only if real game sessions exhaust this cache.
        cache = cache.Take(512).ToImmutableDictionary();
        return new ScreenTranslation(source, regions.Length, labels);
    }
}
