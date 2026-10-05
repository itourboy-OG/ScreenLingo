using System;
using System.Windows;
using System.Windows.Media;

namespace ScreenLingo;

public sealed record ColorPalette(Color Backdrop, Color Card, Color Line, Color Ink, Color Muted, Color Accent, Color AccentInk);

public static class Appearance
{
    public static ColorPalette Palette(ColorSkin skin) => skin switch
    {
        ColorSkin.Copper => new(Parse("#211D19"), Parse("#2E2823"), Parse("#57473B"), Parse("#FFF5EB"), Parse("#CEBFB1"), Parse("#F2AF78"), Parse("#302116")),
        ColorSkin.Paper => new(Parse("#F4F0E8"), Parse("#FFFDF8"), Parse("#D4C7B8"), Parse("#302820"), Parse("#736456"), Parse("#97482E"), Parse("#FFF8EF")),
        ColorSkin.Plum => new(Parse("#251B29"), Parse("#34263A"), Parse("#654B6F"), Parse("#FCF0FF"), Parse("#CFB5D7"), Parse("#E1A8EC"), Parse("#341A3B")),
        _ => throw new ArgumentOutOfRangeException(nameof(skin), skin, "Choose a supported color skin.")
    };

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}

/// <summary>Connects the selected skin to WPF resources; Windows high contrast takes precedence.</summary>
public static class ThemeConnector
{
    public static void Apply(ColorSkin skin)
    {
        ColorPalette palette = SystemParameters.HighContrast
            ? new(SystemColors.WindowColor, SystemColors.WindowColor, SystemColors.WindowTextColor,
                SystemColors.WindowTextColor, SystemColors.WindowTextColor, SystemColors.HighlightColor, SystemColors.HighlightTextColor)
            : Appearance.Palette(skin);
        ResourceDictionary resources = Application.Current.Resources;
        resources["Backdrop"] = new SolidColorBrush(palette.Backdrop);
        resources["Card"] = new SolidColorBrush(palette.Card);
        resources["Line"] = new SolidColorBrush(palette.Line);
        resources["Ink"] = new SolidColorBrush(palette.Ink);
        resources["Muted"] = new SolidColorBrush(palette.Muted);
        resources["Accent"] = new SolidColorBrush(palette.Accent);
        resources["AccentInk"] = new SolidColorBrush(palette.AccentInk);
    }
}

public static class ApplicationIdentity
{
    public const string Name = "ScreenLingo";
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? throw new InvalidOperationException("The application assembly has no version.");
}
