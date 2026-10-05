using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
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
public sealed record ReadyUpdate([property: JsonRequired] string Version, [property: JsonRequired] string InstallerPath,
    [property: JsonRequired] string Sha256);

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
        string assetName = $"ScreenLingo-{version.ToString(3)}-Setup-x64.exe";
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

/// <summary>Connects GitHub Releases to verified Windows installers. Downloads wait until the user chooses to install.</summary>
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
        string path = Path.Combine(directory, "installer-ready.json");
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
        string installerPath = Path.Combine(directory, "ScreenLingo-" + release.Version + "-Setup-x64.exe");
        string partialPath = installerPath + ".download";
        await DownloadInstallerAsync(release, partialPath, progress, cancellation);
        File.Move(partialPath, installerPath, true);
        ReadyUpdate ready = new(release.Version, installerPath, release.Sha256);
        string manifest = Path.Combine(directory, "installer-ready.json");
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

    internal async Task DownloadInstallerAsync(UpdateRelease release, string path, IProgress<DownloadProgress> progress, CancellationToken cancellation)
    {
        await DownloadFileAsync(release, path, progress, cancellation);
        await VerifyHashAsync(path, release.Sha256, cancellation);
    }

    public async Task<Process> StartInstallAsync(ReadyUpdate ready, string targetDirectory, CancellationToken cancellation)
    {
        ValidateReady(ready);
        await VerifyHashAsync(ready.InstallerPath, ready.Sha256, cancellation);
        string target = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(target, "ScreenLingo.exe")) || target.Equals(Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The update destination must be the existing ScreenLingo application folder: " + targetDirectory);
        ProcessStartInfo start = new(ready.InstallerPath) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
        start.ArgumentList.Add("/SP-");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add("/DIR=" + target);
        return Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the verified ScreenLingo installer.");
    }

    private void ValidateReady(ReadyUpdate ready)
    {
        if (!Version.TryParse(ready.Version, out Version? version) || version.Build < 0 || ready.Version != version.ToString(3)
            || string.IsNullOrWhiteSpace(ready.InstallerPath) || string.IsNullOrWhiteSpace(ready.Sha256))
            throw new JsonException("The saved installer record is missing a stable version, installer path or SHA-256 digest.");
        string path = Path.GetFullPath(ready.InstallerPath);
        string expected = Path.Combine(directory, "ScreenLingo-" + ready.Version + "-Setup-x64.exe");
        if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)
            || ready.Sha256.Length != 64 || !ready.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The saved installer is incomplete or outside the update folder. Download it again: " + directory);
        for (string? current = path; current is not null && current.StartsWith(directory, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installer path contains a filesystem link: " + current);
    }

    private static async Task VerifyHashAsync(string path, string expected, CancellationToken cancellation)
    {
        await using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Update verification failed: file={path}; expected SHA-256={expected}; received={actual}. Download the release again.");
    }

    public void Dispose() { metadata.Dispose(); downloads.Dispose(); }
}
