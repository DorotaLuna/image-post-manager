using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImagePostManager;

internal readonly record struct PhotoBitmap(BitmapSource Bitmap, int OriginalWidth, int OriginalHeight);

internal static class PhotoBitmapLoader
{
    // A zero limit explicitly requests original pixels. Streams close before returning.
    internal static PhotoBitmap Read(string path, int maximumPreviewPixels)
    {
        int width, height;
        using (var stream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }
        using var input = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (maximumPreviewPixels > 0 && Math.Max(width, height) > maximumPreviewPixels)
        {
            if (width >= height) bitmap.DecodePixelWidth = maximumPreviewPixels;
            else bitmap.DecodePixelHeight = maximumPreviewPixels;
        }
        bitmap.StreamSource = input;
        bitmap.EndInit();
        bitmap.Freeze();
        return new PhotoBitmap(bitmap, width, height);
    }
}

internal readonly record struct PhotoViewState(double Zoom, double CenterX, double CenterY, bool IsFit);

/// <summary>A clipped image surface whose zoom is measured in source pixels per display pixel.</summary>
internal sealed class PhotoViewport : Grid, IDisposable
{
    private readonly SemaphoreSlim decodeGate;
    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly TextBlock message = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, MaxWidth = 500, Margin = new Thickness(24), IsHitTestVisible = false
    };
    internal Image Picture { get; } = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private string? path;
    private int generation;
    private bool disposed, loadingOriginal, originalLoadFailed, pendingActual;
    private int originalWidth, originalHeight;
    private double zoom = 1;
    private Vector pan;
    private Point dragStart;
    private Vector dragPan;
    private bool isFit = true;
    internal event Action? ViewChanged;
    internal event Action? ImageChanged;
    internal double Zoom => zoom;
    internal bool HasImage => Picture.Source != null;
    internal string Description => originalWidth == 0 ? "" : $"{originalWidth} × {originalHeight} · {zoom:P0}" +
        (Picture.Source is BitmapSource source && source.PixelWidth == originalWidth && source.PixelHeight == originalHeight ? " · 原图" : " · 预览");

    internal PhotoViewport(SemaphoreSlim decodeGate)
    {
        this.decodeGate = decodeGate;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        message.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        canvas.Children.Add(Picture);
        Children.Add(canvas);
        Children.Add(message);
        SizeChanged += (_, _) => { if (isFit) Fit(); else Render(); };
        PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            ZoomWheel(e.Delta, e.GetPosition(this));
            e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            if (!HasImage) return;
            if (e.ClickCount == 2)
            {
                if (isFit) ActualPixels(); else Fit();
                e.Handled = true;
                return;
            }
            dragStart = e.GetPosition(this);
            dragPan = pan;
            if (CaptureMouse()) Cursor = Cursors.SizeAll;
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            pan = dragPan + (e.GetPosition(this) - dragStart);
            Render();
            ViewChanged?.Invoke();
        };
        MouseLeftButtonUp += (_, e) => { if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; } };
        LostMouseCapture += (_, _) => Cursor = Cursors.Arrow;
    }

    internal async Task LoadAsync(string imagePath)
    {
        if (disposed) return;
        int token = ++generation;
        path = imagePath;
        originalWidth = originalHeight = 0;
        loadingOriginal = originalLoadFailed = pendingActual = false;
        Picture.Source = null;
        pan = default;
        isFit = true;
        message.Text = "正在加载…";
        ImageChanged?.Invoke();
        var dpi = VisualTreeHelper.GetDpi(this);
        int limit = (int)Math.Clamp(Math.Max(ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY), 640, 2048);
        await decodeGate.WaitAsync();
        try
        {
            if (disposed || token != generation) return;
            var result = await Task.Run(() => PhotoBitmapLoader.Read(imagePath, limit));
            if (disposed || token != generation) return;
            originalWidth = result.OriginalWidth;
            originalHeight = result.OriginalHeight;
            Picture.Source = result.Bitmap;
            message.Text = "";
            if (pendingActual) ActualPixels(); else Fit();
        }
        catch (Exception ex) { if (!disposed && token == generation) message.Text = "无法加载图片：" + ex.Message; }
        finally { decodeGate.Release(); }
    }

    internal static double FitScale(int width, int height, double viewportWidth, double viewportHeight, double dpiX, double dpiY) =>
        width <= 0 || height <= 0 ? 1 : Math.Max(double.Epsilon, Math.Min(1, Math.Min(viewportWidth * dpiX / width, viewportHeight * dpiY / height)));

    internal void Fit()
    {
        isFit = true;
        pendingActual = false;
        var dpi = VisualTreeHelper.GetDpi(this);
        zoom = FitScale(originalWidth, originalHeight, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight), dpi.DpiScaleX, dpi.DpiScaleY);
        pan = default;
        Render();
        ViewChanged?.Invoke();
        _ = EnsureOriginalAsync();
    }

    internal void ActualPixels()
    {
        pendingActual = true;
        if (originalWidth == 0) return;
        SetZoom(1, new Point(ActualWidth / 2, ActualHeight / 2));
    }

    internal void ZoomBy(double factor) => SetZoom(zoom * factor, new Point(ActualWidth / 2, ActualHeight / 2));
    internal void ZoomWheel(int delta, Point anchor) { if (delta != 0) SetZoom(zoom * Math.Pow(1.2, delta / 120.0), anchor); }

    private void SetZoom(double value, Point anchor)
    {
        if (originalWidth == 0 || disposed) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double oldWidth = originalWidth * zoom / dpi.DpiScaleX, oldHeight = originalHeight * zoom / dpi.DpiScaleY;
        double x = (anchor.X - (ActualWidth - oldWidth) / 2 - pan.X) / oldWidth;
        double y = (anchor.Y - (ActualHeight - oldHeight) / 2 - pan.Y) / oldHeight;
        double fit = FitScale(originalWidth, originalHeight, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight), dpi.DpiScaleX, dpi.DpiScaleY);
        zoom = Math.Clamp(value, Math.Min(.01, fit), 32);
        isFit = false;
        double width = originalWidth * zoom / dpi.DpiScaleX, height = originalHeight * zoom / dpi.DpiScaleY;
        pan = new Vector(anchor.X - (ActualWidth - width) / 2 - x * width, anchor.Y - (ActualHeight - height) / 2 - y * height);
        Render();
        ViewChanged?.Invoke();
        _ = EnsureOriginalAsync();
    }

    internal PhotoViewState CaptureState()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new PhotoViewState(zoom,
            originalWidth == 0 ? .5 : .5 - pan.X / (originalWidth * zoom / dpi.DpiScaleX),
            originalHeight == 0 ? .5 : .5 - pan.Y / (originalHeight * zoom / dpi.DpiScaleY), isFit);
    }

    internal void ApplyState(PhotoViewState state)
    {
        if (originalWidth == 0 || disposed) return;
        if (state.IsFit) { Fit(); return; }
        isFit = false;
        zoom = Math.Clamp(state.Zoom, .000001, 32);
        var dpi = VisualTreeHelper.GetDpi(this);
        pan = new Vector((.5 - state.CenterX) * originalWidth * zoom / dpi.DpiScaleX,
            (.5 - state.CenterY) * originalHeight * zoom / dpi.DpiScaleY);
        Render();
        ViewChanged?.Invoke();
        _ = EnsureOriginalAsync();
    }

    private async Task EnsureOriginalAsync()
    {
        if (disposed || loadingOriginal || originalLoadFailed || path == null || Picture.Source is not BitmapSource bitmap) return;
        if (bitmap.PixelWidth >= originalWidth && bitmap.PixelHeight >= originalHeight) return;
        if (originalWidth * zoom <= bitmap.PixelWidth + 1 && originalHeight * zoom <= bitmap.PixelHeight + 1) return;
        int token = generation;
        string imagePath = path;
        loadingOriginal = true;
        message.Text = "正在读取原图…";
        await decodeGate.WaitAsync();
        try
        {
            if (disposed || token != generation) return;
            var original = await Task.Run(() => PhotoBitmapLoader.Read(imagePath, 0));
            if (disposed || token != generation) return;
            Picture.Source = original.Bitmap;
            message.Text = "";
            Render();
            ImageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            if (!disposed && token == generation)
            {
                originalLoadFailed = true;
                message.Text = "原图加载失败：" + ex.Message;
            }
        }
        finally { decodeGate.Release(); if (token == generation) loadingOriginal = false; }
    }

    private void Render()
    {
        if (originalWidth == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double width = originalWidth * zoom / dpi.DpiScaleX, height = originalHeight * zoom / dpi.DpiScaleY;
        double maxX = Math.Max(0, (width - ActualWidth) / 2), maxY = Math.Max(0, (height - ActualHeight) / 2);
        pan = new Vector(Math.Clamp(pan.X, -maxX, maxX), Math.Clamp(pan.Y, -maxY, maxY));
        Picture.Width = width;
        Picture.Height = height;
        Canvas.SetLeft(Picture, Math.Round(((ActualWidth - width) / 2 + pan.X) * dpi.DpiScaleX) / dpi.DpiScaleX);
        Canvas.SetTop(Picture, Math.Round(((ActualHeight - height) / 2 + pan.Y) * dpi.DpiScaleY) / dpi.DpiScaleY);
        RenderOptions.SetBitmapScalingMode(Picture, zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (isFit) Fit(); else { Render(); ViewChanged?.Invoke(); }
    }

    internal void Clear()
    {
        generation++;
        path = null;
        originalWidth = originalHeight = 0;
        Picture.Source = null;
        message.Text = "";
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    public void Dispose() { disposed = true; Clear(); }
}

internal static class PhotoViewerChrome
{
    internal static void Initialize(Window window, bool light)
    {
        window.Background = new SolidColorBrush(WindowAppearance.BackgroundColor(light));
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.UseLayoutRounding = true;
        window.SourceInitialized += (_, _) => WindowAppearance.Apply(window, light);
        window.Resources["Card"] = new SolidColorBrush(light ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(230, 37, 37, 43));
        window.Resources["Text"] = light ? Brushes.Black : Brushes.White;
        window.Resources["Muted"] = light ? Brushes.DimGray : Brushes.LightGray;
        window.Resources["Line"] = new SolidColorBrush(light ? Color.FromRgb(221, 222, 230) : Color.FromRgb(61, 61, 70));
        window.Resources["Hover"] = new SolidColorBrush(light ? Color.FromRgb(235, 237, 244) : Color.FromRgb(53, 53, 64));
        window.Resources["Accent"] = new SolidColorBrush(Color.FromRgb(125, 155, 224));
        window.Resources["SubtleAccent"] = window.Resources["Accent"];
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ImagePostManager;component/ModernStyles.xaml", UriKind.Relative) });
    }

    internal static Button Button(string text, Action click, string? tooltip = null)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), ToolTip = tooltip };
        button.Click += (_, _) => click();
        return button;
    }

    internal static Border Surface(Window window, UIElement child) => new()
    {
        Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10, 4, 2),
        Background = (Brush)window.Resources["Card"], BorderBrush = (Brush)window.Resources["Line"], BorderThickness = new Thickness(1)
    };
}

internal sealed class ViewerFullscreen(Window window)
{
    private WindowStyle style;
    private ResizeMode resize;
    private WindowState state;
    internal bool IsFullscreen { get; private set; }
    internal void Toggle()
    {
        if (IsFullscreen)
        {
            window.WindowState = WindowState.Normal;
            window.WindowStyle = style;
            window.ResizeMode = resize;
            window.WindowState = state;
            IsFullscreen = false;
        }
        else
        {
            style = window.WindowStyle;
            resize = window.ResizeMode;
            state = window.WindowState;
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
            IsFullscreen = true;
        }
    }
}
