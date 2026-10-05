using System;
using System.Collections.Immutable;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenLingo;

/// <summary>Connects translated labels to a non-activating, click-through native overlay excluded from capture.</summary>
public sealed class OverlayWindow : Window
{
    private Canvas canvas = new();

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = canvas;
        SourceInitialized += (_, _) => WindowsConnector.MakeOverlay(new WindowInteropHelper(this).Handle);
    }

    public void Present(CaptureTarget target, CapturedImage image, ImmutableArray<TranslatedRegion> regions, AppSettings settings)
    {
        if (regions.IsEmpty) { Hide(); return; }
        bool opening = !IsVisible;
        if (opening) Show();
        WindowsConnector.PositionOverlay(new WindowInteropHelper(this).Handle, target.Bounds);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = target.Bounds.Width / (double)image.Width / dpi.DpiScaleX;
        double scaleY = target.Bounds.Height / (double)image.Height / dpi.DpiScaleY;
        canvas = CreateCanvas(target.Bounds.Width / dpi.DpiScaleX, target.Bounds.Height / dpi.DpiScaleY, scaleX, scaleY, regions, settings);
        Content = canvas;
        if (opening && !settings.ReduceMotion && SystemParameters.ClientAreaAnimation)
            canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(120)));
    }

    public static Canvas CreateCanvas(double width, double height, double scaleX, double scaleY,
        ImmutableArray<TranslatedRegion> regions, AppSettings settings)
    {
        Canvas labels = new() { Width = width, Height = height };
        foreach (TranslatedRegion region in regions)
        {
            PixelRect position = region.Source.Bounds;
            double x = Math.Max(0, position.X * scaleX - 4);
            double y = Math.Max(0, position.Y * scaleY - 3);
            double availableWidth = Math.Max(1, width - x);
            TextBlock text = new()
            {
                Text = region.Translation, FontFamily = new FontFamily("Segoe UI"), FontSize = settings.OverlayFontSize,
                Foreground = (Brush)Application.Current.Resources["Ink"],
                TextWrapping = TextWrapping.Wrap, MaxWidth = Math.Max(1, Math.Min(availableWidth - 1, Math.Max(180, position.Width * scaleX + 60)))
            };
            Border background = new()
            {
                Background = new SolidColorBrush(((SolidColorBrush)Application.Current.Resources["Card"]).Color)
                    { Opacity = SystemParameters.HighContrast ? 1 : settings.OverlayOpacity },
                BorderBrush = (Brush)Application.Current.Resources["Line"],
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 3, 6, 4),
                MaxWidth = availableWidth, Child = text, ToolTip = region.Source.Text, Tag = region.Source
            };
            Canvas.SetLeft(background, x);
            Canvas.SetTop(background, y);
            labels.Children.Add(background);
        }
        return labels;
    }

    public void RetainStationary(ImmutableArray<TextRegion> regions)
    {
        foreach (Border label in canvas.Children.OfType<Border>().ToArray())
        {
            TextRegion source = (TextRegion)label.Tag;
            if (!regions.Any(region => region.Text == source.Text &&
                Math.Abs(region.Bounds.X - source.Bounds.X) <= 4 && Math.Abs(region.Bounds.Y - source.Bounds.Y) <= 4))
                canvas.Children.Remove(label);
        }
        if (canvas.Children.Count == 0) Hide();
    }
}
