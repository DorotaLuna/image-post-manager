using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImagePostManager;

internal static class PerformanceTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(MainWindow w, string name) => (T)typeof(MainWindow).GetField(name, Flags)!.GetValue(w)!;
    private static object? Call(MainWindow w, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Flags)!.Invoke(w, args);
    private static void Pump(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Performance regression did not settle");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }
    private static void Ready(MainWindow w)
    {
        bool yielded = false;
        Pump(() => { if (!yielded) { yielded = true; return false; } w.UpdateLayout(); return w.IsLoaded && !Field<bool>(w, "busy") && Field<int>(w, "pendingThumbnails") == 0 && !Field<bool>(w, "thumbnailLayoutQueued"); });
    }
    internal static void RunUi(string root, Action<bool, string> assert)
    {
        string folder = Path.Combine(root, "performance-regressions");
        using var library = new Library(folder);
        var bmp = BitmapSource.Create(480, 640, 96, 96, PixelFormats.Bgr32, null, new byte[480 * 640 * 4], 480 * 4);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = new MemoryStream(); encoder.Save(stream); var bytes = stream.ToArray();
        for (int i = 0; i < 1000; i++)
        {
            var post = new Post { Title = $"performance-{i}" };
            for (int j = 0; j < 10; j++) post.Images.Add(new Photo { OriginalName = $"{i}-{j}.jpg", Extension = ".jpg", AspectRatio = .75 });
            post.Cover = post.Images[0].Id; library.Data.Posts.Add(post);
            File.WriteAllBytes(library.Thumb(post.Images[0]), bytes);
        }
        library.Save();
        var app = Application.Current;
        var mode = app.ShutdownMode; app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var w = new MainWindow(library, new Preferences()) { ShowInTaskbar = false };
        try
        {
            w.Show(); Ready(w);
            var cards = Field<MasonryPanel>(w, "cards"); var scroll = Field<ScrollViewer>(w, "scroller");
            var cache = Field<ThumbnailCache>(w, "thumbnailCache");
            assert(cards.IsVirtual && cards.Children.Count < 50, "1000 groups realize only viewport cards");
            assert(Field<StackPanel>(w, "paging").Children.Count == 0, "continuous group list has no pagination");
            var nav = (TreeViewItem)((TreeViewItem)Field<TreeView>(w, "navigation").Items[0]).Items[0];
            assert(nav.Items.Count <= 62, "sidebar bounds group controls with navigable ranges");
            var isolatedCache = new ThumbnailCache();
            var mergedA = isolatedCache.Load("same", () => Library.ReadBitmap(library.Thumb(library.Data.Posts[0].Images[0]), 480));
            var mergedB = isolatedCache.Load("same", () => throw new Exception("duplicate decode"));
            Pump(() => mergedA.IsCompleted); mergedA.GetAwaiter().GetResult();
            assert(ReferenceEquals(mergedA, mergedB) && isolatedCache.Decodes == 1, "duplicate in-flight requests share one decode");
            var before = cards.Children.Cast<UIElement>().ToArray(); int decodes = cache.Decodes;
            var photo = library.Data.Posts[^1].Images[0];
            var favorite = (Task)Call(w, "ChangeFavorites", new[] { photo }, true)!; Pump(() => favorite.IsCompleted); favorite.GetAwaiter().GetResult(); Ready(w);
            assert(before.SequenceEqual(cards.Children.Cast<UIElement>()) && cache.Decodes == decodes && photo.IsFavorite, "favorite preserves every card and does not decode thumbnails");
            var unfavorite = (Task)Call(w, "ChangeFavorites", new[] { photo }, false)!; Pump(() => unfavorite.IsCompleted); Ready(w);
            assert(!photo.IsFavorite && before.SequenceEqual(cards.Children.Cast<UIElement>()), "unfavorite also preserves cards");
            var source = (Border)cards.Children[0]; var moved = library.Data.Posts[^1];
            assert(w.BeginCardDrag(source, new Point(5, 5), new Point(5, 5)), "virtual group begins global reorder");
            var target = cards.Positions[65]; w.UpdateCardDrag(new Point(target.Right - 1, target.Bottom - 1));
            w.CommitCardDrag(); Ready(w);
            var saved = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(folder, "library.json")))!;
            assert(library.Data.Posts.AsEnumerable().Reverse().ToList().IndexOf(moved) > 40 && saved.Posts.Select(p => p.Id).SequenceEqual(library.Data.Posts.Select(p => p.Id)), "cross former page boundary reorder commits globally and survives index reload");
            var original = library.Data.Posts.Select(p => p.Id).ToArray();
            source = cards.Children.OfType<Border>().First();
            w.BeginCardDrag(source, new Point(5, 5), new Point(5, 5));
            for (int i = 70; i < 100; i++) { var r = cards.Positions[i]; w.UpdateCardDrag(r.TopLeft); }
            w.CancelCardDrag(); Ready(w);
            assert(original.SequenceEqual(library.Data.Posts.Select(p => p.Id)) && cards.Children.Contains(source), "continuous drag cancellation preserves order and existing controls");
            scroll.ScrollToVerticalOffset(1400); Ready(w);
            double offset = scroll.VerticalOffset;
            var retained = cards.Children.Cast<UIElement>().ToArray(); decodes = cache.Decodes;
            Call(w, "EnterPost", library.Data.Posts[^1]); Ready(w); Call(w, "GoBack"); Ready(w);
            assert(Math.Abs(scroll.VerticalOffset - offset) < 1 && retained.Intersect(cards.Children.Cast<UIElement>()).Count() == retained.Length, $"return restores retained group cards and scroll offset ({scroll.VerticalOffset}/{offset}, {retained.Intersect(cards.Children.Cast<UIElement>()).Count()}/{retained.Length})");
            assert(cache.Decodes <= decodes + 9, "return does not decode retained group covers");
            w.Width = 940; Ready(w); w.Width = 1280; Ready(w);
            assert(cards.Children.Count < 50, "window resize retains bounded realization");
            for (int i = 0; i < 1000; i += 30)
            { scroll.ScrollToVerticalOffset(cards.Positions[i].Top); Ready(w); assert(cards.Children.Count < 50 && cache.Bytes <= ThumbnailCache.Capacity, "scroll realization and decoded cache stay bounded"); }
            assert(Field<System.Collections.IDictionary>(w, "postCards").Count <= 96, "retained group view cache stays bounded");
            // A failed async snapshot must restore both model and the rendered state.
            scroll.ScrollToTop(); Ready(w); photo = library.Data.Posts[^1].Images[0];
            var originalFavorite = photo.IsFavorite;
            Directory.CreateDirectory(Path.Combine(folder, "library.json.tmp"));
            var responder = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
            responder.Tick += (_, _) => { foreach (Window dialog in app.Windows.Cast<Window>().Where(d => d.Title == "修改未保存").ToArray()) dialog.Close(); };
            responder.Start();
            try { var failed = (Task)Call(w, "ChangeFavorites", new[] { photo }, !originalFavorite)!; Pump(() => failed.IsCompleted); failed.GetAwaiter().GetResult(); }
            finally { responder.Stop(); Directory.Delete(Path.Combine(folder, "library.json.tmp")); }
            assert(photo.IsFavorite == originalFavorite && !Field<bool>(w, "busy"), "failed background save rolls back favorite and unlocks editing");
            source = cards.Children.OfType<Border>().First(); original = library.Data.Posts.Select(p => p.Id).ToArray();
            Directory.CreateDirectory(Path.Combine(folder, "library.json.tmp")); responder.Start();
            try { w.BeginCardDrag(source, new Point(5, 5), new Point(5, 5)); w.UpdateCardDrag(cards.Positions[55].BottomRight); w.CommitCardDrag(); Ready(w); }
            finally { responder.Stop(); Directory.Delete(Path.Combine(folder, "library.json.tmp")); }
            assert(original.SequenceEqual(library.Data.Posts.Select(p => p.Id)), "failed global reorder save rolls back data and controls");
            var closeSave = (Task)Call(w, "ChangeFavorites", new[] { photo }, !photo.IsFavorite)!;
            w.Close(); Pump(() => !w.IsVisible && closeSave.IsCompleted);
            assert(closeSave.IsCompletedSuccessfully && File.ReadAllText(Path.Combine(folder, "library.json")).Contains("\"IsFavorite\": true"), "close waits for pending snapshot commit");
        }
        finally { if (w.IsVisible) w.Close(); app.ShutdownMode = mode; SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
}
