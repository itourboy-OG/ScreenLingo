using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ScreenLingo;

/// <summary>Connects the preferences form to a new validated settings value.</summary>
public partial class SettingsView : UserControl
{
    private readonly AppSettings initial;
    private readonly Func<Task<string>> checkUpdates;
    public event Action<AppSettings>? Saved;
    public event Action? Cancelled;

    public SettingsView(AppSettings settings, Func<Task<string>> checkUpdates)
    {
        initial = settings;
        this.checkUpdates = checkUpdates;
        InitializeComponent();
        Skin.SelectedValue = settings.ColorSkin.ToString();
        Provider.SelectedValue = settings.Provider.ToString();
        Scope.SelectedValue = settings.Scope.ToString();
        Mode.SelectedValue = settings.Mode.ToString();
        Interval.SelectedValue = settings.ScanIntervalMs.ToString(CultureInfo.InvariantCulture);
        LocalModel.Text = settings.OllamaModel;
        TextSize.Value = settings.OverlayFontSize;
        BackgroundOpacity.Value = settings.OverlayOpacity;
        ReducedMotion.IsChecked = settings.ReduceMotion;
    }

    private void ProviderChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProviderHelp is null || LocalModelSettings is null) return;
        bool offline = (string?)Provider.SelectedValue == "Ollama";
        LocalModelSettings.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        ProviderHelp.Text = offline
            ? "Text stays on your PC. Requires Ollama and a downloaded model; speed depends on your hardware."
            : "Recognized text is sent to MyMemory. Screenshots stay on your PC. Anonymous limit: 5,000 characters per day. Cached labels are reused.";
    }

    private void SaveClicked(object sender, RoutedEventArgs args)
    {
        AppSettings result = TranslationRules.ValidateSettings(initial with
        {
            ColorSkin = Enum.Parse<ColorSkin>((string)Skin.SelectedValue),
            Provider = Enum.Parse<TranslationProvider>((string)Provider.SelectedValue),
            Scope = Enum.Parse<CaptureScope>((string)Scope.SelectedValue),
            Mode = Enum.Parse<TranslationMode>((string)Mode.SelectedValue),
            ScanIntervalMs = int.Parse((string)Interval.SelectedValue, CultureInfo.InvariantCulture),
            OverlayFontSize = checked((int)TextSize.Value), OverlayOpacity = BackgroundOpacity.Value,
            ReduceMotion = ReducedMotion.IsChecked == true, OllamaModel = LocalModel.Text.Trim()
        });
        Saved?.Invoke(result);
    }

    private void CancelClicked(object sender, RoutedEventArgs args) => Cancelled?.Invoke();

    private void OpenOllamaClicked(object sender, RoutedEventArgs args) =>
        _ = Process.Start(new ProcessStartInfo("https://ollama.com/download/windows") { UseShellExecute = true });

    private async void CheckUpdatesClicked(object sender, RoutedEventArgs args)
    {
        CheckUpdates.IsEnabled = false;
        UpdateStatus.Text = "Checking GitHub for updates…";
        try { UpdateStatus.Text = await checkUpdates(); }
        finally { CheckUpdates.IsEnabled = true; }
    }
}
