using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ScreenLingo;

public static partial class SmokeCheck
{
    /// <summary>Uses the real public GitHub feed and full installer download, including its published SHA-256 digest.</summary>
    public static async Task<int> RunUpdateAsync(string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        try
        {
            SettingsConnector log = new(reportDirectory);
            using UpdateConnector updates = new(Path.Combine(reportDirectory, "Updates"), log.Warn);
            UpdateRelease release = await updates.CheckAsync(new Version(0, 2, 0), CancellationToken.None)
                ?? throw new InvalidOperationException("The public GitHub release must be newer than version 0.2.0.");
            Require(await updates.CheckAsync(Version.Parse(release.Version), CancellationToken.None) is null, "The installed version must not offer itself as a newer update.");
            DownloadProgress? last = null;
            IProgress<DownloadProgress> progress = new Progress<DownloadProgress>(value =>
            {
                last = value;
                File.WriteAllText(Path.Combine(reportDirectory, "download-progress.json"), JsonSerializer.Serialize(value));
            });
            ReadyUpdate ready = await updates.DownloadAsync(release, progress, CancellationToken.None);
            await Task.Delay(100);
            Require(last is not null && last.Received == release.Size, "The GitHub installer download must report its exact published byte count.");
            Require(updates.LoadReady(new Version(0, 2, 0)) == ready, "Install later must retain the verified installer across relaunches.");
            Require(updates.LoadReady(Version.Parse(release.Version)) is null, "An installed update must no longer be offered as a deferred installation.");
            await RejectChangedInstallerAsync(updates, ready);
            File.WriteAllText(Path.Combine(reportDirectory, "update-report.json"), JsonSerializer.Serialize(new CheckResult("real GitHub installer update", true,
                "Public release comparison, EXE selection, full download, progress, size, SHA-256, deferred state and modified-installer rejection passed.")));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(reportDirectory, "update-report.json"), JsonSerializer.Serialize(new CheckResult("real GitHub installer update", false, error.ToString())));
            return 1;
        }
    }

    /// <summary>Installs the real EXE to an isolated folder, upgrades a running 0.2.0 copy, and uninstalls the result.</summary>
    public static async Task<int> RunInstallAsync(string reportDirectory, string installerPath, string previousPackageDirectory)
    {
        const string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenLingo_is1";
        string target = Path.GetFullPath(Path.Combine(reportDirectory, "installed-app"));
        string source = Path.GetFullPath(AppContext.BaseDirectory);
        Directory.CreateDirectory(reportDirectory);
        try
        {
            Require(!target.StartsWith(source, StringComparison.OrdinalIgnoreCase), "The installer check must be outside the running application folder.");
            Require(!Directory.Exists(target), "The isolated installation folder must be new: " + target);
            using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(registryPath))
                Require(existing is null, "An existing registered ScreenLingo installation must not be changed by this check.");
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenLingo", "settings.json");
            string? originalPreferences = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
            string stage = Path.Combine(reportDirectory, "Updates");
            Directory.CreateDirectory(stage);
            string setup = Path.Combine(stage, "ScreenLingo-" + ApplicationIdentity.Version + "-Setup-x64.exe");
            File.Copy(installerPath, setup, false);
            await using (FileStream input = File.OpenRead(setup))
            {
                string digest = Convert.ToHexString(await SHA256.HashDataAsync(input, CancellationToken.None));
                ReadyUpdate ready = new(ApplicationIdentity.Version, setup, digest);
                SettingsConnector log = new(reportDirectory);
                using UpdateConnector updates = new(stage, log.Warn);
                File.WriteAllText(Path.Combine(stage, "installer-ready.json"), JsonSerializer.Serialize(ready));
                Require(updates.LoadReady(new Version(0, 2, 0)) == ready, "The verified EXE must remain available after loading deferred state.");
                await RejectChangedInstallerAsync(updates, ready);
            }
            await RunNativeSetupAsync(setup, target, Path.Combine(reportDirectory, "fresh-install.log"));
            Require(FileVersionInfo.GetVersionInfo(Path.Combine(target, "ScreenLingo.dll")).ProductVersion == ApplicationIdentity.Version, "Fresh installation must deploy the current app version.");
            using (RegistryKey registration = Registry.CurrentUser.OpenSubKey(registryPath) ?? throw new InvalidOperationException("The installer did not register ScreenLingo in Windows Installed apps."))
            {
                Require((string?)registration.GetValue("InstallLocation") == target + Path.DirectorySeparatorChar, "Windows must register the selected installation folder.");
                Require((string?)registration.GetValue("DisplayVersion") == ApplicationIdentity.Version, "Windows must display the installed version.");
            }
            foreach (string name in new[] { "ScreenLingo.exe", "ScreenLingo.dll", "ScreenLingo.deps.json", "ScreenLingo.runtimeconfig.json" })
                File.Copy(Path.Combine(previousPackageDirectory, name), Path.Combine(target, name), true);
            File.WriteAllText(Path.Combine(target, "README.md"), "old package marker");
            Require(FileVersionInfo.GetVersionInfo(Path.Combine(target, "ScreenLingo.dll")).ProductVersion == "0.2.0", "The upgrade check must use the actual previous release.");
            ProcessStartInfo start = new(Path.Combine(target, "ScreenLingo.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using Process oldApp = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the previous app for the update check.");
            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
            while (oldApp.MainWindowHandle == 0 && !oldApp.HasExited)
            {
                await Task.Delay(100, deadline.Token);
                oldApp.Refresh();
            }
            Require(!oldApp.HasExited, "The previous app must be running when native Setup begins.");
            await RunNativeSetupAsync(setup, target, Path.Combine(reportDirectory, "upgrade.log"));
            await oldApp.WaitForExitAsync(deadline.Token);
            Require(FileVersionInfo.GetVersionInfo(Path.Combine(target, "ScreenLingo.dll")).ProductVersion == ApplicationIdentity.Version, "Setup must replace the previous version in the same folder.");
            Require(File.ReadAllText(Path.Combine(target, "README.md")) != "old package marker", "Setup must replace supporting application files.");
            Require((File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null) == originalPreferences, "Installation and update must preserve user preferences.");
            string uninstaller = Path.Combine(target, "unins000.exe");
            Require(File.Exists(uninstaller), "A Windows uninstaller must be included.");
            ProcessStartInfo uninstall = new(uninstaller) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            uninstall.ArgumentList.Add("/VERYSILENT");
            uninstall.ArgumentList.Add("/SUPPRESSMSGBOXES");
            uninstall.ArgumentList.Add("/NORESTART");
            uninstall.ArgumentList.Add("/LOG=" + Path.Combine(reportDirectory, "uninstall.log"));
            using Process removal = Process.Start(uninstall) ?? throw new InvalidOperationException("Could not start the isolated installation's uninstaller.");
            await removal.WaitForExitAsync(deadline.Token);
            Require(removal.ExitCode == 0 && !File.Exists(Path.Combine(target, "ScreenLingo.exe")), "Uninstall must remove the application's installed files.");
            using (RegistryKey? removed = Registry.CurrentUser.OpenSubKey(registryPath))
                Require(removed is null, "Uninstall must remove the Windows Installed apps registration.");
            Require((File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null) == originalPreferences, "Uninstall must preserve user preferences.");
            File.WriteAllText(Path.Combine(reportDirectory, "install-report.json"), JsonSerializer.Serialize(new CheckResult("native Setup EXE lifecycle", true,
                "Fresh installation, selected folder, Windows registration, real running 0.2.0 upgrade, file replacement, deferred state, modified-installer rejection, preference preservation and uninstall passed.")));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(reportDirectory, "install-report.json"), JsonSerializer.Serialize(new CheckResult("native Setup EXE lifecycle", false, error.ToString())));
            return 1;
        }
    }

    private static async Task RejectChangedInstallerAsync(UpdateConnector updates, ReadyUpdate ready)
    {
        try
        {
            using Process unexpected = await updates.StartInstallAsync(ready with { Sha256 = new string('0', 64) }, AppContext.BaseDirectory, CancellationToken.None);
            throw new InvalidOperationException("The updater accepted an installer with a mismatched digest.");
        }
        catch (InvalidDataException) { /* Modified installers must never execute. */ }
    }

    private static async Task RunNativeSetupAsync(string installerPath, string target, string logPath)
    {
        ProcessStartInfo start = new(installerPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOICONS", "/TASKS=", "/DIR=" + target, "/LOG=" + logPath })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the ScreenLingo installer.");
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(deadline.Token);
        Require(process.ExitCode == 0, "Native Setup failed: exit code=" + process.ExitCode + "; log=" + logPath);
    }
}
