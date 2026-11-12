using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImagePostManager;

public sealed class Viewer : Window
{
    private readonly Library library;
    private Post post => library.Data.Posts.First(p => p.Images.Any(photo => photo.Id == CurrentPhoto.Id));
    private readonly string scope;
    private readonly List<UIElement> chrome = [];
    private readonly System.Windows.Threading.DispatcherTimer chromeTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool manuallyHidden;
    private readonly List<Photo> displayOrder;
    private int index, wheelRemainder;
    private long lastWheelAt;
    private bool deleting, closed;
    private readonly SemaphoreSlim decodeGate = new(1);
    private readonly PhotoViewport viewport;
    private readonly Image picture;
    private readonly TextBlock caption = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 8) };
    private readonly TextBlock zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 8) };
    private readonly Button favorite;
    private readonly Button undo;
    private readonly ViewerFullscreen fullscreen;
    private (string Id, int Index)? lastDeleted;
    private Photo CurrentPhoto => displayOrder[index];

    public Viewer(Library library, Post post, int index, bool light = false, IReadOnlyList<Photo>? displayOrder = null, string scope = "当前图组")
    {
        this.library = library;
        this.scope = scope;
        this.displayOrder = (displayOrder ?? post.Images.AsEnumerable().Reverse().ToList()).DistinctBy(p => p.Id).ToList();
        this.index = Math.Clamp(index, 0, Math.Max(0, this.displayOrder.Count - 1));
        Title = post.DisplayTitle;
        Width = 1180; Height = 850; MinWidth = 700; MinHeight = 440;
        PhotoViewerChrome.Initialize(this, light);
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        zoomLabel.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        viewport = new PhotoViewport(decodeGate);
        picture = viewport.Picture;
        viewport.ViewChanged += () => zoomLabel.Text = viewport.Description;
        viewport.ImageChanged += () => zoomLabel.Text = viewport.Description;
        fullscreen = new ViewerFullscreen(this);

        var root = new DockPanel { Margin = new Thickness(16) };
        var bar = new StackPanel();
        bar.Children.Add(caption);
        var controls = new WrapPanel();
        controls.Children.Add(PhotoViewerChrome.Button("适合窗口", viewport.Fit, "0 · 双击图像切换适合窗口 / 100%"));
        controls.Children.Add(PhotoViewerChrome.Button("100%", viewport.ActualPixels, "1 · 一个原图像素对应一个屏幕像素"));
        controls.Children.Add(PhotoViewerChrome.Button("−", () => viewport.ZoomBy(1 / 1.25), "缩小 · Ctrl + 滚轮"));
        controls.Children.Add(PhotoViewerChrome.Button("+", () => viewport.ZoomBy(1.25), "放大 · Ctrl + 滚轮"));
        controls.Children.Add(zoomLabel);
        favorite = PhotoViewerChrome.Button("☆ 收藏", ToggleFavorite, "F · 收藏 / 取消收藏");
        controls.Children.Add(favorite);
        controls.Children.Add(PhotoViewerChrome.Button("删除", () => _ = DeleteCurrent(), "Delete · 删除后可以撤销"));
        undo = PhotoViewerChrome.Button("撤销删除", () => _ = UndoDelete(), "Ctrl + Z · 撤销最近一次删除");
        undo.IsEnabled = false;
        controls.Children.Add(undo);
        controls.Children.Add(PhotoViewerChrome.Button("全屏", ToggleFullscreen, "F11 · Esc 退出全屏"));
        controls.Children.Add(PhotoViewerChrome.Button("隐藏工具栏", ToggleChrome, "Tab · 显示 / 隐藏工具栏"));
        controls.Children.Add(PhotoViewerChrome.Button("关闭  Esc", Close));
        bar.Children.Add(controls);
        var barSurface = PhotoViewerChrome.Surface(this, bar);
        barSurface.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(barSurface, Dock.Top);
        root.Children.Add(barSurface);
        var hint = new TextBlock { Text = "滚轮 / ← → 翻页 · Ctrl + 滚轮缩放 · 拖动平移 · 双击切换 100%", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        var previous = PhotoViewerChrome.Button("❮", () => Navigate(-1));
        previous.Width = 44; previous.Height = 54; previous.FontSize = 22; previous.Padding = new Thickness(0); previous.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(previous, Dock.Left); root.Children.Add(previous);
        var next = PhotoViewerChrome.Button("❯", () => Navigate(1));
        next.Width = 44; next.Height = 54; next.FontSize = 22; next.Padding = new Thickness(0); next.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(next, Dock.Right); root.Children.Add(next);
        root.Children.Add(viewport);
        Content = root;
        chrome.AddRange([barSurface, hint, previous, next]);
        chromeTimer.Tick += (_, _) => { chromeTimer.Stop(); if (fullscreen.IsFullscreen && !chrome.Any(item => item.IsMouseOver || item.IsKeyboardFocusWithin)) SetChrome(false); };
        PreviewMouseMove += (_, _) => { if (!manuallyHidden) { SetChrome(true); RestartChromeTimer(); } };


        AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (deleting || e.Delta == 0) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                wheelRemainder = 0;
                viewport.ZoomWheel(e.Delta, e.GetPosition(viewport));
                return;
            }
            long now = Environment.TickCount64;
            if (now - lastWheelAt > 400 || Math.Sign(wheelRemainder) != Math.Sign(e.Delta)) wheelRemainder = 0;
            lastWheelAt = now;
            wheelRemainder += e.Delta;
            if (Math.Abs(wheelRemainder) >= Mouse.MouseWheelDeltaForOneLine)
            {
                int direction = -Math.Sign(wheelRemainder);
                wheelRemainder %= Mouse.MouseWheelDeltaForOneLine;
                Navigate(direction);
            }
        };
        Closing += (_, e) => { if (deleting) e.Cancel = true; };
        Loaded += (_, _) => { Load(); viewport.Focus(); };
        Closed += (_, _) => { closed = true; chromeTimer.Stop(); viewport.Dispose(); };
    }

    private void SetChrome(bool visible)
    {
        foreach (var item in chrome) item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
    private void RestartChromeTimer() { chromeTimer.Stop(); if (fullscreen.IsFullscreen) chromeTimer.Start(); }
    private void ToggleChrome() { manuallyHidden = !manuallyHidden; SetChrome(!manuallyHidden); viewport.Focus(); RestartChromeTimer(); }
    private void ToggleFullscreen() { fullscreen.Toggle(); manuallyHidden = false; SetChrome(true); viewport.Focus(); RestartChromeTimer(); }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (deleting) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Tab: e.Handled = true; if (!e.IsRepeat) ToggleChrome(); break;
            case Key.Delete: e.Handled = true; if (!e.IsRepeat) await DeleteCurrent(); break;
            case Key.Escape: e.Handled = true; if (fullscreen.IsFullscreen) ToggleFullscreen(); else Close(); break;
            case Key.Right: Navigate(1); e.Handled = true; break;
            case Key.Left: Navigate(-1); e.Handled = true; break;
            case Key.D0: case Key.NumPad0: viewport.Fit(); e.Handled = true; break;
            case Key.D1: case Key.NumPad1: viewport.ActualPixels(); e.Handled = true; break;
            case Key.Add: case Key.OemPlus: viewport.ZoomBy(1.25); e.Handled = true; break;
            case Key.Subtract: case Key.OemMinus: viewport.ZoomBy(1 / 1.25); e.Handled = true; break;
            case Key.F: if (!e.IsRepeat) ToggleFavorite(); e.Handled = true; break;
            case Key.F11: if (!e.IsRepeat) ToggleFullscreen(); e.Handled = true; break;
            case Key.Z when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                e.Handled = true; if (!e.IsRepeat) await UndoDelete(); break;
        }
    }

    private bool ReconcileOrder()
    {
        var live = library.Data.Posts.SelectMany(p => p.Images).ToDictionary(p => p.Id);
        string? currentId = index >= 0 && index < displayOrder.Count ? displayOrder[index].Id : null;
        displayOrder.RemoveAll(p => !live.ContainsKey(p.Id));
        for (int i = 0; i < displayOrder.Count; i++) displayOrder[i] = live[displayOrder[i].Id];
        int retainedIndex = currentId == null ? -1 : displayOrder.FindIndex(p => p.Id == currentId);
        index = retainedIndex >= 0 ? retainedIndex : Math.Clamp(index, 0, Math.Max(0, displayOrder.Count - 1));
        return displayOrder.Count > 0;
    }

    private void Navigate(int delta)
    {
        if (deleting || closed) return;
        if (!ReconcileOrder()) { Close(); return; }
        index = (index + delta + displayOrder.Count) % displayOrder.Count;
        Load();
    }

    private void ToggleFavorite()
    {
        if (deleting || !ReconcileOrder()) return;
        var photo = CurrentPhoto;
        try { library.SetFavorite([photo.Id], !photo.IsFavorite); UpdateCaption(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "收藏未保存"); }
    }

    private async Task DeleteCurrent()
    {
        if (deleting || closed || !ReconcileOrder()) return;
        deleting = true;
        var photo = CurrentPhoto;
        int deletedIndex = index;
        var owner = post;
        viewport.Clear();
        await decodeGate.WaitAsync();
        try
        {
            await Task.Run(() => library.DeletePhoto(owner, photo));
            lastDeleted = (photo.Id, deletedIndex);
            undo.IsEnabled = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "删除未完成"); }
        finally { decodeGate.Release(); deleting = false; }
        wheelRemainder = 0;
        if (!ReconcileOrder()) { Close(); return; }
        Load();
    }

    private async Task UndoDelete()
    {
        if (deleting || closed || lastDeleted is not { } deleted) return;
        deleting = true;
        await decodeGate.WaitAsync();
        try
        {
            var photo = await Task.Run(() => library.RestoreDeletedPhoto(deleted.Id));
            if (!library.Data.Posts.Any(p => p.Images.Any(item => item.Id == photo.Id)))
                throw new InvalidOperationException("图片已恢复到原图组，请关闭查看器后查看。");
            displayOrder.RemoveAll(p => p.Id == photo.Id);
            index = Math.Clamp(deleted.Index, 0, displayOrder.Count);
            displayOrder.Insert(index, photo);
            lastDeleted = null;
            undo.IsEnabled = false;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "撤销删除"); }
        finally { decodeGate.Release(); deleting = false; }
        if (ReconcileOrder()) Load(); else Close();
    }

    private void UpdateCaption()
    {
        caption.Text = $"{index + 1} / {displayOrder.Count}    {CurrentPhoto.OriginalName}    ·    {post.DisplayTitle}    ·    {scope}";
        caption.ToolTip = CurrentPhoto.OriginalName;
        favorite.Content = CurrentPhoto.IsFavorite ? "★ 已收藏" : "☆ 收藏";
    }

    private async void Load()
    {
        if (closed) return;
        if (!ReconcileOrder()) { Close(); return; }
        UpdateCaption();
        await viewport.LoadAsync(library.Original(CurrentPhoto));
    }
}
