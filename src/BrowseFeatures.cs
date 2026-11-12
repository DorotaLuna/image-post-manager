using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ImagePostManager;

public sealed partial class MainWindow
{
    private readonly Button undoDeletion = new() { Content = "撤销删除", ToolTip = "Ctrl+Z · 撤销最近一次删除（含整批）", Visibility = Visibility.Collapsed };
    private readonly WrapPanel browseTools = new() { Margin = new Thickness(15, 0, 15, 0) };
    private readonly TextBox searchBox = new() { Width = 240, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 0, 8, 6), ToolTip = "搜索当前图库的图组名称或原文件名（Ctrl+F）" };
    private readonly Button favoriteFilter = new(), allPhotosToggle = new(), cancelOperation = new();
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly Dictionary<string, BrowseLocation> locations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action> selectionRefresh = [];
    private string searchQuery = "", lastImportReport = "尚无导入记录。";
    private string? selectionAnchor;
    private double? pendingScrollOffset;
    private int pendingThumbnails;
    private bool favoritesOnly, allPhotos, restoringLocation, cancelImport;
    private sealed record BrowseLocation(int Page, double Offset, string Query, bool Favorites, bool AllPhotos);
    internal sealed record PhotoEntry(Post Post, Photo Photo);
    private bool HasFilter => searchQuery.Length > 0 || favoritesOnly;
    private bool ShowingPhotos => !rootView && (current != null || allPhotos || HasFilter);
    private string LocationKey => rootView ? "root" : library.Root + "|" + (current?.Id ?? "library");

    private void InitializeBrowseTools()
    {
        browseTools.Children.Add(new TextBlock { Text = "搜索", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 6) });
        System.Windows.Automation.AutomationProperties.SetName(searchBox, "搜索图组或原文件名");
        browseTools.Children.Add(searchBox);
        favoriteFilter.Click += (_, _) => { favoritesOnly = !favoritesOnly; ApplyFilter(); };
        allPhotosToggle.Click += (_, _) => { allPhotos = !allPhotos; ApplyFilter(); };
        browseTools.Children.Add(favoriteFilter); browseTools.Children.Add(allPhotosToggle);
        browseTools.Children.Add(Btn("清除筛选", () => { restoringLocation = true; searchBox.Text = ""; restoringLocation = false; searchQuery = ""; favoritesOnly = false; ApplyFilter(); }));
        browseTools.Children.Add(Btn("更多 ▾", ShowMoreMenu));
        DockPanel.SetDock(browseTools, Dock.Top); shell.Children.Add(browseTools);
        searchBox.TextChanged += (_, _) => { if (restoringLocation) return; searchTimer.Stop(); searchTimer.Start(); };
        searchTimer.Tick += (_, _) => { if (busy) return; searchTimer.Stop(); searchQuery = searchBox.Text.Trim(); ApplyFilter(); };
        Closed += (_, _) => searchTimer.Stop();
    }
    private void UpdateBrowseTools()
    {
        undoDeletion.Visibility = !rootView && library.Data.DeletedPhotos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        browseTools.Visibility = rootView ? Visibility.Collapsed : Visibility.Visible;
        favoriteFilter.Content = favoritesOnly ? "★ 仅看收藏" : "☆ 仅看收藏";
        allPhotosToggle.Content = allPhotos ? "返回图组视图" : "全部图片";
        allPhotosToggle.Visibility = current == null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ApplyFilter()
    {
        searchTimer.Stop(); searchQuery = searchBox.Text.Trim();
        page = 0; selected.Clear(); selectionAnchor = null; scroller.ScrollToTop(); Render();
    }
    internal static List<PhotoEntry> QueryPhotos(Catalog catalog, Post? post, string query, bool favorites)
    {
        var groups = post == null ? catalog.Posts.AsEnumerable().Reverse() : new[] { post };
        query = query.Trim();
        return groups.SelectMany(group => group.Images.AsEnumerable().Reverse().Where(photo =>
            (!favorites || photo.IsFavorite) && (query.Length == 0 || group.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) || photo.OriginalName.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Select(photo => new PhotoEntry(group, photo))).ToList();
    }
    private List<PhotoEntry> VisiblePhotos() => QueryPhotos(library.Data, current, searchQuery, favoritesOnly);
    private void RememberLocation()
    {
        searchTimer.Stop();
        if (!rootView && library == null) return;
        locations[LocationKey] = new(page, scroller.VerticalOffset, searchBox.Text.Trim(), favoritesOnly, allPhotos);
    }
    private void RestoreLocation()
    {
        searchTimer.Stop();
        var state = locations.GetValueOrDefault(LocationKey) ?? new BrowseLocation(0, 0, "", false, false);
        page = state.Page; searchQuery = state.Query; favoritesOnly = state.Favorites; allPhotos = state.AllPhotos;
        restoringLocation = true; searchBox.Text = searchQuery; restoringLocation = false;
        selected.Clear(); selectMode = false; selectionAnchor = null; Render();
        pendingScrollOffset = state.Offset;
        int token = generation;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (token == generation) RestoreScrollOffset(); }));
    }
    private void RestoreScrollOffset()
    {
        if (pendingScrollOffset is not double offset) return;
        scroller.ScrollToVerticalOffset(offset);
        if (pendingThumbnails == 0) pendingScrollOffset = null;
    }
    private void EnterPost(Post post) { RememberLocation(); current = post; RestoreLocation(); }
    private void ToggleSelection(Photo photo, bool extend)
    {
        var order = VisiblePhotos();
        int anchor = order.FindIndex(p => p.Photo.Id == selectionAnchor), target = order.FindIndex(p => p.Photo.Id == photo.Id);
        if (extend && anchor >= 0 && target >= 0)
            foreach (var item in order.Skip(Math.Min(anchor, target)).Take(Math.Abs(target - anchor) + 1)) selected.Add(item.Photo.Id);
        else { if (!selected.Add(photo.Id)) selected.Remove(photo.Id); selectionAnchor = photo.Id; }
        foreach (var refresh in selectionRefresh.Values) refresh();
        UpdateSelectionStatus();
    }
    private void UpdateSelectionStatus()
    {
        int onPage = VisiblePhotos().Skip(page * PageSize).Take(PageSize).Count(item => selected.Contains(item.Photo.Id));
        status.Text = $"已选择 {selected.Count} 张 · 本页 {onPage} 张 · Shift 连选 · Ctrl+A 全选结果";
        foreach (var button in actions.Children.OfType<Button>())
        {
            string label = button.Content as string ?? "";
            if (label == "双图对比") { button.IsEnabled = selected.Count == 2; button.ToolTip = "选择两张图片以对比（可跨页）"; }
            else if (label is "设为封面" or "← 前移" or "后移 →") button.IsEnabled = selected.Count == 1;
            else if (label is "移动到图组…" or "收藏" or "导出…" or "删除…") button.IsEnabled = selected.Count > 0;
            ToolTipService.SetShowOnDisabled(button, true);
        }
    }
    private void ShowMoreMenu()
    {
        var menu = new ContextMenu();
        if (current != null) AddMenuAction(menu, "修改标题", () => {
            var value = AskText("修改标题", "标题（可留空）", current.Title);
            if (value != null) { var old = current.Title; SaveEdit(() => current.Title = value, () => current.Title = old); Render(); }
        });
        AddMenuAction(menu, "导入结果…", () => ShowDetails("最近一次导入结果", lastImportReport));
        AddMenuAction(menu, "恢复中心…", ShowRecoveryCenter);
        AddMenuAction(menu, "撤销最近一次删除", async () => await UndoLastDelete());
        menu.PlacementTarget = browseTools; menu.IsOpen = true;
    }
    private void SelectAllResults() { foreach (var entry in VisiblePhotos()) selected.Add(entry.Photo.Id); foreach (var refresh in selectionRefresh.Values) refresh(); UpdateSelectionStatus(); }
    private List<Photo> SelectedPhotos() => VisiblePhotos().Where(entry => selected.Contains(entry.Photo.Id)).Select(entry => entry.Photo).ToList();
    private void AddMenuAction(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, e) => { e.Handled = true; if (!busy) action(); }; menu.Items.Add(item);
    }
    private bool SaveEdit(Action change, Action rollback)
    {
        change();
        try { library.Save(); return true; }
        catch (Exception ex) { rollback(); ShowDetails("修改未保存", ex.Message); return false; }
    }
    private void ShowBatchMenu()
    {
        var menu = new ContextMenu();
        if (current != null)
        {
            AddMenuAction(menu, "设为封面", SetCover);
            if (!HasFilter) { AddMenuAction(menu, "← 前移", () => Reorder(-1)); AddMenuAction(menu, "后移 →", () => Reorder(1)); }
            foreach (MenuItem item in menu.Items) { item.IsEnabled = selected.Count == 1; item.ToolTip = "请选择一张图片"; ToolTipService.SetShowOnDisabled(item, true); }
            menu.Items.Add(new Separator());
        }
        AddMenuAction(menu, "全选当前结果（跨页）", SelectAllResults);
        AddMenuAction(menu, "清空选择", () => { selected.Clear(); selectionAnchor = null; Render(); });
        AddMenuAction(menu, "收藏选中图片", () => FavoriteSelected(true));
        AddMenuAction(menu, "取消收藏选中图片", () => FavoriteSelected(false));
        AddMenuAction(menu, "复制选中图片文件", () => CopyPhotoFiles(SelectedPhotos()));
        AddMenuAction(menu, "导出选中原图…", async () => await ExportPhotos(SelectedPhotos()));
        AddMenuAction(menu, "删除选中图片…", async () => await DeleteSelected());
        actions.ContextMenu = menu; menu.PlacementTarget = actions; menu.IsOpen = true;
    }
    private void FavoriteSelected(bool value)
    {
        if (selected.Count == 0) { status.Text = "请先选择图片。"; return; }
        _ = FavoriteSelection(value);
    }
    private async Task FavoriteSelection(bool value)
    {
        await ChangeFavorites(SelectedPhotos(), value);
        selected.Clear(); foreach (var refresh in selectionRefresh.Values) refresh(); UpdateSelectionStatus();
    }
    private void CompareSelected()
    {
        var photos = SelectedPhotos();
        if (photos.Count != 2) { status.Text = "请选择两张图片进行对比，可以跨页选择。"; return; }
        new CompareViewer(library, photos[0], photos[1], IsLightTheme) { Owner = this }.ShowDialog();
    }
    private void CopyPhotoFiles(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0) { status.Text = "请先选择图片。"; return; }
        try
        {
            var paths = new StringCollection();
            foreach (var photo in photos) { var path = library.Original(photo); if (!File.Exists(path)) throw new FileNotFoundException("原图不存在：" + photo.OriginalName); paths.Add(path); }
            Clipboard.SetFileDropList(paths); status.Text = $"已复制 {paths.Count} 个图片文件，可粘贴到文件夹。";
        }
        catch (Exception ex) { ShowDetails("无法复制图片文件", ex.Message); }
    }
    private async Task ExportPhotos(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0) { status.Text = "请先选择图片。"; return; }
        var picker = new OpenFolderDialog { Title = "导出原图到文件夹（保留原文件名，同名自动编号）" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try { var paths = await Task.Run(() => library.ExportPhotos(photos, picker.FolderName)); status.Text = $"已导出 {paths.Count} 张原图至 {picker.FolderName}"; }
        catch (Exception ex) { ShowDetails("导出未完成", ex.Message); }
        finally { SetBusy(false); }
    }
    private async Task DeleteSelected()
    {
        var entries = VisiblePhotos().Where(entry => selected.Contains(entry.Photo.Id)).ToList();
        if (entries.Count == 0) { status.Text = "请先选择图片。"; return; }
        if (MessageBox.Show(this, $"将选中的 {entries.Count} 张原图移入回收站？可在恢复中心恢复。", "删除选中图片", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        SetBusy(true); generation++; var errors = new List<string>();
        string batchId = Guid.NewGuid().ToString("N");
        try { foreach (var entry in entries) { try { await Task.Run(() => library.DeletePhoto(entry.Post, entry.Photo, batchId)); selected.Remove(entry.Photo.Id); } catch (Exception ex) { errors.Add(entry.Photo.OriginalName + "：" + ex.Message); } } }
        finally { SetBusy(false); Render(); }
        status.Text = $"已删除 {entries.Count - errors.Count} 张 · Ctrl+Z 撤销本次删除";
        if (errors.Count > 0) ShowErrors(errors);
    }
    private async Task UndoLastDelete()
    {
        if (busy || rootView) return;
        var records = library.LastDeletionBatch();
        if (records.Count == 0) { status.Text = "没有可恢复的删除记录。"; return; }
        SetBusy(true);
        int restored = 0;
        try
        {
            foreach (var record in records) { await Task.Run(() => library.RestoreDeletedPhoto(record.Photo.Id)); restored++; }
        }
        catch (Exception ex) { ShowDetails("恢复未完成，可重试", ex.Message); }
        finally { SetBusy(false); Render(); status.Text = $"已恢复 {restored} / {records.Count} 张图片"; }
    }
    private enum DuplicateDecision { Skip, Import, SkipAll, ImportAll, Stop }
    private DuplicateDecision AskDuplicate(string path, Post existing)
    {
        var dialog = Dialog("发现完全相同的图片", 560);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"{Path.GetFileName(path)}\n\n已存在于图组「{existing.DisplayTitle}」。\n跳过会保留来源文件；仍然导入会按通常方式移动来源。", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var result = DuplicateDecision.Stop;
        foreach (var (label, value) in new[] { ("跳过", DuplicateDecision.Skip), ("仍然导入", DuplicateDecision.Import), ("跳过所有重复", DuplicateDecision.SkipAll), ("导入所有重复", DuplicateDecision.ImportAll), ("停止导入", DuplicateDecision.Stop) })
        {
            var button = new Button { Content = label }; button.Click += (_, _) => { result = value; dialog.DialogResult = true; }; buttons.Children.Add(button);
        }
        panel.Children.Add(buttons); dialog.Content = panel;
        dialog.SourceInitialized += (_, _) => WindowAppearance.Apply(dialog, IsLightTheme); dialog.ShowDialog(); return result;
    }
    private void ShowDetails(string title, string text)
    {
        var dialog = Dialog(title, 720); dialog.SizeToContent = SizeToContent.Manual; dialog.Height = 500; dialog.ResizeMode = ResizeMode.CanResize;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var close = new Button { Content = "关闭", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
        close.Click += (_, _) => dialog.Close(); DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) });
        dialog.Content = panel; dialog.SourceInitialized += (_, _) => WindowAppearance.Apply(dialog, IsLightTheme); dialog.ShowDialog();
    }
    private void ShowRecoveryCenter()
    {
        var dialog = Dialog("恢复中心", 780); dialog.SizeToContent = SizeToContent.Manual; dialog.Height = 570; dialog.ResizeMode = ResizeMode.CanResize;
        var panel = new DockPanel { Margin = new Thickness(20) };
        var note = new TextBlock { Text = "恢复分组、顺序及封面。图片恢复至原图组，已移除图组需另行恢复。\n删除恢复副本会占用磁盘空间；索引历史不替代原图备份。", Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
        var tabs = new TabControl(); panel.Children.Add(tabs);
        bool restoring = false;
        dialog.Closing += (_, e) => { if (restoring) e.Cancel = true; };
        async Task Restore(Func<List<string>> action)
        {
            restoring = true; tabs.IsEnabled = false; SetBusy(true);
            try
            {
                var errors = await Task.Run(action);
                if (current != null) current = library.Data.Posts.FirstOrDefault(post => post.Id == current.Id);
                selected.Clear(); Render(); Populate();
                note.Text = errors.Count == 0 ? "恢复完成。" : "已恢复索引，但部分原图缺失。";
                if (errors.Count > 0) ShowDetails("恢复详情", string.Join("\n", errors));
            }
            catch (Exception ex) { note.Text = "恢复未完成：" + ex.Message; }
            finally { restoring = false; tabs.IsEnabled = true; SetBusy(false); }
        }
        void AddTab(string title, List<RecoveryItem> items, string buttonText, bool history = false)
        {
            var body = new DockPanel { Margin = new Thickness(12) };
            var list = new ListBox { ItemsSource = items, DisplayMemberPath = nameof(RecoveryItem.Label) };
            var button = new Button { Content = buttonText, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), IsEnabled = items.Count > 0 };
            button.Click += async (_, _) =>
            {
                if (list.SelectedItem is not RecoveryItem item) { note.Text = "请先选中一条记录。"; return; }
                if (history && MessageBox.Show(dialog, "将分组、顺序、收藏和封面恢复到选中的历史版本。当前索引会先保留快照；原图不会被删除。继续？", "恢复历史索引", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                await Restore(item.Restore);
            };
            DockPanel.SetDock(button, Dock.Bottom); body.Children.Add(button); body.Children.Add(list);
            tabs.Items.Add(new TabItem { Header = $"{title}（{items.Count}）", Content = body });
        }
        void Populate()
        {
            int index = tabs.SelectedIndex; tabs.Items.Clear();
            AddTab("已移除图组", library.Data.ArchivedPosts.Select(post => new RecoveryItem($"{post.DisplayTitle}　·　{post.Images.Count} 张", () => { library.RestorePost(post.Id); return []; })).ToList(), "恢复图组");
            AddTab("已删除图片", library.Data.DeletedPhotos.OrderByDescending(item => item.DeletedAt).Select(item => new RecoveryItem($"{item.DeletedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}　{item.Photo.OriginalName}　·　{item.PostTitle}", () => { library.RestoreDeletedPhoto(item.Photo.Id); return []; })).ToList(), "恢复图片");
            AddTab("索引历史", library.GetHistoryFiles().Select(path => new RecoveryItem($"{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss}　{Path.GetFileName(path)}", () => library.RestoreHistory(path))).ToList(), "恢复选中历史…", true);
            tabs.SelectedIndex = Math.Clamp(index, 0, 2);
        }
        Populate(); dialog.Content = panel; dialog.SourceInitialized += (_, _) => WindowAppearance.Apply(dialog, IsLightTheme); dialog.ShowDialog();
    }
    private sealed record RecoveryItem(string Label, Func<List<string>> Restore);
}
