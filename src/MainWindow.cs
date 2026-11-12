using System.Collections;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ImagePostManager;

public sealed partial class MainWindow : Window
{
    private Library library = null!;
    private readonly Preferences preferences;
    private bool rootView;
    private bool systemLight = SystemTheme.IsLight();
    private bool IsLightTheme => SystemTheme.Resolve(preferences.Theme, systemLight);
    private bool rebuildingTree;
    private readonly Dictionary<string, Catalog> catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly TreeView navigation = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(6, 6, 6, 7) };
    private readonly ComboBox themePicker = new() { ItemsSource = new[] { "深色", "浅色", "系统" }, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8) };
    private static readonly SolidColorBrush TextBrush = ThemeBrushes.Create(Color.FromRgb(234, 237, 243));
    private static readonly SolidColorBrush SidebarBrush = ThemeBrushes.Create(Color.FromRgb(23, 26, 32));
    private static readonly SolidColorBrush LineBrush = ThemeBrushes.Create(Color.FromRgb(54, 60, 72));
    private Post? current;
    private readonly HashSet<string> selected = [];
    private readonly MasonryPanel cards = new() { Margin = new Thickness(15, 8, 15, 4) };
    private readonly WrapPanel actions = new() { Orientation = Orientation.Horizontal };
    private readonly Slider sizeSlider = new()
    {
        Minimum = Preferences.MinimumCardWidth, Maximum = Preferences.MaximumCardWidth,
        Width = 150, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true,
        SmallChange = 8, LargeChange = 40, TickFrequency = 8, IsSnapToTickEnabled = true,
        ToolTip = "调整图像区中每张图片的最大显示尺寸；窗口变化时列数会自动增减。"
    };
    private readonly TextBlock sizeValue = new() { Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinWidth = 30, TextAlignment = TextAlignment.Right, Margin = new Thickness(10, 0, 2, 0) };
    private readonly DispatcherTimer settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly TextBlock status = new() { Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel paging = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ScrollViewer scroller;
    private readonly DockPanel shell = new();
    private int page, generation;
    private const int PageSize = 40;
    private bool busy;
    private bool selectMode;
    private readonly Canvas dragOverlay = new() { IsHitTestVisible = false };
    private Border? pendingDragCard;
    private Point pendingDragStart;
    private Point pendingDragOffset;
    private bool suppressNextActivation;
    private DragState? dragState;
    private static readonly SolidColorBrush BackgroundBrush = ThemeBrushes.Create(Color.FromRgb(17, 19, 24));
    private static readonly SolidColorBrush CardBrush = ThemeBrushes.Create(Color.FromRgb(28, 31, 39));
    private static readonly SolidColorBrush Accent = ThemeBrushes.Create(Color.FromRgb(141, 170, 242));
    private static readonly SolidColorBrush HoverBrush = ThemeBrushes.Create(Color.FromRgb(49, 49, 55));
    private static readonly SolidColorBrush SelectedBrush = ThemeBrushes.Create(Color.FromRgb(58, 61, 70));
    private static readonly SolidColorBrush SurfaceBrush = ThemeBrushes.Create(Color.FromRgb(25, 25, 28));
    private static readonly SolidColorBrush ScrollBrush = ThemeBrushes.Create(Color.FromRgb(67, 67, 74));
    private static readonly SolidColorBrush Dim = ThemeBrushes.Create(Color.FromRgb(150, 158, 175));
    public MainWindow(Library? lib = null, Preferences? settings = null)
    {
        library = lib!;
        preferences = settings ?? new Preferences();
        if (lib != null) { preferences.Add(lib.Root); catalogs[lib.Root] = lib.Data; }
        rootView = lib == null;
        Title = "Image Post Manager"; Width = 1280; Height = 860; MinWidth = 900; MinHeight = 600;
        Background = BackgroundBrush; Foreground = TextBrush; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        UseLayoutRounding = true; SourceInitialized += (_, _) => WindowAppearance.Apply(this, IsLightTheme);
        Resources["Card"] = CardBrush; Resources["Text"] = TextBrush; Resources["Line"] = LineBrush;
        Resources["Hover"] = HoverBrush; Resources["Selected"] = SelectedBrush; Resources["Muted"] = Dim;
        Resources["Accent"] = Accent; Resources["SubtleAccent"] = SelectedBrush; Resources["ScrollThumb"] = ScrollBrush;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ImagePostManager;component/ModernStyles.xaml", UriKind.Relative) });
        // Scope the compact demo styles to the main content, not viewer/dialog resources.
        shell.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ImagePostManager;component/MainWindowStyles.xaml", UriKind.Relative) });
        navigation.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ImagePostManager;component/MainWindowStyles.xaml", UriKind.Relative) });
        var header = new DockPanel { Margin = new Thickness(15, 11, 15, 0) };
        var sizePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 7, 0, 8) };
        sizePanel.Children.Add(new TextBlock { Text = "图像尺寸", Foreground = Dim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        sizePanel.Children.Add(sizeSlider); sizePanel.Children.Add(sizeValue);
        DockPanel.SetDock(sizePanel, Dock.Right); header.Children.Add(sizePanel);
        header.Children.Add(actions);
        DockPanel.SetDock(header, Dock.Top); shell.Children.Add(header);
        InitializeBrowseTools();
        var footer = new DockPanel { Margin = new Thickness(15, 12, 15, 16) };
        cancelOperation.Content = "停止导入"; cancelOperation.Visibility = Visibility.Collapsed;
        cancelOperation.Click += (_, _) => { cancelImport = true; cancelOperation.IsEnabled = false; status.Text = "完成当前文件后停止…"; };
        DockPanel.SetDock(cancelOperation, Dock.Left); footer.Children.Add(cancelOperation);
        DockPanel.SetDock(undoDeletion, Dock.Right); footer.Children.Add(undoDeletion);
        undoDeletion.Click += async (_, _) => await UndoLastDelete();
        DockPanel.SetDock(paging, Dock.Right); footer.Children.Add(paging); footer.Children.Add(status); DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, FocusVisualStyle = null, Content = cards };
        shell.Children.Add(scroller);
        cards.AnchorShift += offset =>
        {
            int token = generation;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            { if (token == generation && pendingScrollOffset == null && cards.IsVirtual) scroller.ScrollToVerticalOffset(offset); }));
        };
        scroller.ScrollChanged += (_, _) => { cards.SetViewport(scroller.VerticalOffset, scroller.ViewportHeight); };
        dragScrollTimer.Tick += (_, _) =>
        {
            if (dragState == null || !cards.IsVirtual || !ReferenceEquals(Mouse.Captured, this)) return;
            var pointer = Mouse.GetPosition(scroller);
            double step = pointer.Y < 60 ? -24 : pointer.Y > scroller.ActualHeight - 60 ? 24 : 0;
            if (step != 0) { scroller.ScrollToVerticalOffset(scroller.VerticalOffset + step); UpdateCardDrag(Mouse.GetPosition(cards)); }
        };
        var sidebar = new DockPanel { Width = 206, Background = Brushes.Transparent };
        var themePanel = new StackPanel { Margin = new Thickness(10, 16, 10, 8) };
        themePanel.Children.Add(new TextBlock { Text = "画面主题", Foreground = Dim, FontSize = 12 });
        themePanel.Children.Add(themePicker); DockPanel.SetDock(themePanel, Dock.Bottom); sidebar.Children.Add(themePanel);
        sidebar.Children.Add(navigation);
        var layout = new DockPanel();
        var sideBorder = new Border { Child = sidebar, Background = SidebarBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(4) };
        DockPanel.SetDock(sideBorder, Dock.Left); layout.Children.Add(sideBorder); layout.Children.Add(new Border { Child = shell, Background = SurfaceBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 4, 4, 4) });
        var root = new Grid();
        root.Children.Add(layout); root.Children.Add(dragOverlay); Content = root;
        themePicker.SelectedIndex = preferences.Theme switch { "light" => 1, "system" => 2, _ => 0 };
        sizeSlider.Value = preferences.CardWidth; cards.TileWidth = sizeSlider.Value; sizeValue.Text = $"{sizeSlider.Value:0}";
        sizeSlider.ValueChanged += (_, e) =>
        {
            cards.TileWidth = e.NewValue; preferences.CardWidth = e.NewValue; sizeValue.Text = $"{e.NewValue:0}";
            settingsSaveTimer.Stop(); settingsSaveTimer.Start();
        };
        settingsSaveTimer.Tick += (_, _) => { settingsSaveTimer.Stop(); preferences.Save(); };
        ApplyTheme();
        themePicker.SelectionChanged += (_, _) => { preferences.Theme = themePicker.SelectedIndex switch { 1 => "light", 2 => "system", _ => "dark" }; if (preferences.Theme == "system") systemLight = SystemTheme.IsLight(); preferences.Save(); ApplyTheme(); };
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = !busy && !rootView && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { if (!busy && !rootView && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Import(paths); };
        PreviewMouseMove += OnCardDragMouseMove;
        PreviewMouseLeftButtonUp += OnCardDragMouseUp;
        LostMouseCapture += (_, _) => CancelCardDrag();
        Closing += async (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            if (editFinished != null) { var completion = editFinished.Task; await completion; Close(); }
            else MessageBox.Show(this, "正在保存图片，请等操作完成后再关闭。", "正在处理");
        };
        Closed += (_, _) => { dragScrollTimer.Stop(); settingsSaveTimer.Stop(); try { preferences.Save(); } catch { } generation++; library?.Dispose(); };
        Loaded += async (_, _) =>
        {
            if (library != null)
            {
                SetBusy(true); status.Text = "正在恢复未完成的导入…";
                try { var errors = await Task.Run(library.Recover); if (errors.Count > 0) ShowErrors(errors); }
                finally { SetBusy(false); }
            }
            Render();
        };
        KeyDown += (_, e) =>
        {
            if (!busy && e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !rootView) { searchBox.Focus(); searchBox.SelectAll(); e.Handled = true; return; }
            if (!busy && e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !rootView && !searchBox.IsKeyboardFocusWithin) { _ = UndoLastDelete(); e.Handled = true; return; }
            if (!busy && e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && selectMode && !searchBox.IsKeyboardFocusWithin) { SelectAllResults(); e.Handled = true; return; }
            if (e.Key != Key.Escape) return;
            if (busy) { e.Handled = true; return; }
            if (dragState != null) { CancelCardDrag(); e.Handled = true; return; }
            if (selectMode) { selectMode = false; selected.Clear(); Render(); e.Handled = true; return; }
            if (!busy) GoBack();
        };
    }
    private void SetBusy(bool value) { busy = value; actions.IsEnabled = !value; browseTools.IsEnabled = !value; paging.IsEnabled = !value; undoDeletion.IsEnabled = !value; scroller.IsEnabled = !value; navigation.IsEnabled = !value; }
    private void ApplyTheme()
    {
        bool light = IsLightTheme;
        ThemeBrushes.Set(BackgroundBrush, WindowAppearance.BackgroundColor(light));
        ThemeBrushes.Set(CardBrush, light ? Color.FromArgb(245, 255, 255, 255) : Color.FromArgb(245, 36, 36, 40));
        ThemeBrushes.Set(SidebarBrush, light ? Color.FromArgb(248, 245, 245, 247) : Color.FromArgb(248, 34, 34, 38));
        ThemeBrushes.Set(TextBrush, light ? Color.FromRgb(39, 40, 45) : Color.FromRgb(233, 233, 237));
        ThemeBrushes.Set(Dim, light ? Color.FromRgb(113, 114, 125) : Color.FromRgb(151, 151, 163));
        ThemeBrushes.Set(LineBrush, light ? Color.FromArgb(190, 219, 220, 226) : Color.FromArgb(180, 59, 59, 66));
        ThemeBrushes.Set(HoverBrush, light ? Color.FromRgb(237, 238, 244) : Color.FromRgb(48, 48, 55));
        ThemeBrushes.Set(SelectedBrush, light ? Color.FromRgb(226, 230, 242) : Color.FromRgb(56, 58, 68));
        ThemeBrushes.Set(SurfaceBrush, light ? Color.FromRgb(250, 250, 252) : Color.FromRgb(25, 25, 28));
        ThemeBrushes.Set(ScrollBrush, light ? Color.FromRgb(205, 205, 214) : Color.FromRgb(65, 65, 73));
        ThemeBrushes.Set(Accent, light ? Color.FromRgb(84, 115, 199) : Color.FromRgb(141, 170, 242));
        WindowAppearance.Apply(this, light);
        navigation.Foreground = TextBrush;
        navigation.Resources[SystemColors.HighlightBrushKey] = LineBrush;
        navigation.Resources[SystemColors.HighlightTextBrushKey] = TextBrush;
        navigation.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = LineBrush;
        navigation.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = TextBrush;
    }
    private static string LibraryName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
    private void GoRoot() { RememberLocation(); rootView = true; current = null; RestoreLocation(); }
    private void GoLibrary() { RememberLocation(); rootView = false; current = null; RestoreLocation(); }
    /// <summary>Up one level: post -> library -> virtual root. The virtual root is the top level.</summary>
    private void GoBack()
    {
        if (rootView) return;
        if (current != null) GoLibrary(); else GoRoot();
    }
    private void ResetLocation() { page = 0; selected.Clear(); selectMode = false; scroller.ScrollToTop(); Render(); }
    private Catalog? ReadCatalog(string path)
    {
        if (library != null && string.Equals(path, library.Root, StringComparison.OrdinalIgnoreCase)) return library.Data;
        try
        {
            var data = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(path, "library.json")));
            if (data?.Version != 1) return null;
            catalogs[path] = data; return data;
        }
        catch { return null; }
    }
    private async Task OpenLibrary(string path, string? postId = null)
    {
        if (busy) return;
        RememberLocation();
        Library? next = null;
        try
        {
            if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "library.json"))) throw new IOException("图库路径已不可用，请确认磁盘已连接，或重新添加图库。");
            if (library == null || !string.Equals(path, library.Root, StringComparison.OrdinalIgnoreCase))
            {
                SetBusy(true); generation++;
                next = new Library(path);
                var errors = await Task.Run(next.Recover);
                library?.Dispose(); library = next; next = null;
                if (errors.Count > 0) ShowErrors(errors);
            }
            rootView = false; current = postId == null ? null : library.Data.Posts.FirstOrDefault(p => p.Id == postId);
            RestoreLocation();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开图库"); }
        finally { next?.Dispose(); SetBusy(false); }
    }
    private TreeViewItem? navigationRoot;
    private readonly Dictionary<string, TreeViewItem> libraryNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> navigationStarts = new(StringComparer.OrdinalIgnoreCase);
    private const int NavigationWindowSize = 60;
    private TreeViewItem NavigationNode(string text, Action action)
    {
        var node = new TreeViewItem { Header = text, Foreground = TextBrush, Padding = new Thickness(5, 8, 4, 8) };
        node.Selected += (_, e) => { if (!busy && !rebuildingTree && ReferenceEquals(e.OriginalSource, node)) action(); };
        return node;
    }
    private void RenderNavigation()
    {
        rebuildingTree = true;
        try
        {
            if (navigationRoot == null)
            {
                navigationRoot = NavigationNode("库", GoRoot); navigationRoot.IsExpanded = true;
                navigation.Items.Add(navigationRoot);
            }
            navigationRoot.IsSelected = rootView;
            foreach (var path in libraryNodes.Keys.Where(path => !preferences.Libraries.Contains(path)).ToArray())
            { navigationRoot.Items.Remove(libraryNodes[path]); libraryNodes.Remove(path); }
            var rootMenu = new ContextMenu();
            var roots = new MenuItem { Header = "在资源管理器中打开", IsEnabled = preferences.Libraries.Count > 0 };
            foreach (var path in preferences.Libraries) roots.Items.Add(ExplorerMenuItem(path, null, LibraryName(path)));
            rootMenu.Items.Add(roots); navigationRoot.ContextMenu = rootMenu;
            foreach (var path in preferences.Libraries)
            {
                bool active = !rootView && library != null && string.Equals(path, library.Root, StringComparison.OrdinalIgnoreCase);
                if (!libraryNodes.TryGetValue(path, out var node))
                {
                    node = NavigationNode(LibraryName(path), async () => await OpenLibrary(path));
                    node.ToolTip = path; node.ContextMenu = ExplorerMenu(path);
                    libraryNodes.Add(path, node); navigationRoot.Items.Add(node);
                    node.Items.Add(new TreeViewItem { Header = "…" });
                    node.Expanded += (_, e) => { if (!rebuildingTree && ReferenceEquals(e.OriginalSource, node)) { RenderNavigation(); } };
                }
                node.IsSelected = active && current == null;
                if (active && current != null)
                {
                    int index = library!.Data.Posts.Count - 1 - library.Data.Posts.IndexOf(current);
                    navigationStarts[path] = Math.Max(0, index / NavigationWindowSize * NavigationWindowSize);
                }
                if (active || node.IsExpanded) PopulateNavigation(path, node, active);
                if (active) node.IsExpanded = true;
            }
        }
        finally { rebuildingTree = false; }
    }
    private void PopulateNavigation(string path, TreeViewItem node, bool active)
    {
        var data = ReadCatalog(path); if (data == null) return;
        int start = Math.Clamp(navigationStarts.GetValueOrDefault(path), 0, Math.Max(0, data.Posts.Count - 1));
        var posts = data.Posts.AsEnumerable().Reverse().Skip(start).Take(NavigationWindowSize).ToList();
        var existing = node.Items.OfType<TreeViewItem>().Where(n => n.Tag is Post).ToDictionary(n => ((Post)n.Tag).Id);
        var desired = new List<TreeViewItem>();
        TreeViewItem More(string text, int offset) => NavigationNode(text, () =>
        { navigationStarts[path] = Math.Max(0, offset); rebuildingTree = true; try { PopulateNavigation(path, node, active); } finally { rebuildingTree = false; } });
        if (start > 0) desired.Add(More("↑ 前面的图组", start - NavigationWindowSize));
        foreach (var post in posts)
        {
            if (!existing.TryGetValue(post.Id, out var child) || !ReferenceEquals(child.Tag, post))
            { child = NavigationNode(post.DisplayTitle, async () => await OpenLibrary(path, post.Id)); child.Tag = post; child.MinHeight = 30; child.Margin = new Thickness(0, 2, 0, 2); child.ContextMenu = ExplorerMenu(path, post); }
            child.Header = post.DisplayTitle; child.IsSelected = active && current?.Id == post.Id; desired.Add(child);
        }
        if (start + posts.Count < data.Posts.Count) desired.Add(More($"↓ 后面的图组（共 {data.Posts.Count} 组）", start + NavigationWindowSize));
        // Remove departed IDs first: deleting the first row must not detach/reinsert every following row.
        foreach (var old in node.Items.OfType<TreeViewItem>().Where(item => !desired.Contains(item)).ToArray()) node.Items.Remove(old);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < node.Items.Count && ReferenceEquals(node.Items[i], desired[i])) continue;
            node.Items.Remove(desired[i]); node.Items.Insert(i, desired[i]);
        }
        while (node.Items.Count > desired.Count) node.Items.RemoveAt(node.Items.Count - 1);
    }
    private MenuItem ExplorerMenuItem(string path, Post? post = null, string title = "在资源管理器中打开")
    {
        var item = new MenuItem { Header = title, ToolTip = post == null ? path : "打开原图目录并定位封面" };
        item.Click += (_, e) =>
        {
            e.Handled = true;
            try { ExplorerNavigation.Open(path, post); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开路径", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        return item;
    }
    private ContextMenu ExplorerMenu(string path, Post? post = null)
    {
        var menu = new ContextMenu(); menu.Items.Add(ExplorerMenuItem(path, post));
        var unlink = new MenuItem { Header = post == null ? "移除管理器链接" : "移至已移除图组", ToolTip = post == null ? "保留图库，可重新添加" : "保留图片及分组，可在恢复中心还原" };
        unlink.Click += (_, e) => { e.Handled = true; UnlinkNavigation(path, post); };
        menu.Items.Add(unlink); return menu;
    }
    private void UnlinkNavigation(string path, Post? post)
    {
        if (busy) return;
        try
        {
            bool active = library != null && string.Equals(library.Root, path, StringComparison.OrdinalIgnoreCase);
            if (post == null)
            {
                preferences.Remove(path); catalogs.Remove(path);
                if (active) { generation++; library!.Dispose(); library = null!; current = null; rootView = true; }
                ResetLocation();
            }
            else
            {
                if (active)
                {
                    var existing = library!.Data.Posts.FirstOrDefault(p => p.Id == post.Id);
                    if (existing != null) library.UnlinkPost(existing);
                    if (current?.Id == post.Id) { current = null; selected.Clear(); }
                }
                else
                {
                    if (!File.Exists(Path.Combine(path, "library.json"))) throw new IOException("图库路径不可用，未改动文件。");
                    using var other = new Library(path);
                    var existing = other.Data.Posts.FirstOrDefault(p => p.Id == post.Id);
                    if (existing != null) other.UnlinkPost(existing);
                }
                catalogs.Remove(path); Render();
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法移除链接"); }
    }
    private ContextMenu PhotoMenu(Post owner, Photo photo, bool libraryCover)
    {
        var menu = new ContextMenu();
        var cover = new MenuItem { Header = "设为封面" };
        cover.Click += (_, e) =>
        {
            e.Handled = true; if (busy) return;
            var previous = libraryCover ? library.Data.Cover : owner.Cover;
            SaveEdit(() => { if (libraryCover) library.Data.Cover = photo.Id; else owner.Cover = photo.Id; },
                () => { if (libraryCover) library.Data.Cover = previous; else owner.Cover = previous; });
            Render();
        };
        var move = new MenuItem { Header = "移动到" };
        move.SubmenuOpened += (_, _) =>
        {
            move.Items.Clear();
            var searchTarget = new MenuItem { Header = "搜索图组…" };
            searchTarget.Click += (_, e) => { e.Handled = true; if (busy) return; var target = ChoosePost("移动到图组", true, owner); if (target != null) { library.Transfer(owner, target, new[] { photo.Id }); selected.Remove(photo.Id); Render(); } };
            move.Items.Add(searchTarget);
            foreach (var target in library.Data.Posts.Where(p => p != owner))
            {
                var item = new MenuItem { Header = target.DisplayTitle };
                item.Click += (_, e) => { e.Handled = true; if (busy) return; library.Transfer(owner, target, new[] { photo.Id }); selected.Remove(photo.Id); Render(); };
                move.Items.Add(item);
            }
            var create = new MenuItem { Header = "＋ 新建图组…" };
            create.Click += (_, e) =>
            {
                e.Handled = true; if (busy) return;
                var title = AskText("新建图组", "标题（可留空）", "");
                if (title == null) return;
                var target = library.AddPost(title);
                library.Transfer(owner, target, new[] { photo.Id }); selected.Remove(photo.Id); Render();
            };
            move.Items.Add(create);
        };
        move.Items.Add(new MenuItem { Header = "…" });
        var delete = new MenuItem { Header = "删除该图", ToolTip = "将原图送入回收站" };
        delete.Click += async (_, e) => { e.Handled = true; await DeletePhoto(owner, photo); };
        var copy = new MenuItem { Header = "复制 ComfyUI 工作流", ToolTip = "从原始 PNG 提取完整工作流 JSON 并复制到剪贴板" };
        copy.Click += async (_, e) =>
        {
            e.Handled = true; if (busy) return;
            var path = library.Original(photo);
            SetBusy(true);
            try
            {
                var json = await Task.Run(() => ComfyWorkflow.Read(path));
                Clipboard.SetDataObject(json, true);
                status.Text = "已复制 ComfyUI 工作流 JSON";
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法复制工作流", MessageBoxButton.OK, MessageBoxImage.Information); }
            finally { SetBusy(false); }
        };
        menu.Items.Add(cover); menu.Items.Add(move); menu.Items.Add(delete); menu.Items.Add(copy);
        AddMenuAction(menu, "复制图片文件", () => CopyPhotoFiles([photo]));
        AddMenuAction(menu, "导出原图…", async () => await ExportPhotos([photo]));
        var favorite = new MenuItem { Header = photo.IsFavorite ? "取消收藏" : "收藏" };
        favorite.Click += async (_, e) => { e.Handled = true; if (!busy) await ChangeFavorites([photo], !photo.IsFavorite); };
        menu.Opened += (_, _) => favorite.Header = photo.IsFavorite ? "取消收藏" : "收藏";
        menu.Items.Add(favorite);
        return menu;
    }
    private async Task DeletePhoto(Post owner, Photo photo)
    {
        if (busy) return;
        SetBusy(true); generation++;
        try { await Task.Run(() => library.DeletePhoto(owner, photo)); selected.Remove(photo.Id); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "删除未完成"); }
        finally { SetBusy(false); Render(); }
        if (library.Data.DeletedPhotos.Any(item => item.Photo.Id == photo.Id)) status.Text = "已删除 · Ctrl+Z 可恢复，或打开恢复中心";
    }
    private Button Btn(string text, Action action)
    {
        var b = new Button { Content = text }; b.Click += (_, _) => { if (!busy) action(); }; return b;
    }
    private void ShowErrors(List<string> errors) => ShowDetails("部分操作未完成", string.Join("\n\n", errors) + "\n\n未完成的文件操作会在下次打开图库时重试。");
    private async Task SwitchLibrary()
    {
        var picker = new OpenFolderDialog { Title = "添加现有图库或选择空文件夹新建图库" };
        if (picker.ShowDialog(this) != true) return;
        var path = Path.GetFullPath(picker.FolderName);
        if (!File.Exists(Path.Combine(path, "library.json")))
        {
            if (Directory.EnumerateFileSystemEntries(path).Any()) { MessageBox.Show(this, "新图库请选择空文件夹；已有图库请选择包含 library.json 的文件夹。", "添加图库"); return; }
            try { using var created = new Library(path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "创建图库失败"); return; }
        }
        preferences.Add(path);
        await OpenLibrary(path);
    }
    private void NewPost()
    {
        var title = AskText("新建图组", "标题（可留空）", "");
        if (title == null) return;
        RememberLocation(); current = library.AddPost(title); RestoreLocation();
    }
    private async Task PickImages()
    {
        var picker = new OpenFileDialog { Title = "选择要移动进图库的图片", Multiselect = true, Filter = "PNG / JPG 图片|*.png;*.jpg;*.jpeg" };
        if (picker.ShowDialog(this) == true) await Import(picker.FileNames);
    }
    private async Task Import(string[] paths)
    {
        if (busy) return;
        var files = paths.Where(p => File.Exists(p) && new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(p).ToLowerInvariant())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) { MessageBox.Show(this, "请选择 PNG 或 JPG 文件。暂不支持拖入整个文件夹。", "添加图片"); return; }
        if (current == null)
        {
            var chosen = ChoosePost("将图片加入哪个图组？", true);
            if (chosen == null) return;
            RememberLocation(); current = chosen; RestoreLocation();
        }
        SetBusy(true); generation++; selected.Clear();
        cancelImport = false; cancelOperation.IsEnabled = true; cancelOperation.Visibility = Visibility.Visible;
        List<string> errors = []; List<string> skippedFiles = []; int success = 0, processed = 0;
        DuplicateDecision? duplicatePolicy = null;
        try
        {
            foreach (var file in files)
            {
                if (cancelImport) break;
                status.Text = $"正在检查 / 移动 {processed + 1} / {files.Length} · {Path.GetFileName(file)}";
                try
                {
                    var duplicate = await Task.Run(() => library.FindDuplicate(file));
                    if (cancelImport) break;
                    if (duplicate != null)
                    {
                        var decision = duplicatePolicy ?? AskDuplicate(file, duplicate.Value.Post);
                        if (decision == DuplicateDecision.Stop) { cancelImport = true; break; }
                        if (decision is DuplicateDecision.SkipAll or DuplicateDecision.ImportAll) duplicatePolicy = decision;
                        if (decision is DuplicateDecision.Skip or DuplicateDecision.SkipAll) { skippedFiles.Add(file); processed++; continue; }
                    }
                    await Task.Run(() => library.Import(current, file)); success++;
                }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(file)}：{ex.Message}"); }
                processed++;
            }
        }
        finally { cancelOperation.Visibility = Visibility.Collapsed; SetBusy(false); Render(); }
        status.Text = $"已移动 {success} 张 · 跳过 {skippedFiles.Count} 张 · 失败 {errors.Count} 张" + (cancelImport ? $" · 已停止，剩余 {files.Length - processed} 张未处理" : "");
        lastImportReport = status.Text + "\n\n跳过的重复文件（来源保留）：\n" + string.Join("\n", skippedFiles) + "\n\n失败详情：\n" + string.Join("\n\n", errors);
        if (errors.Count > 0) ShowDetails("导入结果", lastImportReport);
    }
    private void Render()
    {
        if (dragState != null) CancelCardDrag();
        var token = ++generation;
        pendingScrollOffset = null; pendingThumbnails = 0;
        if (!(cards.IsVirtual && !rootView && !ShowingPhotos && library.Data.Posts.Count > 0)) cards.SetVirtual(null);
        actions.Children.Clear(); paging.Children.Clear();
        selectionRefresh.Clear(); UpdateBrowseTools();
        RenderNavigation();
        if (rootView) { RenderLibraries(token); return; }
        status.Text = "";
        status.ToolTip = null;
        if (selectMode) UpdateSelectionStatus();
        actions.Children.Add(Btn("← 返回", GoBack));
        if (current == null && !selectMode)
        {
            actions.Children.Add(Btn("＋ 新建图组", NewPost));
            actions.Children.Add(Btn("移动图片到图库…", async () => await PickImages()));
        }
        else if (current != null && !selectMode)
        {
            actions.Children.Add(Btn("＋ 移动图片到此组…", async () => await PickImages()));
            if (current.Images.Count == 0) actions.Children.Add(Btn("删除空图组", () => { library.UnlinkPost(current); GoLibrary(); }));
        }
        if (ShowingPhotos)
        {
            actions.Children.Add(Btn(selectMode ? "完成选择" : "选择", () => { selectMode = !selectMode; selected.Clear(); selectionAnchor = null; Render(); }));
            if (selectMode)
            {
                actions.Children.Add(Btn("移动到图组…", MoveSelected));
                actions.Children.Add(Btn("双图对比", CompareSelected));
                actions.Children.Add(Btn("收藏", () => FavoriteSelected(true)));
                actions.Children.Add(Btn("导出…", async () => await ExportPhotos(SelectedPhotos())));
                actions.Children.Add(Btn("删除…", async () => await DeleteSelected()));
                actions.Children.Add(Btn("批量操作 ▾", ShowBatchMenu));
            }
        }
        if (selectMode) UpdateSelectionStatus();
        var photos = ShowingPhotos ? VisiblePhotos() : [];
        int count = ShowingPhotos ? photos.Count : library.Data.Posts.Count;
        int pages = ShowingPhotos ? Math.Max(1, (count + PageSize - 1) / PageSize) : 1;
        page = Math.Clamp(page, 0, pages - 1);
        if (pages > 1)
        {
            var previous = Btn("← 上一页", () => { page--; scroller.ScrollToTop(); Render(); }); previous.IsEnabled = page > 0; paging.Children.Add(previous);
            paging.Children.Add(new TextBlock { Text = $"{page + 1} / {pages}", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 16, 0) });
            var next = Btn("下一页 →", () => { page++; scroller.ScrollToTop(); Render(); }); next.IsEnabled = page < pages - 1; paging.Children.Add(next);
            paging.Children.Add(Btn("跳转…", () => { var value = AskText("跳转到页", $"页码（1～{pages}）", (page + 1).ToString()); if (int.TryParse(value, out var target) && target >= 1 && target <= pages) { page = target - 1; scroller.ScrollToTop(); Render(); } }));
        }
        if (count == 0)
        {
            cards.Children.Add(new TextBlock { Text = HasFilter ? "没有匹配的图片。\n\n试试其他关键词，或清除筛选。" : current == null ? "从一组作品开始。\n\n新建图组，或者将图片拖到这里。" : "把属于这一组的图片拖进来。\n\n添加后，原文件会移动到专用图库。", FontSize = 19, Foreground = Dim, Margin = new Thickness(8, 60, 0, 80), LineHeight = 30 });
            return;
        }
        if (!ShowingPhotos) ConfigurePostCards();
        else
            foreach (var entry in photos.Skip(page * PageSize).Take(PageSize))
            {
                var photo = entry.Photo;
                var tile = MakeCard(photo, photo.OriginalName, photo.Source != null ? "导入待恢复" : photo.Id == entry.Post.Cover ? "● 封面" : "", token, () => OpenPhoto(photo), photoOwner: entry.Post, order: current != null && !HasFilter ? current.Images : null, orderItem: photo);
                cards.Children.Add(tile);
            }
        SizeCards();
    }
    private void SizeCards() => cards.InvalidateMeasure();
    private void RenderLibraries(int token)
    {
        status.Text = ""; status.ToolTip = null;
        actions.Children.Add(Btn("＋ 添加图库", async () => await SwitchLibrary()));
        foreach (var path in preferences.Libraries)
        {
            var data = ReadCatalog(path);
            var first = data?.Posts.FirstOrDefault(p => p.Images.Count > 0);
            var cover = data?.Posts.SelectMany(p => p.Images).FirstOrDefault(p => p.Id == data.Cover) ?? first?.Images.FirstOrDefault(p => p.Id == first.Cover) ?? first?.Images.FirstOrDefault();
            cards.Children.Add(MakeCard(cover, LibraryName(path), data == null ? "路径不可用 / 索引无法读取" : $"{data.Posts.Count} 个图组 · {data.Posts.Sum(p => p.Images.Count)} 张图片", token, async () => await OpenLibrary(path), null, path));
        }
        if (preferences.Libraries.Count == 0) cards.Children.Add(new TextBlock { Text = "尚未添加图库。选择已有图库，或用空文件夹建立新图库。", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 32, 0, 32) });
    }
    private Border MakeCard(Photo? photo, string title, string info, int token, Action click, int? count = null, string? sourceRoot = null, Post? photoOwner = null, IList? order = null, object? orderItem = null)
    {
        var stack = new StackPanel();
        var picture = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
        // Natural bitmap dimensions determine the height; no fixed crop or fill.
        var surface = new AspectSurface { Ratio = PhotoRatio(photo) };
        surface.Children.Add(picture);
        if (photo == null) surface.Children.Add(new TextBlock { Text = "暂无封面", Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 60, 0, 60) });
        if (count.HasValue)
        {
            var badge = new Border { Background = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(9), Padding = new Thickness(7, 4, 7, 4), IsHitTestVisible = false };
            badge.Child = new TextBlock { Text = "▧  " + count.Value, Foreground = new SolidColorBrush(Color.FromRgb(37, 43, 53)), FontSize = 13, FontWeight = FontWeights.SemiBold };
            surface.Children.Add(badge);
        }
        if (photo != null && !count.HasValue)
        {
            var star = new TextBlock { Text = "★", FontSize = 22, Foreground = Brushes.Gold, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12), IsHitTestVisible = false };
            star.Visibility = photo.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
            surface.Children.Add(star);
            surface.Tag = (photo, star);
        }
        var frame = new Border { Child = surface, Cursor = Cursors.Hand, Background = SidebarBrush };
        stack.Children.Add(frame);
        var card = new Border { Background = CardBrush, BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Child = stack, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null };
        card.ToolTip = title;
        if (order != null && orderItem != null) card.Tag = new CardDragTarget(order, orderItem);
        if (photo != null && photoOwner != null) card.ContextMenu = PhotoMenu(photoOwner, photo, current == null);
        stack.SizeChanged += (_, _) => stack.Clip = new RectangleGeometry(new Rect(0, 0, stack.ActualWidth, stack.ActualHeight), 12, 12);
        bool selectable = selectMode && ShowingPhotos && photo != null && count == null && sourceRoot == null;
        Border? selectionMarker = null;
        TextBlock? selectionGlyph = null;
        if (selectable)
        {
            selectionGlyph = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            selectionMarker = new Border { Width = 29, Height = 29, CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(11), IsHitTestVisible = false, Child = selectionGlyph };
            surface.Children.Add(selectionMarker);
        }
        void RefreshSelection()
        {
            bool chosen = selectable && selected.Contains(photo!.Id);
            card.BorderBrush = chosen ? Accent : LineBrush;
            if (selectionMarker != null && selectionGlyph != null)
            {
                selectionMarker.Background = chosen ? Accent : new SolidColorBrush(Color.FromArgb(170, 30, 30, 35));
                selectionGlyph.Text = chosen ? "✓" : "○";
                selectionGlyph.Foreground = chosen ? new SolidColorBrush(Color.FromRgb(20, 25, 40)) : Brushes.White;
            }
            System.Windows.Automation.AutomationProperties.SetName(card, title + (selectable ? chosen ? "，已选择" : "，未选择" : ""));
        }
        void Activate()
        {
            if (busy) return;
            if (selectable)
            {
                ToggleSelection(photo!, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            }
            else click();
        }
        card.PreviewMouseLeftButtonDown += (_, e) => PrepareCardDrag(card, e);
        card.PreviewMouseMove += (_, e) => StartPreparedCardDrag(card, e);
        card.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (suppressNextActivation) { suppressNextActivation = false; e.Handled = true; return; }
            Activate(); e.Handled = true;
        };
        card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Activate(); e.Handled = true; } };
        var lift = new TranslateTransform(); card.RenderTransform = lift;
        card.MouseEnter += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, TimeSpan.FromMilliseconds(140)));
            card.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 5, Opacity = .18 };
        };
        card.MouseLeave += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
            card.Effect = null;
        };
        RefreshSelection();
        if (selectable) selectionRefresh[photo!.Id] = RefreshSelection;
        if (photo != null) LoadThumbnail(photo, picture, token, sourceRoot);
        return card;
    }
    private readonly ThumbnailCache thumbnailCache = new();
    private readonly Dictionary<string, double> photoRatios = [];
    private static Photo? CoverPhoto(Post post) => post.Images.FirstOrDefault(p => p.Id == post.Cover) ?? post.Images.FirstOrDefault();
    private double PhotoRatio(Photo? p) => p == null ? 1 : photoRatios.GetValueOrDefault(p.Id, double.IsFinite(p.AspectRatio) && p.AspectRatio > 0 ? p.AspectRatio : 1);
    private async void LoadThumbnail(Photo p, Image target, int token, string? sourceRoot = null)
    {
        pendingThumbnails++;
        var owner = library; var path = sourceRoot;
        string key = Path.Combine(path ?? owner.Root, p.Id);
        try
        {
            var bitmap = await thumbnailCache.Load(key, () =>
            {
                if (path == null) { owner.EnsureThumbnail(p); return Library.ReadBitmap(owner.Thumb(p), 480); }
                if (!Guid.TryParseExact(p.Id, "N", out _) || p.Extension is not (".png" or ".jpg" or ".jpeg")) throw new InvalidDataException("无效图片路径");
                var thumb = Path.Combine(path, "thumbnails", p.Id + ".jpg");
                return Library.ReadBitmap(File.Exists(thumb) ? thumb : Path.Combine(path, "originals", p.Id + p.Extension), 480);
            });
            if (token != generation) return;
            target.Source = bitmap;
            double aspect = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            photoRatios[p.Id] = aspect;
            // Dimensions are learned lazily and join the next ordinary index save.
            if (!busy) p.AspectRatio = aspect;
            if (target.Parent is AspectSurface surface && Math.Abs(surface.Ratio - aspect) > .001)
            { surface.Ratio = aspect; surface.InvalidateMeasure(); QueueThumbnailLayout(); }
        }
        catch (Exception ex) { if (token == generation) target.ToolTip = "图片无法加载：" + ex.Message; }
        finally { if (token == generation) { pendingThumbnails--; if (pendingThumbnails == 0) QueueThumbnailLayout(); } }
    }
    private bool thumbnailLayoutQueued;
    private void QueueThumbnailLayout()
    {
        if (thumbnailLayoutQueued) return;
        thumbnailLayoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        { thumbnailLayoutQueued = false; cards.InvalidateMeasure(); RestoreScrollOffset(); }));
    }

    private void PrepareCardDrag(Border card, MouseButtonEventArgs e)
    {
        if (busy || selectMode || HasFilter || dragState != null || card.Tag is not CardDragTarget) return;
        pendingDragCard = card;
        pendingDragStart = e.GetPosition(cards);
        pendingDragOffset = e.GetPosition(card);
        suppressNextActivation = false;
    }

    private void StartPreparedCardDrag(Border card, MouseEventArgs e)
    {
        if (pendingDragCard != card || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(cards);
        double dx = pointer.X - pendingDragStart.X, dy = pointer.Y - pendingDragStart.Y;
        if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance) return;
        if (!BeginCardDrag(card, pendingDragOffset, pointer)) return;
        pendingDragCard = null;
        Mouse.Capture(this);
        suppressNextActivation = true;
    }

    private void OnCardDragMouseMove(object? sender, MouseEventArgs e)
    {
        if (dragState == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelCardDrag(); return; }
        UpdateCardDrag(e.GetPosition(cards));
        e.Handled = true;
    }

    private void OnCardDragMouseUp(object? sender, MouseButtonEventArgs e)
    {
        if (dragState != null)
        {
            suppressNextActivation = true;
            CommitCardDrag();
            e.Handled = true;
            return;
        }
        pendingDragCard = null;
    }

    internal bool IsCardDragActive => dragState != null;

    internal bool BeginCardDrag(Border source, Point pressOffset, Point pointerInCards)
    {
        if (busy || selectMode || HasFilter || dragState != null || source.Tag is not CardDragTarget target) return false;
        int sourceIndex = cards.IndexOf(source);
        int itemIndex = target.Items.IndexOf(target.Item);
        if (sourceIndex < 0 || itemIndex < 0 || source.ActualWidth <= 0 || source.ActualHeight <= 0) return false;

        int pixelWidth = (int)Math.Ceiling(source.ActualWidth), pixelHeight = (int)Math.Ceiling(source.ActualHeight);
        var snapshot = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        snapshot.Render(source); snapshot.Freeze();
        var ghost = new Image
        {
            Source = snapshot, Width = source.ActualWidth, Height = source.ActualHeight,
            Stretch = Stretch.Fill, Opacity = .88, IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 28, ShadowDepth = 8, Opacity = .3 }
        };
        var placeholder = MakeDropPlaceholder(source.ActualWidth, source.ActualHeight);
        source.Effect = null;

        bool virtualDrag = cards.IsVirtual;
        if (!virtualDrag) { cards.Children.RemoveAt(sourceIndex); cards.Children.Insert(sourceIndex, placeholder); }
        else source.Opacity = .3;
        dragOverlay.Children.Add(ghost);
        dragState = new DragState(
            source, ghost, placeholder, target.Items, target.Item,
            sourceIndex, page * PageSize + sourceIndex, page * PageSize, pressOffset, sourceIndex);
        dragState.Virtual = virtualDrag; dragScrollTimer.Start();
        PositionGhost(pointerInCards);
        return true;
    }

    internal void UpdateCardDrag(Point pointerInCards)
    {
        if (dragState == null) return;
        PositionGhost(pointerInCards);
        int target = ComputeDropTarget(pointerInCards);
        if (target == dragState.TargetDisplayIndex) return;
        if (dragState.Virtual)
        {
            dragState.TargetDisplayIndex = target;
            if (target < cards.Positions.Count)
            {
                var rect = cards.Positions[target];
                status.Text = $"移动到第 {target + 1} 个图组";
                ShowDropMarker(rect);
            }
            return;
        }

        cards.Children.Remove(dragState.Placeholder);
        cards.Children.Insert(Math.Clamp(target, 0, cards.Children.Count), dragState.Placeholder);
        dragState.TargetDisplayIndex = target;
    }

    internal async void CommitCardDrag()
    {
        var state = dragState;
        if (state == null) return;
        dragState = null; dragScrollTimer.Stop(); RemoveDropMarker();
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
        if (state.Virtual)
        {
            state.Source.Opacity = 1; dragOverlay.Children.Remove(state.Ghost);
            int destination = state.TargetDisplayIndex;
            if (destination == state.SourceDisplayIndex) return;
            var previous = state.Items.Cast<object>().ToArray();
            MoveInDisplayOrder(state.Items, state.Item, state.SourceDisplayIndex, destination);
            if (state.Item is Post moved) cards.IgnoreAnchorKey = PostCardKey(moved);
            ConfigurePostCards(); RenderNavigation();
            await PersistEdit(() => { state.Items.Clear(); foreach (var item in previous) state.Items.Add(item); ConfigurePostCards(); RenderNavigation(); });
            return;
        }
        int placeholderIndex = cards.Children.IndexOf(state.Placeholder);
        if (placeholderIndex < 0) { CancelVisualDrag(state); return; }
        int targetDisplayIndex = state.PageStart + placeholderIndex;

        if (targetDisplayIndex != state.SourceDisplayIndex)
        {
            var original = state.Items.Cast<object>().ToList();
            MoveInDisplayOrder(state.Items, state.Item, state.SourceDisplayIndex, targetDisplayIndex);
            cards.Children.Remove(state.Placeholder); cards.Children.Insert(placeholderIndex, state.Source);
            dragOverlay.Children.Remove(state.Ghost);
            await PersistEdit(() => { state.Items.Clear(); foreach (var item in original) state.Items.Add(item); cards.Children.Remove(state.Source); cards.Children.Insert(state.SourceChildIndex, state.Source); });
            return;
        }

        dragOverlay.Children.Remove(state.Ghost);
        cards.Children.Remove(state.Placeholder); cards.Children.Insert(placeholderIndex, state.Source);
    }

    internal void CancelCardDrag()
    {
        pendingDragCard = null;
        var state = dragState;
        if (state == null) return;
        dragState = null; dragScrollTimer.Stop(); RemoveDropMarker();
        if (ReferenceEquals(Mouse.Captured, this)) Mouse.Capture(null);
        CancelVisualDrag(state);
    }

    private void CancelVisualDrag(DragState state)
    {
        dragOverlay.Children.Remove(state.Ghost);
        state.Source.Opacity = 1;
        if (state.Virtual) return;
        int placeholderIndex = cards.Children.IndexOf(state.Placeholder);
        if (placeholderIndex >= 0) cards.Children.RemoveAt(placeholderIndex);
        cards.Children.Insert(Math.Clamp(state.SourceChildIndex, 0, cards.Children.Count), state.Source);
        state.Source.Effect = null;
    }

    private void PositionGhost(Point pointerInCards)
    {
        if (dragState == null) return;
        Point overlayPoint = cards.TranslatePoint(pointerInCards, dragOverlay);
        Canvas.SetLeft(dragState.Ghost, overlayPoint.X - dragState.PressOffset.X);
        Canvas.SetTop(dragState.Ghost, overlayPoint.Y - dragState.PressOffset.Y);
    }

    private int ComputeDropTarget(Point pointer)
    {
        if (dragState == null) return 0;
        if (cards.IsVirtual)
        {
            int gap = cards.DropIndex(pointer);
            return Math.Clamp(gap > dragState.SourceDisplayIndex ? gap - 1 : gap, 0, Math.Max(0, cards.Positions.Count - 1));
        }
        int bestTarget = 0, cardsBeforePlaceholder = 0;
        bool found = false;
        double bestDistance = double.PositiveInfinity;
        foreach (UIElement child in cards.Children)
        {
            if (ReferenceEquals(child, dragState.Placeholder)) continue;
            var rawOffset = VisualTreeHelper.GetOffset(child);
            Point offset = new(rawOffset.X, rawOffset.Y);
            Size size = child.RenderSize;
            var center = new Point(offset.X + size.Width / 2, offset.Y + size.Height / 2);
            double dx = pointer.X - center.X, dy = pointer.Y - center.Y;
            double distance = dx * dx + dy * dy;
            if (!found || distance < bestDistance)
            {
                found = true; bestDistance = distance;
                double horizontal = dx / Math.Max(1, size.Width / 2);
                double vertical = dy / Math.Max(1, size.Height / 2);
                bestTarget = cardsBeforePlaceholder + (horizontal + vertical < 0 ? 0 : 1);
            }
            cardsBeforePlaceholder++;
        }
        return Math.Clamp(bestTarget, 0, cardsBeforePlaceholder);
    }

    private static Grid MakeDropPlaceholder(double width, double height)
    {
        var slot = new Grid { Width = width, Height = height, Background = Brushes.Transparent, IsHitTestVisible = false };
        slot.Children.Add(new System.Windows.Shapes.Rectangle { Width = 4, Height = height, Fill = Accent, Opacity = .9, HorizontalAlignment = HorizontalAlignment.Left });
        return slot;
    }

    private static void MoveInDisplayOrder(IList items, object item, int sourceDisplayIndex, int targetDisplayIndex)
    {
        var display = items.Cast<object>().Reverse().ToList();
        display.RemoveAt(sourceDisplayIndex);
        display.Insert(targetDisplayIndex, item);
        items.Clear();
        for (int i = display.Count - 1; i >= 0; i--) items.Add(display[i]);
    }

    private sealed record CardDragTarget(IList Items, object Item);

    private sealed class DragState
    {
        public DragState(Border source, Image ghost, Grid placeholder, IList items, object item,
            int sourceChildIndex, int sourceDisplayIndex, int pageStart, Point pressOffset, int targetDisplayIndex)
        {
            Source = source; Ghost = ghost; Placeholder = placeholder; Items = items; Item = item;
            SourceChildIndex = sourceChildIndex; SourceDisplayIndex = sourceDisplayIndex; PageStart = pageStart;
            PressOffset = pressOffset; TargetDisplayIndex = targetDisplayIndex;
        }
        public bool Virtual { get; set; }
        public Border Source { get; }
        public Image Ghost { get; }
        public Grid Placeholder { get; }
        public IList Items { get; }
        public object Item { get; }
        public int SourceChildIndex { get; }
        public int SourceDisplayIndex { get; }
        public int PageStart { get; }
        public Point PressOffset { get; }
        public int TargetDisplayIndex { get; set; }
    }

    private void SetCover()
    {
        if (selected.Count != 1) { MessageBox.Show(this, "请只选择一张图片。", "设置封面"); return; }
        var previous = current!.Cover; SaveEdit(() => current.Cover = selected.First(), () => current.Cover = previous); Render();
    }
    private void Reorder(int delta)
    {
        if (selected.Count != 1) { MessageBox.Show(this, "请选择一张图片，然后前移或后移。", "调整顺序"); return; }
        var photo = current!.Images.FirstOrDefault(p => selected.Contains(p.Id));
        if (photo == null) return;
        int source = current.Images.Count - 1 - current.Images.IndexOf(photo);
        int target = Math.Clamp(source + delta, 0, current.Images.Count - 1);
        if (target == source) return;
        var previous = current.Images.ToList();
        if (SaveEdit(() => MoveInDisplayOrder(current.Images, photo, source, target), () => { current.Images.Clear(); current.Images.AddRange(previous); })) page = target / PageSize;
        Render();
    }
    private void MoveSelected()
    {
        if (selected.Count == 0) { MessageBox.Show(this, "请先选择图片。", "移动图片"); return; }
        var target = ChoosePost("移动到图组", true, current);
        if (target == null) return;
        var errors = new List<string>(); int moved = 0;
        foreach (var photo in target.Images) selected.Remove(photo.Id);
        foreach (var owner in library.Data.Posts.Where(p => p != target).ToList())
        {
            var ids = owner.Images.Where(p => selected.Contains(p.Id)).Select(p => p.Id).ToArray();
            if (ids.Length == 0) continue;
            try { library.Transfer(owner, target, ids); selected.ExceptWith(ids); moved += ids.Length; }
            catch (Exception ex) { errors.Add($"{owner.DisplayTitle}：{ex.Message}"); }
        }
        Render(); status.Text = $"已移动 {moved} 张" + (errors.Count > 0 ? $" · {selected.Count} 张未移动，选择已保留" : "");
        if (errors.Count > 0) ShowDetails("部分图片未移动", string.Join("\n\n", errors));
    }
    private void OpenPhoto(Photo p)
    {
        var owner = library.Data.Posts.FirstOrDefault(post => post.Images.Contains(p));
        if (owner == null) return;
        var order = VisiblePhotos().Select(item => item.Photo).ToList();
        new Viewer(library, owner, order.IndexOf(p), IsLightTheme, order, searchQuery.Length > 0 ? "搜索结果" : favoritesOnly ? "收藏" : current == null ? "全部图片" : "当前图组") { Owner = this }.ShowDialog();
        selected.RemoveWhere(id => !library.Data.Posts.SelectMany(post => post.Images).Any(photo => photo.Id == id)); Render();
    }
    private Window Dialog(string title, int width = 440) => new() { Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = BackgroundBrush, Foreground = TextBrush, FontFamily = FontFamily, ShowInTaskbar = false, Resources = Resources };
    private string? AskText(string title, string label, string initial)
    {
        var dialog = Dialog(title);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 12) });
        var input = new TextBox { Text = initial, FontSize = 16, Padding = new Thickness(8), MaxLength = 200 }; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var save = Btn("保存", () => dialog.DialogResult = true); save.IsDefault = true; buttons.Children.Add(save);
        var cancel = Btn("取消", () => dialog.DialogResult = false); cancel.IsCancel = true; buttons.Children.Add(cancel); panel.Children.Add(buttons);
        dialog.Content = panel; dialog.SourceInitialized += (_, _) => WindowAppearance.Apply(dialog, IsLightTheme);
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text : null;
    }
    private Post? ChoosePost(string title, bool allowNew, Post? exclude = null)
    {
        var dialog = Dialog(title); var panel = new StackPanel { Margin = new Thickness(24) }; Post? result = null;
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, Margin = new Thickness(0, 0, 0, 16) });
        var filter = new TextBox { Margin = new Thickness(0, 12, 0, 8), Padding = new Thickness(8), ToolTip = "搜索图组名称" };
        System.Windows.Automation.AutomationProperties.SetName(filter, "搜索目标图组"); panel.Children.Add(filter);
        var list = new ListBox { Height = 260, DisplayMemberPath = "DisplayTitle", ItemsSource = library.Data.Posts.Where(p => p != exclude).ToList(), FontSize = 16 };
        filter.TextChanged += (_, _) => list.ItemsSource = library.Data.Posts.Where(p => p != exclude && p.DisplayTitle.Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        panel.Children.Add(list);
        var choose = Btn("加入选中图组", () => { if (list.SelectedItem is Post post) { result = post; dialog.DialogResult = true; } }); choose.Margin = new Thickness(0, 16, 0, 8); panel.Children.Add(choose);
        if (allowNew) panel.Children.Add(Btn("＋ 新建图组…", () => { var name = AskText("新建图组", "标题（可留空）", ""); if (name != null) { result = library.AddPost(name); dialog.DialogResult = true; } }));
        dialog.Content = panel; dialog.SourceInitialized += (_, _) => WindowAppearance.Apply(dialog, IsLightTheme); dialog.ShowDialog(); return result;
    }
}
