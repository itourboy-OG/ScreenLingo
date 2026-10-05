using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLingo;

public sealed record UpdateRelease(string Version, Uri DownloadUrl, long Size, string Sha256);
public sealed record DownloadProgress(long Received, long Total);
public sealed record ReadyUpdate([property: JsonRequired] string Version, [property: JsonRequired] string ArchivePath,
    [property: JsonRequired] string SourceDirectory, [property: JsonRequired] string Sha256);
public sealed record InstallRequest(ReadyUpdate Package, string TargetDirectory, int ProcessId, string UpdateDirectory);

public static class UpdateRules
{
    public const string Repository = "itourboy-OG/ScreenLingo";
    public const string RepositoryUrl = "https://github.com/" + Repository;

    /// <summary>Requires a stable release, the exact Windows asset and GitHub's SHA-256 digest; unrelated JSON fields are ignored.</summary>
    public static UpdateRelease? ParseRelease(string json, Version installed)
    {
        ReleaseResponse release = JsonSerializer.Deserialize<ReleaseResponse>(json)
            ?? throw new JsonException("GitHub returned a null release response.");
        if (release.Draft || release.Prerelease || string.IsNullOrEmpty(release.Tag) || !release.Tag.StartsWith('v')
            || !Version.TryParse(release.Tag[1..], out Version? version) || version.Build < 0 || release.Tag[1..] != version.ToString(3))
            throw new JsonException("GitHub's latest release must be published and use a stable vMAJOR.MINOR.PATCH tag.");
        if (version <= installed) return null;
        if (release.Assets.IsDefault) throw new JsonException("GitHub's release contains no asset list.");
        string assetName = $"ScreenLingo-{version.ToString(3)}-win-x64.zip";
        ImmutableArray<AssetResponse> matches = release.Assets.Where(item => item.Name == assetName).ToImmutableArray();
        if (matches.Length != 1) throw new JsonException($"GitHub release {release.Tag} must contain exactly one {assetName}; received={matches.Length}.");
        AssetResponse asset = matches[0];
        string expectedUrl = RepositoryUrl + "/releases/download/" + release.Tag + "/" + assetName;
        if (asset.State != "uploaded" || asset.Size is <= 0 or > 1073741824 || asset.Url != expectedUrl)
            throw new JsonException($"GitHub returned an invalid Windows asset: tag={release.Tag}; name={asset.Name}; size={asset.Size}; URL={asset.Url}.");
        if (asset.Digest is null || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal) || asset.Digest.Length != 71 || !asset.Digest[7..].All(Uri.IsHexDigit))
            throw new JsonException($"GitHub asset {assetName} has no valid SHA-256 digest. Re-upload the release package.");
        return new(version.ToString(3), new Uri(asset.Url), asset.Size, asset.Digest[7..]);
    }

    private sealed record ReleaseResponse(
        [property: JsonRequired, JsonPropertyName("tag_name")] string Tag,
        [property: JsonRequired, JsonPropertyName("draft")] bool Draft,
        [property: JsonRequired, JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonRequired, JsonPropertyName("assets")] ImmutableArray<AssetResponse> Assets);
    private sealed record AssetResponse(
        [property: JsonRequired, JsonPropertyName("name")] string Name,
        [property: JsonRequired, JsonPropertyName("state")] string State,
        [property: JsonRequired, JsonPropertyName("browser_download_url")] string Url,
        [property: JsonRequired, JsonPropertyName("size")] long Size,
        [property: JsonRequired, JsonPropertyName("digest")] string? Digest);
}

/// <summary>Connects GitHub Releases to verified portable updates. Downloads are staged until the user chooses to install.</summary>
public sealed class UpdateConnector : IDisposable
{
    private readonly HttpConnector metadata;
    private readonly HttpClient downloads = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly string directory;
    private readonly Action<string, string, int> warning;

    public UpdateConnector(string directory, Action<string, string, int> warning)
    {
        this.directory = Path.GetFullPath(directory);
        this.warning = warning;
        metadata = new HttpConnector(TimeSpan.FromSeconds(20), warning);
        downloads.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenLingo/" + ApplicationIdentity.Version);
    }

    public async Task<UpdateRelease?> CheckAsync(Version installed, CancellationToken cancellation)
    {
        string json = await metadata.GetAsync(new Uri("https://api.github.com/repos/" + UpdateRules.Repository + "/releases/latest"),
            "repository=" + UpdateRules.Repository + "; installed=" + installed, cancellation);
        return UpdateRules.ParseRelease(json, installed);
    }

    public ReadyUpdate? LoadReady(Version installed)
    {
        string path = Path.Combine(directory, "ready.json");
        if (!File.Exists(path)) return null;
        ReadyUpdate ready = JsonSerializer.Deserialize<ReadyUpdate>(File.ReadAllText(path))
            ?? throw new JsonException("The saved update record is null: " + path);
        if (!Version.TryParse(ready.Version, out Version? version)) throw new JsonException("The saved update version is invalid: " + path);
        if (version <= installed) return null;
        ValidateReady(ready);
        return ready;
    }

    public async Task<ReadyUpdate> DownloadAsync(UpdateRelease release, IProgress<DownloadProgress> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(directory);
        string archivePath = Path.Combine(directory, "ScreenLingo-" + release.Version + ".zip");
        string partialPath = archivePath + ".download";
        await DownloadArchiveAsync(release, partialPath, progress, cancellation);
        File.Move(partialPath, archivePath, true);
        string stage = Path.Combine(directory, "staged-" + release.Version + "-" + Guid.NewGuid().ToString("N"));
        await Task.Run(() => ExtractPackage(archivePath, stage, release.Version, cancellation), cancellation);
        ReadyUpdate ready = new(release.Version, archivePath, stage, release.Sha256);
        string manifest = Path.Combine(directory, "ready.json");
        File.WriteAllText(manifest + ".tmp", JsonSerializer.Serialize(ready));
        File.Move(manifest + ".tmp", manifest, true);
        return ready;
    }

