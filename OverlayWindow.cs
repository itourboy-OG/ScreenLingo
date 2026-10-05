using System;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenLingo;

/// <summary>Connects translated labels to a non-activating, click-through native overlay excluded from capture.</summary>
public sealed class OverlayWindow : Window
{
    private readonly Canvas canvas = new();

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
        if (!IsVisible) Show();
        WindowsConnector.PositionOverlay(new WindowInteropHelper(this).Handle, target.Bounds);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = target.Bounds.Width / (double)image.Width / dpi.DpiScaleX;
        double scaleY = target.Bounds.Height / (double)image.Height / dpi.DpiScaleY;
        canvas.Children.Clear();
        foreach (TranslatedRegion region in regions)
        {
            PixelRect position = region.Source.Bounds;
            double x = Math.Max(0, position.X * scaleX - 4);
            double y = Math.Max(0, position.Y * scaleY - 3);
            double availableWidth = Math.Max(1, target.Bounds.Width / dpi.DpiScaleX - x);
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
                MaxWidth = availableWidth, Child = text, ToolTip = region.Source.Text
            };
            Canvas.SetLeft(background, x);
            Canvas.SetTop(background, y);
            canvas.Children.Add(background);
        }
        if (!settings.ReduceMotion && SystemParameters.ClientAreaAnimation)
            canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(120)));
    }
}
