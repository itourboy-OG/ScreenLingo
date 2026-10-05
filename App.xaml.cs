using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace ScreenLingo;

/// <summary>Connects application startup, fatal-error reporting and the optional integration smoke check.</summary>
public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs args)
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            MessageBox.Show(eventArgs.Exception.ToString(), "ScreenLingo · Application error", MessageBoxButton.OK, MessageBoxImage.Error);
            eventArgs.Handled = true;
            Shutdown(1);
        };
        ThemeConnector.Apply(ColorSkin.Copper);
        if (args.Args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string reportDirectory = args.Args.Length == 2 ? Path.GetFullPath(args.Args[1]) :
                throw new ArgumentException("Usage: \"ScreenLingo.exe\" --smoke-test <report-directory>");
            int result = await SmokeCheck.RunAsync(reportDirectory);
            Shutdown(result);
            return;
        }
        if (args.Args.Contains("--appearance-check", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string reportDirectory = args.Args.Length == 2 ? Path.GetFullPath(args.Args[1]) :
                throw new ArgumentException("Usage: \"ScreenLingo.exe\" --appearance-check <report-directory>");
            int result = await SmokeCheck.RunAppearanceAsync(reportDirectory);
            Shutdown(result);
            return;
        }
        SettingsConnector store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenLingo"));
        if (args.Args.Contains("--install-check", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (args.Args.Length != 3) throw new ArgumentException("Usage: ScreenLingo.exe --install-check <report-directory> <package-zip>");
            Shutdown(await SmokeCheck.RunInstallAsync(Path.GetFullPath(args.Args[1]), Path.GetFullPath(args.Args[2])));
            return;
        }
        if (args.Args.Contains("--update-check", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (args.Args.Length != 2) throw new ArgumentException("Usage: ScreenLingo.exe --update-check <report-directory>");
            Shutdown(await SmokeCheck.RunUpdateAsync(Path.GetFullPath(args.Args[1])));
            return;
        }
        MainWindow window = new(store, store.Load());
        MainWindow = window;
        window.Show();
        await window.CheckForUpdatesAsync();
    }
}