    private async Task DownloadFileAsync(UpdateRelease release, string path, IProgress<DownloadProgress> progress, CancellationToken cancellation)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromMinutes(15));
                using HttpResponseMessage response = await downloads.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellation);
                    throw new HttpRequestException($"Update download failed: URL={release.DownloadUrl}; HTTP={(int)response.StatusCode}; response={body}", null, response.StatusCode);
                }
                await using Stream input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using FileStream output = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                byte[] buffer = new byte[81920];
                long received = 0;
                Stopwatch reportClock = Stopwatch.StartNew();
                progress.Report(new(0, release.Size));
                int count;
                while ((count = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > release.Size) throw new InvalidDataException("The update exceeds its published size: " + release.DownloadUrl);
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    if (reportClock.ElapsedMilliseconds >= 100 || received == release.Size)
                    {
                        progress.Report(new(received, release.Size));
                        reportClock.Restart();
                    }
                }
                if (received != release.Size) throw new IOException($"Update download was incomplete: URL={release.DownloadUrl}; expected={release.Size}; received={received}.");
                return;
            }
            catch (Exception error) when (error is HttpRequestException request && (request.StatusCode is null || (int)request.StatusCode >= 500 || (int)request.StatusCode == 429)
                || error is IOException && error is not InvalidDataException || error is TaskCanceledException && !cancellation.IsCancellationRequested)
            {
                if (attempt == 3)
                {
                    if (error is TaskCanceledException) throw new TimeoutException("Update download timed out after 15 minutes: " + release.DownloadUrl, error);
                    throw;
                }
                warning("update_download_retry", release.DownloadUrl.Host, attempt);
                await Task.Delay(attempt * 1000, cancellation);
            }
        }
        throw new InvalidOperationException("Update download ended without a result.");
    }

    internal async Task DownloadArchiveAsync(UpdateRelease release, string path, IProgress<DownloadProgress> progress, CancellationToken cancellation)
    {
        await DownloadFileAsync(release, path, progress, cancellation);
        await VerifyHashAsync(path, release.Sha256, cancellation);
    }

    internal static void ExtractPackage(string archivePath, string stage, string version, CancellationToken cancellation)
    {
        Directory.CreateDirectory(stage);
        string prefix = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 4096 || archive.Entries.Sum(entry => entry.Length) > 2147483648)
            throw new InvalidDataException("The update package exceeds supported extraction limits.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(stage, entry.FullName));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The update contains an unsafe archive path: " + entry.FullName);
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidDataException("The update entry has no parent: " + entry.FullName));
            entry.ExtractToFile(path, false);
        }
        foreach (string file in new[] { "ScreenLingo.exe", "ScreenLingo.dll", "ScreenLingo.deps.json", "ScreenLingo.runtimeconfig.json", "Install-Update.ps1" })
            if (!File.Exists(Path.Combine(stage, file))) throw new InvalidDataException("The update is missing " + file);
        if (FileVersionInfo.GetVersionInfo(Path.Combine(stage, "ScreenLingo.dll")).ProductVersion != version)
            throw new InvalidDataException("The update DLL version does not match the published release " + version);
    }

    public async Task<Process> StartInstallAsync(ReadyUpdate ready, string targetDirectory, int processId, CancellationToken cancellation)
    {
        ValidateReady(ready);
        await VerifyHashAsync(ready.ArchivePath, ready.Sha256, cancellation);
        await VerifyStagedPackageAsync(ready, cancellation);
        string requestPath = Path.Combine(directory, "install.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new InstallRequest(ready, Path.GetFullPath(targetDirectory), processId, directory)));
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo start = new(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(ready.SourceDirectory, "Install-Update.ps1"), "-ManifestPath", requestPath })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the ScreenLingo update installer.");
    }

    private void ValidateReady(ReadyUpdate ready)
    {
        if (string.IsNullOrWhiteSpace(ready.SourceDirectory) || string.IsNullOrWhiteSpace(ready.ArchivePath) || string.IsNullOrWhiteSpace(ready.Sha256))
            throw new JsonException("The saved update record is missing a source folder, archive path or SHA-256 digest.");
        string prefix = directory + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(ready.SourceDirectory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFullPath(ready.ArchivePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(ready.ArchivePath) || !File.Exists(Path.Combine(ready.SourceDirectory, "Install-Update.ps1"))
            || ready.Sha256.Length != 64 || !ready.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The saved update is incomplete or outside the update folder. Download it again: " + directory);
    }

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellation)
    {
        await using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Update verification failed: file={path}; expected SHA-256={expected}; received={actual}. Download the release again.");
    }

    private static async Task VerifyStagedPackageAsync(ReadyUpdate ready, CancellationToken cancellation)
    {
        using ZipArchive archive = ZipFile.OpenRead(ready.ArchivePath);
        string prefix = Path.GetFullPath(ready.SourceDirectory) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries.Where(item => item.Name.Length > 0))
        {
            string path = Path.GetFullPath(Path.Combine(ready.SourceDirectory, entry.FullName));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The staged update contains an unsafe file: " + entry.FullName);
            await using Stream original = entry.Open();
            await using FileStream staged = File.OpenRead(path);
            byte[] expected = await SHA256.HashDataAsync(original, cancellation).ConfigureAwait(false);
            byte[] actual = await SHA256.HashDataAsync(staged, cancellation).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new InvalidDataException("A staged update file changed after extraction: " + entry.FullName + ". Download the update again.");
        }
    }

    public void Dispose() { metadata.Dispose(); downloads.Dispose(); }
}
