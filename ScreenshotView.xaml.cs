using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLingo;

/// <summary>Displays one local capture and its translations, independent of subsequent focus and screen changes.</summary>
public partial class ScreenshotView : UserControl
{
    private readonly CapturedImage image;
    private double customScale = 1;
    private Point? panStart;
    private Point panOffset;
    internal CapturedImage CapturedImage => image;
    internal ScreenTranslation? Translation { get; private set; }

    public ScreenshotView(CapturedImage image, string targetTitle)
    {
        this.image = image;
        InitializeComponent();
        using MemoryStream input = new(image.Png);
        BitmapFrame bitmap = BitmapFrame.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        bitmap.Freeze();
        CaptureImage.Source = bitmap;
        CaptureSurface.Width = image.Width;
        CaptureSurface.Height = image.Height;
        CaptureCaption.Text = targetTitle + $" · {image.Width} × {image.Height} · stored in memory only";
        PreviewKeyUp += (_, args) => { if (args.Key is Key.LeftCtrl or Key.RightCtrl) EndPan(); };
    }

    public event Action? CloseRequested;
    private void BackClicked(object sender, RoutedEventArgs args) => CloseRequested?.Invoke();

    public void SetProgress(string detail) => Status.Text = detail;
    public void SetError(string detail) => Status.Text = "Translation stopped: " + detail;

    public void Present(ScreenTranslation result, AppSettings settings, TimeSpan elapsed)
    {
        Translation = result;
        Labels.Content = OverlayWindow.CreateCanvas(image.Width, image.Height, 1, 1, result.Regions, settings);
        TranslationList.Children.Clear();
        foreach (TranslatedRegion region in result.Regions)
        {
            StackPanel text = new();
            text.Children.Add(new TextBlock { Text = region.Source.Text, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 5) });
            text.Children.Add(new TextBlock { Text = region.Translation, TextWrapping = TextWrapping.Wrap, FontSize = settings.OverlayFontSize });
            Border card = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9), Child = text };
            card.SetResourceReference(BackgroundProperty, "Card");
            TranslationList.Children.Add(card);
        }
        string detail = result.RecognizedCount == 0 ? "No readable text found. Try a larger game UI or choose the source language manually."
            : !TranslationRules.NeedsTranslation(result.SourceLanguage, settings.TargetLanguage) ? "Already in your output language. No translation request was needed."
            : $"{result.Regions.Length} / {result.RecognizedCount} labels translated · {TranslationRules.Language(result.SourceLanguage).Name} → {TranslationRules.Language(settings.TargetLanguage).Name} · {elapsed.TotalSeconds:0.0}s";
        Status.Text = detail;
        if (result.Regions.IsEmpty) TranslationList.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Caption") });
    }

    private void ZoomChanged(object sender, SelectionChangedEventArgs args) { if (Viewport is not null) ResizeImage(); }
    private void ViewportChanged(object sender, SizeChangedEventArgs args) => ResizeImage();

    private void ResizeImage()
    {
        string selected = (string)Zoom.SelectedValue;
        if (selected == "fit")
        {
            ImageView.Width = Math.Max(1, Viewport.ActualWidth - 20);
            ImageView.Height = Math.Max(1, Viewport.ActualHeight - 20);
            return;
        }
        double scale = selected == "custom" ? customScale : double.Parse(selected, CultureInfo.InvariantCulture);
        ImageView.Width = image.Width * scale;
        ImageView.Height = image.Height * scale;
    }

    private void ZoomWheel(object sender, MouseWheelEventArgs args)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        Vector renderedWidth = CaptureSurface.TranslatePoint(new Point(image.Width, 0), Viewport) - CaptureSurface.TranslatePoint(new Point(), Viewport);
        double scale = renderedWidth.Length / image.Width * Math.Pow(1.15, args.Delta / 120.0);
        ZoomAt(Math.Clamp(scale, 0.1, 8), args.GetPosition(Viewport));
        args.Handled = true;
    }

    /// <summary>Changes screenshot scale around a viewport point, preserving the image location under the pointer.</summary>
    internal void ZoomAt(double scale, Point pointer)
    {
        if (!double.IsFinite(scale) || scale is < 0.1 or > 8) throw new ArgumentOutOfRangeException(nameof(scale), "Screenshot zoom must be between 10% and 800%.");
        Point imagePoint = Viewport.TranslatePoint(pointer, CaptureSurface);
        customScale = scale;
        CustomZoom.Content = (scale * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        CustomZoom.Visibility = Visibility.Visible;
        Zoom.SelectedValue = "custom";
        ResizeImage();
        Viewport.UpdateLayout();
        Point moved = CaptureSurface.TranslatePoint(imagePoint, Viewport);
        PanTo(new Point(Viewport.HorizontalOffset + moved.X - pointer.X, Viewport.VerticalOffset + moved.Y - pointer.Y));
    }

    /// <summary>Moves the frozen image through the native scroll viewer; its bounds limit the offsets.</summary>
    internal void PanTo(Point offset)
    {
        Viewport.ScrollToHorizontalOffset(offset.X);
        Viewport.ScrollToVerticalOffset(offset.Y);
    }

    private void StartPan(object sender, MouseButtonEventArgs args)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        panStart = args.GetPosition(Viewport);
        panOffset = new Point(Viewport.HorizontalOffset, Viewport.VerticalOffset);
        if (!CaptureSurface.CaptureMouse()) throw new InvalidOperationException("The screenshot could not capture the pointer for dragging.");
        CaptureSurface.Cursor = Cursors.SizeAll;
        args.Handled = true;
    }

    private void MovePan(object sender, MouseEventArgs args)
    {
        if (panStart is not Point start) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) { EndPan(); return; }
        Vector movement = args.GetPosition(Viewport) - start;
        PanTo(panOffset - movement);
        args.Handled = true;
    }

    private void StopPan(object sender, MouseButtonEventArgs args) { if (panStart is not null) { EndPan(); args.Handled = true; } }
    private void PanCaptureLost(object sender, MouseEventArgs args) { panStart = null; CaptureSurface.ClearValue(CursorProperty); }
    private void EndPan() { panStart = null; CaptureSurface.ReleaseMouseCapture(); CaptureSurface.ClearValue(CursorProperty); }

    private void LabelsChanged(object sender, RoutedEventArgs args)
    {
        if (Labels is not null) Labels.Visibility = ShowLabels.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }
}
