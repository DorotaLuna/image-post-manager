using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImagePostManager;

public sealed class CompareViewer : Window
{
    private readonly SemaphoreSlim decodeGate = new(1);
    private readonly PhotoViewport leftViewport, rightViewport;
    private readonly ViewerFullscreen fullscreen;
    private readonly CheckBox synchronize;
    private bool syncing;
    private PhotoViewport active;

    public CompareViewer(Library library, Photo left, Photo right, bool light = false)
    {
        Title = "图片对比";
        Width = 1440; Height = 900; MinWidth = 760; MinHeight = 460;
        PhotoViewerChrome.Initialize(this, light);
        fullscreen = new ViewerFullscreen(this);
        leftViewport = new PhotoViewport(decodeGate);
        rightViewport = new PhotoViewport(decodeGate);
        active = leftViewport;
        synchronize = new CheckBox { Content = "同步缩放 / 拖动", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 18, 8), ToolTip = "两侧使用相同的像素缩放比例，并同步图像中的相对位置" };
        synchronize.SetResourceReference(CheckBox.ForegroundProperty, "Text");
        synchronize.Checked += (_, _) => Synchronize(active);
        leftViewport.ViewChanged += () => Synchronize(leftViewport);
        rightViewport.ViewChanged += () => Synchronize(rightViewport);
        leftViewport.PreviewMouseDown += (_, _) => active = leftViewport;
        rightViewport.PreviewMouseDown += (_, _) => active = rightViewport;
        leftViewport.PreviewMouseWheel += (_, _) => active = leftViewport;
        rightViewport.PreviewMouseWheel += (_, _) => active = rightViewport;
        leftViewport.GotKeyboardFocus += (_, _) => active = leftViewport;
        rightViewport.GotKeyboardFocus += (_, _) => active = rightViewport;

        var root = new DockPanel { Margin = new Thickness(16) };
        var controls = new WrapPanel();
        controls.Children.Add(PhotoViewerChrome.Button("两侧适合窗口", () => { leftViewport.Fit(); rightViewport.Fit(); }, "0"));
        controls.Children.Add(PhotoViewerChrome.Button("两侧 100%", () => { leftViewport.ActualPixels(); rightViewport.ActualPixels(); }, "1 · 原图像素与屏幕像素一一对应"));
        controls.Children.Add(PhotoViewerChrome.Button("−", () => active.ZoomBy(1 / 1.25), "缩小当前一侧"));
        controls.Children.Add(PhotoViewerChrome.Button("+", () => active.ZoomBy(1.25), "放大当前一侧"));
        controls.Children.Add(synchronize);
        controls.Children.Add(PhotoViewerChrome.Button("全屏", fullscreen.Toggle, "F11"));
        controls.Children.Add(PhotoViewerChrome.Button("关闭  Esc", Close));
        var bar = PhotoViewerChrome.Surface(this, controls);
        bar.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        var hint = new TextBlock { Text = "Ctrl + 滚轮缩放当前一侧 · 拖动平移 · 双击切换适合窗口 / 100% · Tab 切换操作侧", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        var leftPane = MakePane(left, leftViewport);
        var rightPane = MakePane(right, rightViewport);
        Grid.SetColumn(rightPane, 2);
        panes.Children.Add(leftPane);
        panes.Children.Add(rightPane);
        root.Children.Add(panes);
        Content = root;

        AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: if (fullscreen.IsFullscreen) fullscreen.Toggle(); else Close(); e.Handled = true; break;
                case Key.F11: if (!e.IsRepeat) fullscreen.Toggle(); e.Handled = true; break;
                case Key.D0: case Key.NumPad0: leftViewport.Fit(); rightViewport.Fit(); e.Handled = true; break;
                case Key.D1: case Key.NumPad1: leftViewport.ActualPixels(); rightViewport.ActualPixels(); e.Handled = true; break;
                case Key.Add: case Key.OemPlus: active.ZoomBy(1.25); e.Handled = true; break;
                case Key.Subtract: case Key.OemMinus: active.ZoomBy(1 / 1.25); e.Handled = true; break;
                case Key.Tab: active = active == leftViewport ? rightViewport : leftViewport; active.Focus(); e.Handled = true; break;
            }
        }), true);
        Loaded += async (_, _) =>
        {
            leftViewport.Focus();
            await Task.WhenAll(leftViewport.LoadAsync(library.Original(left)), rightViewport.LoadAsync(library.Original(right)));
            if (synchronize.IsChecked == true) Synchronize(active);
        };
        Closed += (_, _) => { leftViewport.Dispose(); rightViewport.Dispose(); };
    }

    private Border MakePane(Photo photo, PhotoViewport viewport)
    {
        var pane = new DockPanel();
        var heading = new TextBlock { Text = photo.OriginalName, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 5), ToolTip = photo.OriginalName };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var details = new TextBlock { Margin = new Thickness(4, 0, 4, 8) };
        details.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        viewport.ViewChanged += () => details.Text = viewport.Description;
        viewport.ImageChanged += () => details.Text = viewport.Description;
        DockPanel.SetDock(heading, Dock.Top); pane.Children.Add(heading);
        DockPanel.SetDock(details, Dock.Top); pane.Children.Add(details);
        pane.Children.Add(viewport);
        return PhotoViewerChrome.Surface(this, pane);
    }

    private void Synchronize(PhotoViewport source)
    {
        if (syncing || synchronize.IsChecked != true) return;
        syncing = true;
        try { (source == leftViewport ? rightViewport : leftViewport).ApplyState(source.CaptureState()); }
        finally { syncing = false; }
    }
}
