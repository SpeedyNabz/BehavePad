using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BehavePad.ViewModels;

namespace BehavePad;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        DataContext = shell;
        StateChanged += (_, _) => UpdateMaximizedLayout();
    }

    /// <summary>Renders the window content to a PNG. Used for documentation and visual checks.</summary>
    public void SaveScreenshot(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
        }

        bitmap.Render(background);
        bitmap.Render(Root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void UpdateMaximizedLayout()
    {
        var maximized = WindowState == WindowState.Maximized;

        // A borderless maximized window extends past the screen edge by the resize border.
        Root.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore down" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore down" : "Maximize");
    }

    /// <summary>A short rise and fade on each page, so switching pages reads as a move rather than a blink.</summary>
    private void OnPageChanged(object sender, DataTransferEventArgs e)
    {
        var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));

        PageHost.RenderTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        PageHost.BeginAnimation(OpacityProperty, fade);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
