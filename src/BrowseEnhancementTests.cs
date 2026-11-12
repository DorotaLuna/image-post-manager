using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ImagePostManager;

internal static class BrowseEnhancementTests
{
    internal static void Run(string root, Action<bool, string> assert)
    {
        var first = new Photo { OriginalName = "Portrait.PNG", IsFavorite = true };
        var second = new Photo { OriginalName = "Sunset.jpg" };
        var third = new Photo { OriginalName = "portrait-detail.png", IsFavorite = true };
        var older = new Post { Title = "Blue STUDY", Images = [first, second] };
        var newer = new Post { Title = "金色时刻", Images = [third] };
        var unnamed = new Post { Title = "  ", Images = [new Photo { OriginalName = "untitled.png" }] };
        var archived = new Post { Title = "Blue archived", Images = [new Photo { OriginalName = "Portrait.png", IsFavorite = true }] };
        var catalog = new Catalog { Posts = [older, newer, unnamed], ArchivedPosts = [archived] };

        List<MainWindow.PhotoEntry> Query(string query = "", bool favorites = false, Post? post = null) => MainWindow.QueryPhotos(catalog, post, query, favorites);
        assert(Query().Select(item => item.Photo).SequenceEqual(new[] { unnamed.Images[0], third, second, first }), "browse query follows displayed post and image order");
        assert(Query("  pOrTrAiT  ").Select(item => item.Photo).SequenceEqual(new[] { third, first }), "filename search trims input and ignores case");
        assert(Query("blue study").Select(item => item.Photo).SequenceEqual(new[] { second, first }), "title search includes every image in a matching group");
        assert(Query("金色").Single().Post == newer, "browse query searches Unicode group titles and retains owning groups");
        assert(Query("未命名图组").Single().Post == unnamed, "browse query uses the displayed fallback group title");
        assert(Query("", true).Select(item => item.Photo).SequenceEqual(new[] { third, first }), "favorites query excludes ordinary and archived images");
        assert(Query("study", true).Single().Photo == first, "favorites and text search combine as an intersection");
        assert(Query("portrait", false, older).Single().Photo == first, "group search does not include matches from other groups");
        assert(Query("absent").Count == 0 && MainWindow.QueryPhotos(new Catalog(), null, "", false).Count == 0, "browse query handles empty results and empty catalogs");
        assert(catalog.Posts.SequenceEqual(new[] { older, newer, unnamed }) && older.Images.SequenceEqual(new[] { first, second }) && !second.IsFavorite, "search leaves stored ordering and favorite state untouched");
    }

    // Call after creating the self-test Application, before showing its main window.
    // Every window, settings file and image used here belongs to this temporary fixture.
    internal static void RunUi(string root, Action<bool, string> assert)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Browse UI checks require the existing self-test Application.");
        var previousMode = app.ShutdownMode;
        var previousMainWindow = app.MainWindow;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        MainWindow? window = null;
        var folder = Path.Combine(root, "browse-enhancements");
        Directory.CreateDirectory(folder);
        using var library = new Library(Path.Combine(folder, "library"));
        var primary = library.AddPost("Primary study");
        var secondary = library.AddPost("Secondary study");
        var bitmap = BitmapSource.Create(120, 180, 96, 96, PixelFormats.Bgr32, null, new byte[120 * 180 * 4], 120 * 4);
        byte[] original = Encode(bitmap, false), thumbnail = Encode(bitmap, true);
        for (int i = 0; i < 87; i++)
        {
            var photo = new Photo { OriginalName = i < 85 ? $"Browse-{i:000}.png" : $"Other-{i:000}.png", IsFavorite = i % 3 == 0 };
            var post = i < 85 ? primary : secondary;
            post.Images.Add(photo); post.Cover ??= photo.Id;
            File.WriteAllBytes(library.Original(photo), original);
            File.WriteAllBytes(library.Thumb(photo), thumbnail);
        }
        library.Save();
        var preferences = new Preferences { FilePath = Path.Combine(folder, "settings.json"), Theme = "dark", CardWidth = 200 };
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            window = new MainWindow(library, preferences)
            {
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000,
                Top = 0,
                Width = 1100,
                Height = 720
            };
            window.Show(); Ready(window);
            Invoke(window, "EnterPost", primary); Ready(window);
            var order = MainWindow.QueryPhotos(library.Data, primary, "", false).Select(item => item.Photo).ToList();
            var cards = Field<MasonryPanel>(window, "cards");
            var selected = Field<HashSet<string>>(window, "selected");
            var scroller = Field<ScrollViewer>(window, "scroller");
            var search = Field<TextBox>(window, "searchBox");
            var unfilteredCard = (Border)cards.Children[0];
            assert(unfilteredCard.Tag != null && window.BeginCardDrag(unfilteredCard, new Point(5, 5), new Point(5, 5)), "unfiltered post allows image reorder drag");
            window.CancelCardDrag();

