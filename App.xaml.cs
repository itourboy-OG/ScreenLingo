using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ScreenLingo;

/// <summary>Connects application startup, fatal-error reporting and the optional integration smoke check.</summary>
public partial class App : Application
{
    private async void OnStartup(object sender, StartupEventArgs args)
    {
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            if (MainWindow is ScreenLingo.MainWindow panel && panel.IsLoaded)
            {
                panel.ShowFatalError(eventArgs.Exception);
                return;
            }
            StackPanel content = new() { Margin = new Thickness(24) };
            content.Children.Add(new TextBlock { Text = "ScreenLingo could not start", FontSize = 22, Margin = new Thickness(0, 0, 0, 18) });
            content.Children.Add(new TextBox { Text = eventArgs.Exception.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 330 });
            Button close = new() { Content = "Close ScreenLingo", Margin = new Thickness(0, 16, 0, 0) };
            close.Click += (_, _) => Shutdown(1);
            content.Children.Add(close);
            Window errorWindow = MainWindow ?? new Window { Title = ApplicationIdentity.Name, Width = 580, Height = 510, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            errorWindow.Content = content;
            MainWindow = errorWindow;
            errorWindow.Closed += (_, _) => Shutdown(1);
            errorWindow.Show();
        };
        ThemeConnector.Apply(ColorSkin.Copper);
        MotionConnector.ApplyPreferences(false);
        if (args.Args.Contains("--motion-check", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (args.Args.Length != 2) throw new ArgumentException("Usage: ScreenLingo.exe --motion-check <report-directory>");
            Shutdown(await SmokeCheck.RunMotionAsync(Path.GetFullPath(args.Args[1])));
            return;
        }
        if (args.Args.Contains("--capture-check", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (args.Args.Length != 3) throw new ArgumentException("Usage: ScreenLingo.exe --capture-check <process-id> <report-directory>");
            Shutdown(await SmokeCheck.RunCaptureAsync(int.Parse(args.Args[1], System.Globalization.CultureInfo.InvariantCulture), Path.GetFullPath(args.Args[2])));
            return;
        }
        if (args.Args.Contains("--game-check", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (args.Args.Length != 3) throw new ArgumentException("Usage: ScreenLingo.exe --game-check <process-id> <report-directory>");
            Shutdown(await SmokeCheck.RunGameAsync(int.Parse(args.Args[1], System.Globalization.CultureInfo.InvariantCulture), Path.GetFullPath(args.Args[2])));
            return;
        }
        if (args.Args.Contains("--recognition-check", StringComparer.Ordinal))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (args.Args.Length != 4) throw new ArgumentException("Usage: ScreenLingo.exe --recognition-check <image-path> <source-language> <report-directory>");
            Shutdown(await SmokeCheck.RunRecognitionAsync(Path.GetFullPath(args.Args[1]), args.Args[2], Path.GetFullPath(args.Args[3])));
            return;
        }
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
            if (args.Args.Length != 4) throw new ArgumentException("Usage: ScreenLingo.exe --install-check <report-directory> <setup-exe> <previous-0.2.0-package-directory>");
            Shutdown(await SmokeCheck.RunInstallAsync(Path.GetFullPath(args.Args[1]), Path.GetFullPath(args.Args[2]), Path.GetFullPath(args.Args[3])));
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
