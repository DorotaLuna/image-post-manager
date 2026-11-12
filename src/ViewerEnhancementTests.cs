using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImagePostManager;

internal static class ViewerEnhancementTests
{
    internal static void Run(string root, Action<bool, string> assert)
    {
        string folder = Path.Combine(root, "viewer-pixels");
        Directory.CreateDirectory(folder);
        string large = Path.Combine(folder, "large.png");
        string tall = Path.Combine(folder, "tall.png");
        string small = Path.Combine(folder, "small.png");
        WriteImage(large, 3600, 1800);
        WriteImage(tall, 9, 4000);
        WriteImage(small, 32, 24);
        var preview = PhotoBitmapLoader.Read(large, 800);
        assert(preview.Bitmap.PixelWidth == 800 && preview.OriginalWidth == 3600 && preview.OriginalHeight == 1800,
            "viewer preview is bounded while preserving original dimensions");
        assert(PhotoBitmapLoader.Read(tall, 640).Bitmap.PixelHeight == 640, "portrait preview bounds the longer dimension");
        assert(PhotoBitmapLoader.Read(small, 2048).Bitmap.PixelWidth == 32, "viewer never enlarges a small image during decode");
        var original = PhotoBitmapLoader.Read(large, 0);
        assert(original.Bitmap.PixelWidth == 3600 && original.Bitmap.PixelHeight == 1800 && original.Bitmap.IsFrozen,
            "original decode exceeds old 2400 pixel ceiling and freezes for UI transfer");
        using (var exclusive = new FileStream(large, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            assert(exclusive.Length > 0, "decoded viewer images release the original file handle");
        assert(Math.Abs(PhotoViewport.FitScale(4000, 2000, 800, 600, 1.5, 1.5) - .3) < .00001,
            "fit scale uses physical display pixels at 150 percent Windows scaling");
        assert(PhotoViewport.FitScale(32, 24, 800, 600, 2, 2) == 1, "fit does not upscale small originals");

        // Keep the pumping dispatcher separate from the application's later startup dispatcher.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { RunViewportChecks(large, small, assert); RunWindowChecks(folder, large, assert); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Exception("Viewer viewport regression failed.", failure);
    }

    private static void RunViewportChecks(string large, string small, Action<bool, string> assert)
    {
            var gate = new SemaphoreSlim(1);
            using var viewport = new PhotoViewport(gate);
            viewport.Measure(new Size(800, 600));
            viewport.Arrange(new Rect(0, 0, 800, 600));
            viewport.UpdateLayout();
            Await(viewport.LoadAsync(large));
            assert(viewport.HasImage && viewport.CaptureState().IsFit && viewport.Zoom < 1, "viewport initially fits a large image");
            viewport.ActualPixels();
            WaitUntil(() => viewport.Picture.Source is BitmapSource source && source.PixelWidth == 3600);
            var dpi = VisualTreeHelper.GetDpi(viewport);
            assert(viewport.Zoom == 1 && Math.Abs(viewport.Picture.Width * dpi.DpiScaleX - 3600) < .00001,
                "100 percent requests original data and maps each source pixel to one screen pixel");
            viewport.ApplyState(new PhotoViewState(2, .65, .6, false));
            var state = viewport.CaptureState();
            assert(Math.Abs(state.CenterX - .65) < .00001 && Math.Abs(state.CenterY - .6) < .00001,
                "comparison view state preserves zoom and relative pan position");
            viewport.ZoomWheel(120, new Point(400, 300));
            assert(Math.Abs(viewport.Zoom - 2.4) < .00001, "control wheel zoom changes magnification");
            viewport.Fit();
            assert(viewport.CaptureState().IsFit && viewport.CaptureState().CenterX == .5, "fit resets zoom and centers the picture");

            // Both requests queue before decoding starts, exercising fast navigation cancellation.
            gate.Wait();
            var obsolete = viewport.LoadAsync(large);
            var latest = viewport.LoadAsync(small);
            gate.Release();
            Await(Task.WhenAll(obsolete, latest));
            assert(viewport.Picture.Source is BitmapSource current && current.PixelWidth == 32,
                "rapid viewer navigation suppresses obsolete decode results");
            gate.Wait();
            var closing = viewport.LoadAsync(large);
            viewport.Dispose();
            gate.Release();
            Await(closing);
            assert(viewport.Picture.Source == null, "closing during queued decode does not repopulate the picture");
    }

    private static void WriteImage(string path, int width, int height)
    {
        // Non-96 image DPI catches accidental reliance on BitmapSource.Width instead of pixel dimensions.
        var source = BitmapSource.Create(width, height, 192, 192, PixelFormats.Gray8, null, new byte[width * height], width);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void RunWindowChecks(string root, string largeImage, Action<bool, string> assert)
    {
        using var library = new Library(Path.Combine(root, "window-library"));
        library.RemoveOriginal = File.Delete;
        var post = library.AddPost("查看器测试");
        var first = new Photo { OriginalName = "first.png" };
        var second = new Photo { OriginalName = "second.png" };
        var hidden = new Photo { OriginalName = "filtered-out.png" };
        post.Images.AddRange([first, hidden, second]);
        post.Cover = second.Id;
        foreach (var photo in post.Images) WriteImage(library.Original(photo), 64, 48);
        library.Save();
        var viewer = new Viewer(library, post, 0, displayOrder: [second, first]);
        try
        {
            LayoutAndLoad(viewer);
            WaitUntil(() => Descendants<Image>(viewer).Single().Source != null);
            bool Caption(string prefix, string name) => Descendants<TextBlock>(viewer).Any(t => t.Text.StartsWith(prefix) && t.Text.Contains($"    {name}    "));
            assert(Caption("1 / 2", "second.png"), "filtered viewer opens the requested display index");
            Click(viewer, "❯");
            assert(Caption("2 / 2", "first.png"), "filtered viewer navigation does not reveal excluded photos");
            Click(viewer, "❯");
            assert(Caption("1 / 2", "second.png"), "filtered viewer wraps in its own display order");
            Click(viewer, "☆ 收藏");
            assert(second.IsFavorite, "viewer favorite button updates the selected photo");
            Click(viewer, "删除");
            WaitUntil(() => !post.Images.Contains(second) && Caption("1 / 1", "first.png"));
            assert(post.Images.Contains(hidden) && Descendants<Button>(viewer).Single(b => b.Content as string == "撤销删除").IsEnabled,
                "viewer deletion preserves filtered position and offers undo");
            Click(viewer, "撤销删除");
            WaitUntil(() => post.Images.Contains(second) && Caption("1 / 2", "second.png"));
            assert(second.IsFavorite && File.Exists(library.Original(second)), "viewer undo restores original selected photo and favorite state");
        }
        finally { viewer.Close(); }

        var other = library.AddPost("另一图组");
        var cross = new Photo { OriginalName = "cross.png" };
        other.Images.Add(cross); WriteImage(library.Original(cross), 64, 48); library.Save();
        var results = new Viewer(library, post, 0, displayOrder: [first, cross], scope: "搜索结果");
        try
        {
            LayoutAndLoad(results);
            Click(results, "❯");
            assert(Descendants<TextBlock>(results).Any(t => t.Text.Contains("cross.png") && t.Text.Contains("另一图组") && t.Text.Contains("搜索结果")), "result viewer navigates across groups with scope and actual owner");
            Click(results, "删除");
            WaitUntil(() => !other.Images.Contains(cross) && Descendants<TextBlock>(results).Any(t => t.Text.StartsWith("1 / 1")));
            assert(post.Images.Contains(first), "cross-group delete changes only the actual owner");
            Click(results, "撤销删除");
            WaitUntil(() => other.Images.Contains(cross) && Descendants<TextBlock>(results).Any(t => t.Text.StartsWith("2 / 2")));
            assert(other.Images.Contains(cross), "cross-group undo restores result position and owner");
            Click(results, "隐藏工具栏");
            assert(Descendants<Button>(results).Single(b => b.Content as string == "❯").Visibility == Visibility.Collapsed, "immersive mode hides navigation chrome");
            typeof(Viewer).GetMethod("ToggleChrome", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(results, null);
            assert(Descendants<Button>(results).Single(b => b.Content as string == "❯").Visibility == Visibility.Visible, "immersive mode restores navigation chrome");
            Click(results, "全屏");
            WaitUntil(() => Descendants<Button>(results).Single(b => b.Content as string == "❯").Visibility == Visibility.Collapsed);
            Click(results, "全屏");
            assert(Descendants<Button>(results).Single(b => b.Content as string == "❯").Visibility == Visibility.Visible, "leaving fullscreen restores tools after idle hiding");
        }
        finally { results.Close(); }

        var left = new Photo { OriginalName = "compare-left.png" };
        var right = new Photo { OriginalName = "compare-right.png" };
        post.Images.AddRange([left, right]);
        File.Copy(largeImage, library.Original(left));
        File.Copy(largeImage, library.Original(right));
        library.Save();
        var compare = new CompareViewer(library, left, right);
        try
        {
            LayoutAndLoad(compare);
            var panes = Descendants<PhotoViewport>(compare).ToList();
            WaitUntil(() => panes.Count == 2 && panes.All(p => p.HasImage));
            assert(panes.All(p => p.CaptureState().IsFit), "comparison starts with both originals fitted");
            Click(compare, "两侧 100%");
            WaitUntil(() => panes.All(p => p.Picture.Source is BitmapSource image && image.PixelWidth == 3600));
            assert(panes.All(p => p.Zoom == 1), "comparison loads both full resolution originals at 100 percent");
            Descendants<CheckBox>(compare).Single().IsChecked = true;
            panes[0].ApplyState(new PhotoViewState(2, .6, .6, false));
            var synchronized = panes[1].CaptureState();
            assert(Math.Abs(synchronized.Zoom - 2) < .00001 && Math.Abs(synchronized.CenterX - .6) < .00001,
                "comparison synchronization copies zoom and relative pan between panes");
            Descendants<CheckBox>(compare).Single().IsChecked = false;
            panes[0].ZoomBy(1.25);
            assert(Math.Abs(panes[1].Zoom - 2) < .00001, "comparison panes zoom independently when synchronization is disabled");
        }
        finally { compare.Close(); }
    }

    private static void LayoutAndLoad(Window window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1200, 700));
        root.Arrange(new Rect(0, 0, 1200, 700));
        root.UpdateLayout();
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
    }

    private static void Click(Window window, string text) => Descendants<Button>(window).Single(b => b.Content as string == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is Window window && window.Content is DependencyObject content)
        {
            if (content is T typedContent) yield return typedContent;
            foreach (var nested in Descendants<T>(content)) yield return nested;
            yield break;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        long deadline = Environment.TickCount64 + 10000;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || Environment.TickCount64 >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new TimeoutException("Viewer original decode did not complete.");
    }
}
