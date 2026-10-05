using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenLingo;

public static partial class SmokeCheck
{
    /// <summary>Uses the real public GitHub feed and full release download. Native installation is checked separately.</summary>
    public static async Task<int> RunUpdateAsync(string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        try
        {
            SettingsConnector log = new(reportDirectory);
            using UpdateConnector updates = new(Path.Combine(reportDirectory, "Updates"), log.Warn);
            UpdateRelease release = await updates.CheckAsync(new Version(0, 1, 1), CancellationToken.None)
                ?? throw new InvalidOperationException("The public GitHub release must be newer than version 0.1.1.");
            Require(await updates.CheckAsync(Version.Parse(release.Version), CancellationToken.None) is null, "The current GitHub release must not offer itself as a newer update.");
            DownloadProgress? last = null;
            IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(value =>
            {
                last = value;
                File.WriteAllText(Path.Combine(reportDirectory, "download-progress.json"), JsonSerializer.Serialize(value));
            });
            await updates.DownloadArchiveAsync(release, Path.Combine(reportDirectory, "downloaded-release.zip"), progress, CancellationToken.None);
            await Task.Delay(100);
            Require(last is not null && last.Received == release.Size, "A real GitHub download must report completion and the exact published byte count.");
            File.WriteAllText(Path.Combine(reportDirectory, "update-report.json"), JsonSerializer.Serialize(new CheckResult("real GitHub update", true,
                "Public release comparison, full download, progress, published size and SHA-256 passed. Native installation is covered by --install-check.")));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(reportDirectory, "update-report.json"), JsonSerializer.Serialize(new CheckResult("real GitHub update", false, error.ToString())));
            return 1;
        }
    }

    /// <summary>Verifies extraction, deferred installation and the real Windows helper using a locally built release ZIP.</summary>
    public static async Task<int> RunInstallAsync(string reportDirectory, string archivePath)
    {
        Directory.CreateDirectory(reportDirectory);
        try
        {
            SettingsConnector log = new(reportDirectory);
            string directory = Path.Combine(reportDirectory, "Updates");
            Directory.CreateDirectory(directory);
            string archive = Path.Combine(directory, "package.zip");
            File.Copy(archivePath, archive, true);
            string stage = Path.Combine(directory, "package-" + Guid.NewGuid().ToString("N"));
            UpdateConnector.ExtractPackage(archive, stage, ApplicationIdentity.Version, CancellationToken.None);
            await using FileStream input = File.OpenRead(archive);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, CancellationToken.None));
            ReadyUpdate ready = new(ApplicationIdentity.Version, archive, stage, hash);
            File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(ready));
            using UpdateConnector updates = new(directory, log.Warn);
            Require(updates.LoadReady(new Version(0, 1, 1)) == ready, "A deferred update must remain available after loading preferences again.");
            await ExerciseInstallationAsync(ready, updates, reportDirectory);
            File.WriteAllText(Path.Combine(reportDirectory, "install-report.json"), JsonSerializer.Serialize(new CheckResult("native update installation", true,
                "ZIP extraction, hash rejection, unsafe path rejection, staged-file verification, deferred state, real file replacement and restart passed.")));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(reportDirectory, "install-report.json"), JsonSerializer.Serialize(new CheckResult("native update installation", false, error.ToString())));
            return 1;
        }
    }

    private static async Task ExerciseInstallationAsync(ReadyUpdate ready, UpdateConnector updates, string reportDirectory)
    {
        string traversal = Path.Combine(reportDirectory, "unsafe.zip");
        using (ZipArchive archive = ZipFile.Open(traversal, ZipArchiveMode.Create))
        {
            using StreamWriter writer = new(archive.CreateEntry("../outside.txt").Open());
            writer.Write("unsafe");
        }
        try
        {
            UpdateConnector.ExtractPackage(traversal, Path.Combine(reportDirectory, "unsafe-stage"), ready.Version, CancellationToken.None);
            throw new InvalidOperationException("The update extractor accepted a path outside its staging directory.");
        }
        catch (InvalidDataException) { Require(!File.Exists(Path.Combine(reportDirectory, "outside.txt")), "Unsafe ZIP contents must never escape the staging directory."); }
        try
        {
            using Process unexpected = await updates.StartInstallAsync(ready with { Sha256 = new string('0', 64) }, AppContext.BaseDirectory, Environment.ProcessId, CancellationToken.None);
            throw new InvalidOperationException("The update installer accepted a wrong archive digest.");
        }
        catch (InvalidDataException) { /* A mismatched digest must stop before launching any helper. */ }
        string stagedReadme = Path.Combine(ready.SourceDirectory, "README.md");
        string originalReadme = File.ReadAllText(stagedReadme);
        File.WriteAllText(stagedReadme, "changed after extraction");
        try
        {
            using Process unexpected = await updates.StartInstallAsync(ready, AppContext.BaseDirectory, Environment.ProcessId, CancellationToken.None);
            throw new InvalidOperationException("The installer accepted a changed staged file.");
        }
        catch (InvalidDataException) { /* Modified staging files must not execute. */ }
        finally { File.WriteAllText(stagedReadme, originalReadme); }

        string target = Path.GetFullPath(Path.Combine(reportDirectory, "installed-app"));
        string source = Path.GetFullPath(AppContext.BaseDirectory);
        if (target.StartsWith(source, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use a test report directory outside the running application folder.");
        foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidDataException("A package file has no directory."));
            File.Copy(path, destination, true);
        }
        File.WriteAllText(Path.Combine(target, "README.md"), "previous package marker");
        ProcessStartInfo start = new(Path.Combine(target, "ScreenLingo.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--appearance-check");
        start.ArgumentList.Add(Path.Combine(reportDirectory, "target-appearance"));
        using Process running = Process.Start(start) ?? throw new InvalidOperationException("Could not start the disposable ScreenLingo copy.");
        await Task.Delay(150);
        using Process installer = await updates.StartInstallAsync(ready, target, running.Id, CancellationToken.None);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        await installer.WaitForExitAsync(deadline.Token);
        Require(installer.ExitCode == 0, "The Windows update helper must install and restart successfully.");
        string json = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ready.ArchivePath) ?? throw new InvalidDataException("Update archive has no directory."), "install-result.json"));
        InstallResult result = JsonSerializer.Deserialize<InstallResult>(json) ?? throw new JsonException("Installer returned a null result.");
        Require(result.Status == "installed" && result.Version == ready.Version, "The helper must report the installed version.");
        using Process restarted = Process.GetProcessById(result.ProcessId);
        while (restarted.MainWindowHandle == 0 && !restarted.HasExited)
        {
            await Task.Delay(100, deadline.Token);
            restarted.Refresh();
        }
        Require(File.ReadAllText(Path.Combine(target, "README.md")) == originalReadme, "Installing must replace the old files with the verified package.");
        Require(restarted.MainModule?.FileName == Path.Combine(target, "ScreenLingo.exe"), "The helper must restart the updated application in the same folder.");
        Require(restarted.CloseMainWindow(), "The disposable restarted app must accept a normal close request.");
        await restarted.WaitForExitAsync(deadline.Token);
    }

    private sealed record InstallResult([property: JsonRequired] string Status, [property: JsonRequired] string Version,
        [property: JsonRequired] int ProcessId, [property: JsonRequired] string BackupDirectory);
}