            Click(window, "选择"); Ready(window);
            assert(!ActionButtons(window).Single(b => b.Content as string == "双图对比").IsEnabled, "compare unavailable before choosing two photos");
            Invoke(window, "ToggleSelection", order[1], false);
            Invoke(window, "ToggleSelection", order[0], false);
            assert(ActionButtons(window).Single(b => b.Content as string == "双图对比").IsEnabled, "compare available for exactly two photos");
            Invoke(window, "ToggleSelection", order[1], false);
            Click(window, "下一页 →"); Ready(window);
            assert(Field<int>(window, "page") == 1 && selected.SetEquals(new[] { order[0].Id }), "next page keeps previously selected images");
            assert(Field<TextBlock>(window, "status").Text.Contains("本页 0 张"), "selection feedback distinguishes off-page selection");
            Invoke(window, "ToggleSelection", order[43], true);
            assert(selected.SetEquals(order.Take(44).Select(photo => photo.Id)), "Shift selection spans the displayed order across page boundaries");
            assert(AutomationProperties.GetName((Border)cards.Children[3]).Contains("已选择"), "cross-page Shift selection updates visible selection markers");
            Click(window, "← 上一页"); Ready(window);
            assert(selected.Count == 44 && AutomationProperties.GetName((Border)cards.Children[0]).Contains("已选择"), "returning to a page restores selected card appearance");
            Invoke(window, "SelectAllResults");
            assert(selected.Count == 85 && ((List<Photo>)Invoke(window, "SelectedPhotos")!).SequenceEqual(order), "select all and batch actions include every page in displayed order");
            Invoke(window, "FavoriteSelected", true); Ready(window);
            var saved = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(library.Root, "library.json")))!;
            assert(primary.Images.All(photo => photo.IsFavorite) && saved.Posts.Single(post => post.Id == primary.Id).Images.All(photo => photo.IsFavorite) && selected.Count == 0, "batch favorite persists all selected pages and clears selection");

            selected.Add(order[0].Id);
            SetField(window, "selectionAnchor", order[0].Id);
            SetField(window, "page", 2);
            search.Text = "  bRoWsE  "; Invoke(window, "ApplyFilter"); Ready(window);
            assert(Field<int>(window, "page") == 0 && selected.Count == 0 && Field<string?>(window, "selectionAnchor") == null, "changing search resets page and selection anchor");
            assert(Field<string>(window, "searchQuery") == "bRoWsE" && cards.Children.Count == 40, "search applies trimmed text and paginates matching images");
            assert(!ActionButtons(window).Any(button => button.Content as string is "← 前移" or "后移 →"), "filtered selection view omits reorder commands");
            SetField(window, "selectMode", false); Invoke(window, "Render"); Ready(window);
            assert(cards.Children.Cast<Border>().All(card => card.Tag == null) && !window.BeginCardDrag((Border)cards.Children[0], new Point(5, 5), new Point(5, 5)), "search results cannot start reorder drag");
            search.Text = ""; Invoke(window, "ApplyFilter"); Ready(window);
            Field<Button>(window, "favoriteFilter").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Ready(window);
            assert(Field<bool>(window, "favoritesOnly") && cards.Children.Cast<Border>().All(card => card.Tag == null) && !window.BeginCardDrag((Border)cards.Children[0], new Point(5, 5), new Point(5, 5)), "favorites filter independently prevents reorder drag");

            search.Text = "bRoWsE"; Invoke(window, "ApplyFilter"); Ready(window);
            Click(window, "下一页 →"); Ready(window);
            double postOffset = Scroll(window, scroller, 650);
            Invoke(window, "GoLibrary"); Ready(window);
            assert(Field<Post?>(window, "current") == null && !Field<bool>(window, "allPhotos") && !Field<bool>(window, "favoritesOnly") && search.Text == "", "library navigation restores its own unfiltered group view");
            Field<Button>(window, "allPhotosToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Ready(window);
            assert(cards.Children.Count == 40 && !Field<bool>(window, "favoritesOnly") && cards.Children.Cast<Border>().All(card => card.Tag == null), "all photos view combines groups without offering image reorder drag");
            Click(window, "下一页 →"); Ready(window);
            double libraryOffset = Scroll(window, scroller, 330);
            Invoke(window, "GoRoot"); Ready(window);
            assert(Field<WrapPanel>(window, "browseTools").Visibility == Visibility.Collapsed, "virtual root hides library search controls");
            Invoke(window, "GoLibrary"); Ready(window);
            assert(Field<bool>(window, "allPhotos") && Field<int>(window, "page") == 1 && Math.Abs(scroller.VerticalOffset - libraryOffset) < 1, "library all-photos mode page and scroll survive a visit to virtual root");
            Invoke(window, "EnterPost", primary); Ready(window);
            assert(Field<int>(window, "page") == 1 && Field<bool>(window, "favoritesOnly") && search.Text == "bRoWsE", "return to group restores its independent page text and favorites filter");
            assert(Math.Abs(scroller.VerticalOffset - postOffset) < 1 && postOffset > 0, "group scroll restores after asynchronous thumbnails establish content height");
            assert(!Field<bool>(window, "selectMode") && selected.Count == 0, "location restore leaves selection mode and clears stale selection");
            search.Text = "missing-image"; Invoke(window, "ApplyFilter"); Ready(window);
            assert(cards.Children.OfType<TextBlock>().Single().Text.Contains("没有匹配的图片") && Field<int>(window, "page") == 0, "empty filtered result displays useful text and resets page");
            Click(window, "清除筛选"); Ready(window);
            assert(search.Text == "" && !Field<bool>(window, "favoritesOnly") && cards.Children.Count == 40, "clear filters restores the current group's images");

            var duplicatePaths = new[] { Path.Combine(folder, "duplicate-a.png"), Path.Combine(folder, "duplicate-b.png") };
            foreach (var path in duplicatePaths) File.WriteAllBytes(path, original);
            var duplicateDialogs = new HashSet<Window>();
            var responder = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(10) };
            responder.Tick += (_, _) =>
            {
                foreach (var dialog in app.Windows.Cast<Window>().Where(item => item.Title == "发现完全相同的图片").ToArray())
                {
                    duplicateDialogs.Add(dialog);
                    Descendants<Button>(dialog).Single(button => button.Content as string == "跳过所有重复").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            responder.Start();
            try { RunImport(window, duplicatePaths); }
            finally { responder.Stop(); }
            Ready(window);
            assert(duplicateDialogs.Count == 1 && duplicatePaths.All(File.Exists) && primary.Images.Count == 85, "skip-all duplicate decision prompts once and preserves every source file");
            assert(Field<string>(window, "lastImportReport").Contains("跳过 2 张") && duplicatePaths.All(path => Field<string>(window, "lastImportReport").Contains(path)), "duplicate report lists the skipped source files");

            RunImport(window, duplicatePaths, () => window.Dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => Field<Button>(window, "cancelOperation").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)))));
            Ready(window);
            assert(duplicatePaths.All(File.Exists) && primary.Images.Count == 85 && Field<string>(window, "lastImportReport").Contains("已停止，剩余 2 张未处理"), "stop-import action cancels before moving pending sources");
            assert(!Field<bool>(window, "busy") && Field<Button>(window, "cancelOperation").Visibility == Visibility.Collapsed, "cancelled import restores the browsing controls");
        }
        finally
        {
            if (window != null)
            {
                // A failing assertion must not leave a test window, timer or library lock alive.
                SetField(window, "busy", false);
                window.Close();
            }
            app.MainWindow = previousMainWindow;
            app.ShutdownMode = previousMode;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private static byte[] Encode(BitmapSource bitmap, bool jpeg)
    {
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    private static void RunImport(MainWindow window, string[] paths, Action? beforeStart = null)
    {
        Task? task = null;
        Exception? error = null;
        // Dispatcher execution supplies the WPF synchronization context for Import's awaits.
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { beforeStart?.Invoke(); task = (Task)Invoke(window, "Import", (object)paths)!; }
            catch (Exception ex) { error = ex; }
        }));
        PumpUntil(() => error != null || task?.IsCompleted == true, "import duplicate or cancellation workflow");
        if (error != null) throw error;
        task!.GetAwaiter().GetResult();
    }

    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(window)!;
    private static void SetField(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object[] arguments)
    {
        try { return typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
    }
    private static IEnumerable<Button> ActionButtons(MainWindow window) => Field<WrapPanel>(window, "actions").Children.OfType<Button>();
    private static void Click(MainWindow window, string label)
    {
        var buttons = ActionButtons(window)
            .Concat(Field<StackPanel>(window, "paging").Children.OfType<Button>())
            .Concat(Field<WrapPanel>(window, "browseTools").Children.OfType<Button>());
        var button = buttons.Single(item => item.Content as string == label);
        if (!button.IsEnabled) throw new InvalidOperationException("Disabled test button: " + label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Ready(MainWindow window) => PumpUntil(() =>
    {
        window.UpdateLayout();
        return window.IsLoaded && !Field<bool>(window, "busy") && Field<int>(window, "pendingThumbnails") == 0;
    }, "window layout and thumbnails");
    private static double Scroll(MainWindow window, ScrollViewer scroller, double offset)
    {
        scroller.ScrollToVerticalOffset(offset);
        PumpUntil(() => { window.UpdateLayout(); return Math.Abs(scroller.VerticalOffset - Math.Min(offset, scroller.ScrollableHeight)) < 1 && scroller.VerticalOffset > 0; }, "scrollable fixture images");
        return scroller.VerticalOffset;
    }
    private static void PumpUntil(Func<bool> condition, string label)
    {
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        Exception? error = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (!condition())
                {
                    if (watch.Elapsed < TimeSpan.FromSeconds(10)) return;
                    throw new TimeoutException("Browse UI test timed out waiting for " + label);
                }
            }
            catch (Exception ex) { error = ex; }
            timer.Stop(); frame.Continue = false;
        };
        timer.Start(); Dispatcher.PushFrame(frame);
        if (error != null) throw error;
    }
}
