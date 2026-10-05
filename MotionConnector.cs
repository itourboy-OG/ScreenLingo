using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenLingo;

/// <summary>Connects short native WPF animations to app and Windows motion preferences.</summary>
public static class MotionConnector
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(MotionConnector), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, PreferenceChanged));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    private static void PreferenceChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement spinner || spinner.RenderTransform is not RotateTransform rotation) return;
        rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if ((bool)args.NewValue && spinner.IsVisible) StartSpinner(spinner);
    }

    public static void ApplyPreferences(bool reduceMotion)
    {
        bool enabled = !reduceMotion && SystemParameters.ClientAreaAnimation;
        Application.Current.Resources["MotionEnabled"] = enabled;
    }

    public static void Enter(FrameworkElement element, TimeSpan duration, double distance)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        element.IsHitTestVisible = true;
        element.Visibility = Visibility.Visible;
        TranslateTransform transform = new();
        element.RenderTransform = transform;
        if (!GetEnabled(element) || !SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(distance, 0, duration)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    public static void Dismiss(FrameworkElement element)
    {
        if (!GetEnabled(element) || !SystemParameters.ClientAreaAnimation) { element.Visibility = Visibility.Collapsed; return; }
        element.IsHitTestVisible = false;
        DoubleAnimation fade = new(element.Opacity, 0, TimeSpan.FromMilliseconds(167)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { element.Visibility = Visibility.Collapsed; element.IsHitTestVisible = true; };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static void ShowProgress(ProgressBar progress, double value)
    {
        double previous = progress.Value;
        progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
        progress.Value = value;
        if (GetEnabled(progress) && SystemParameters.ClientAreaAnimation)
            progress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop });
    }

    public static void StartSpinner(FrameworkElement spinner)
    {
        spinner.Visibility = Visibility.Visible;
        RotateTransform rotation = new();
        spinner.RenderTransform = rotation;
        if (GetEnabled(spinner) && SystemParameters.ClientAreaAnimation)
            rotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    public static void StopSpinner(FrameworkElement spinner)
    {
        if (spinner.RenderTransform is RotateTransform rotation) rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        spinner.Visibility = Visibility.Collapsed;
    }
}
