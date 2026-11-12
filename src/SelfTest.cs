using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImagePostManager;

internal static class SelfTest
{
    private static int checks;
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Click(MainWindow window, string label)
    {
        if (label is "设为封面" or "← 前移" or "后移 →")
        {
            typeof(MainWindow).GetMethod("ShowBatchMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            var actions = (WrapPanel)typeof(MainWindow).GetField("actions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            var menu = actions.ContextMenu!;
            var item = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == label);
            if (!item.IsEnabled) throw new Exception("Disabled command: " + label);
            menu.IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        else Descendants<Button>(window).Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static void Assert(bool condition, string label)
    { if (!condition) throw new Exception("FAILED: " + label); checks++; }
    private static void WaitForEdit(MainWindow window)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            var field = typeof(MainWindow).GetField("busy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            if ((bool)field.GetValue(window)!) return;
            window.UpdateLayout(); timer.Stop(); frame.Continue = false;
        };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Fixture(string path, Color color, int width = 600, int height = 800)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(color, Colors.MidnightBlue, 65), null, new Rect(0, 0, width, height));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(100, 255, 220, 170)), null, new Point(370, 260), 150, 150);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 120, 160, 255)), null, new Point(170, 600), 270, 210);
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bmp.Render(visual);
        BitmapEncoder encoder = Path.GetExtension(path) == ".jpg" ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp)); using var file = File.Create(path); encoder.Save(file);
    }
    public static int Run(string output, string? workflowFixture = null)
    {
        Directory.CreateDirectory(output);
        try
        {
            var root = Path.Combine(output, "test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            FeatureTests.Run(root, workflowFixture, Assert);
            LibraryEnhancementTests.Run(root, Assert);
            ViewerEnhancementTests.Run(root, Assert);
            BrowseEnhancementTests.Run(root, Assert);
            var libraryRoot = Path.Combine(root, "图库");
            string aPath = Path.Combine(root, "测试图片.png"); Fixture(aPath, Colors.SlateBlue);
            var hash = Library.Digest(aPath);
            string firstId;
            using (var lib = new Library(libraryRoot))
            {
                var first = lib.AddPost("蓝色的梦"); firstId = first.Id;
                var a = lib.Import(first, aPath);
                Assert(!File.Exists(aPath) && File.Exists(lib.Original(a)), "move source into managed library");
                Assert(Library.Digest(lib.Original(a)) == hash, "preserve exact original bytes");
                Assert(File.Exists(lib.Thumb(a)) && a.Source == null, "thumbnail and journal completion");
                Assert(first.Cover == a.Id, "initial cover");
                var second = lib.AddPost("暮色研究"); lib.Transfer(first, second, new[] { a.Id });
                Assert(first.Images.Count == 0 && first.Cover == null && second.Cover == a.Id, "cross-post move repairs covers");
                lib.Transfer(second, first, new[] { a.Id });
                bool locked = false; try { using var duplicate = new Library(libraryRoot); } catch (IOException) { locked = true; }
                Assert(locked, "exclusive library lock");
                bool reject = false; try { lib.Import(first, lib.Original(a)); } catch (IOException) { reject = true; }
                Assert(reject && File.Exists(lib.Original(a)), "reject importing managed original");
                var corrupt = Path.Combine(root, "corrupt.png"); File.WriteAllText(corrupt, "not an image");
                int before = first.Images.Count; try { lib.Import(first, corrupt); } catch { }
                Assert(first.Images.Count == before && File.Exists(corrupt), "invalid image leaves source unchanged");
                var pendingPath = Path.Combine(root, "恢复.jpg"); Fixture(pendingPath, Colors.Coral);
                var pending = new Photo { Source = pendingPath, Hash = Library.Digest(pendingPath), OriginalName = "恢复.jpg", Extension = ".jpg" };
                first.Images.Add(pending); lib.Save();
                Assert(lib.Recover().Count == 0 && !File.Exists(pendingPath), "recover journal before copy");
                var copiedPath = Path.Combine(root, "copied.png"); Fixture(copiedPath, Colors.Teal);
                var copied = new Photo { Source = copiedPath, Hash = Library.Digest(copiedPath), OriginalName = "copied.png" };
                first.Images.Add(copied); File.Copy(copiedPath, lib.Original(copied)); lib.Save();
                Assert(lib.Recover().Count == 0 && !File.Exists(copiedPath), "recover after copy before source deletion");
                var deletedPath = Path.Combine(root, "deleted.png"); Fixture(deletedPath, Colors.Plum);
                var deleted = new Photo { Source = deletedPath, Hash = Library.Digest(deletedPath), OriginalName = "deleted.png" };
                first.Images.Add(deleted); File.Move(deletedPath, lib.Original(deleted)); lib.Save();
                Assert(lib.Recover().Count == 0 && deleted.Source == null, "recover after source deletion before commit");
                var changedPath = Path.Combine(root, "changed.png"); Fixture(changedPath, Colors.Gold);
                var changed = new Photo { Source = changedPath, Hash = Library.Digest(changedPath), OriginalName = "changed.png" };
                first.Images.Add(changed); lib.Save(); Fixture(changedPath, Colors.Green);
                Assert(lib.Recover().Count == 1 && File.Exists(changedPath) && changed.Source != null, "changed source survives recovery");
                first.Images.Remove(changed); lib.Save();
                File.Delete(lib.Thumb(a)); lib.EnsureThumbnail(a); Assert(File.Exists(lib.Thumb(a)), "rebuild thumbnail cache");
                for (int i = 0; i < 8; i++)
                {
                    var post = lib.AddPost(new[] { "光与轮廓", "窗边的午后", "远方来信", "雨后街角", "蓝调时刻", "夏日碎片", "山间薄雾", "留白" }[i]);
                    var source = Path.Combine(root, "fixture-" + i + ".png");
                    Fixture(source, new[] { Colors.DarkCyan, Colors.Salmon, Colors.SlateBlue, Colors.DarkOliveGreen, Colors.SteelBlue, Colors.Goldenrod, Colors.CadetBlue, Colors.RosyBrown }[i], i % 3 == 0 ? 1000 : 600, i % 3 == 1 ? 600 : 800);
                    lib.Import(post, source);
                }
            }
            var trash = Path.GetFullPath(Path.Combine(root, "test-trash")); Directory.CreateDirectory(trash);
            Action<string> fakeRecycle = file => File.Move(file, Path.Combine(trash, Path.GetFileName(file)), true);
            using (var deletes = new Library(Path.Combine(root, "delete-library")))
            {
                deletes.RemoveOriginal = fakeRecycle;
                var unlink = deletes.AddPost("unlink");
                var source = Path.Combine(root, "unlink.png"); Fixture(source, Colors.Pink);
                var photo = deletes.Import(unlink, source);
                var originalHash = Library.Digest(deletes.Original(photo));
                deletes.UnlinkPost(unlink);
                Assert(!deletes.Data.Posts.Contains(unlink) && File.Exists(deletes.Original(photo)) && File.Exists(deletes.Thumb(photo)), "unlink post keeps original and cache");
                Assert(Library.Digest(deletes.Original(photo)) == originalHash, "unlink leaves original bytes unchanged");
                var deletionPost = deletes.AddPost("delete");
                Photo Add(string name)
                {
                    var file = Path.Combine(root, name + ".png"); Fixture(file, Colors.Teal);
                    return deletes.Import(deletionPost, file);
                }
                var first = Add("delete-first"); var second = Add("delete-second"); var third = Add("delete-third");
                deletes.Data.Cover = first.Id; deletes.Save(); deletes.DeletePhoto(deletionPost, first);
                Assert(!File.Exists(deletes.Original(first)) && File.Exists(Path.Combine(trash, Path.GetFileName(deletes.Original(first)))), "delete removes original through recycle adapter");
                Assert(deletionPost.Cover == second.Id && deletes.Data.Cover == null, "deleting cover repairs post and library covers");
                Assert(!File.Exists(deletes.Thumb(first)), "delete removes thumbnail cache");
                deletes.RemoveOriginal = _ => throw new IOException("simulated denial");
                bool denied = false; try { deletes.DeletePhoto(deletionPost, second); } catch (IOException) { denied = true; }
                Assert(denied && deletionPost.Images.Contains(second) && File.Exists(deletes.Original(second)) && !second.Deleting, "failed file delete preserves record and original");
                deletes.RemoveOriginal = fakeRecycle;
                second.Deleting = true; deletes.Save(); fakeRecycle(deletes.Original(second));
                Assert(deletes.Recover().Count == 0 && !deletionPost.Images.Contains(second), "recover deletion after file removal");
                third.Deleting = true; deletes.Save();
                Assert(deletes.Recover().Count == 0 && deletionPost.Images.Count == 0 && deletionPost.Cover == null, "recover deletion before file removal and empty post");
            }
            using (var reopened = new Library(libraryRoot))
            {
                Assert(reopened.Data.Posts.Single(p => p.Id == firstId).Images.Count == 4, "persist grouping across restarts");
                reopened.RemoveOriginal = fakeRecycle;
                var settingsDirectory = Path.Combine(root, "settings");
                Directory.CreateDirectory(settingsDirectory);
                File.WriteAllText(Path.Combine(settingsDirectory, "library.txt"), reopened.Root);
                var settings = Preferences.Load(settingsDirectory);
                Assert(settings.Libraries.Single() == reopened.Root, "migrate legacy library setting");
                settings.Add(reopened.Root.ToUpperInvariant());
                Assert(settings.Libraries.Count == 1, "deduplicate library registration");
                var secondRoot = Path.GetFullPath(Path.Combine(root, "第二图库"));
                using (var other = new Library(secondRoot))
                {
                    var otherPost = other.AddPost("第二图库图组");
                    var fixture = Path.Combine(root, "second-cover.png");
                    Fixture(fixture, Colors.Orange, 1200, 600);
                    other.Import(otherPost, fixture);
                }
                settings.Add(secondRoot);
                var portableDirectory = Path.Combine(root, "portable-app"); Directory.CreateDirectory(portableDirectory);
                var legacyDirectory = Path.Combine(root, "legacy-appdata"); Directory.CreateDirectory(legacyDirectory);
                var seeded = new Preferences { FilePath = Preferences.PortableFile(legacyDirectory), Theme = "light", CardWidth = 300 };
                seeded.Add(reopened.Root);
                Assert(Preferences.MigrateLegacy(portableDirectory, legacyDirectory), "portable settings migrate out of the legacy user store");
                Assert(File.Exists(Preferences.PortableFile(portableDirectory)) && !File.Exists(Path.Combine(legacyDirectory, "settings.json")), "legacy settings file removed after migration");
                var portable = Preferences.Load(portableDirectory);
                Assert(portable.Theme == "light" && Math.Abs(portable.CardWidth - 300) < .01 && portable.Libraries.Single() == reopened.Root, "portable settings keep libraries, theme and image size");
                Assert(!Preferences.MigrateLegacy(portableDirectory, legacyDirectory), "existing portable settings are never overwritten by migration");
                var textLegacy = Path.Combine(root, "legacy-text"); Directory.CreateDirectory(textLegacy);
                File.WriteAllText(Path.Combine(textLegacy, "library.txt"), reopened.Root);
                var textPortable = Path.Combine(root, "portable-app2"); Directory.CreateDirectory(textPortable);
                Assert(Preferences.MigrateLegacy(textPortable, textLegacy), "legacy library.txt alone is migrated too");
                Assert(Preferences.Load(textPortable).Libraries.Single() == reopened.Root && !File.Exists(Path.Combine(textLegacy, "library.txt")), "library.txt migration keeps the entry and clears the old file");
                Assert(Preferences.Load(Path.Combine(root, "empty-appdata")).CardWidth == Preferences.DefaultCardWidth, "missing settings fall back to the default image size");
                var clampedFile = Preferences.PortableFile(Path.Combine(root, "clamped-appdata"));
                var wide = new Preferences { FilePath = clampedFile, CardWidth = 9000 };
                wide.Save();
                Assert(File.Exists(clampedFile), "portable settings file and folder are created on demand");
                Assert(Preferences.Load(Path.GetDirectoryName(clampedFile)!).CardWidth == Preferences.MaximumCardWidth, "out of range image size is clamped on load");
                Assert(Preferences.PortableDirectory == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), "settings default to the program folder beside the executable");
                var gauge = new MasonryPanel { TileWidth = 236 };
                gauge.Children.Add(new Border { Width = 20, Height = 20 });
                gauge.Measure(new Size(1400, double.PositiveInfinity));
                Assert(gauge.ColumnCount == 6, "narrow columns fit six images in a wide window");
                gauge.TileWidth = 354; gauge.InvalidateMeasure();
                gauge.Measure(new Size(1400, double.PositiveInfinity));
                Assert(gauge.ColumnCount == 4, "raising the image size reflows the same window to four columns");
                gauge.TileWidth = 140; gauge.InvalidateMeasure();
                gauge.Measure(new Size(1400, double.PositiveInfinity));
                Assert(gauge.ColumnCount > 6, "lowering the image size increases the column count");
                var folderCommand = ExplorerNavigation.CreateStartInfo(reopened.Root);
                Assert(folderCommand.FileName == "explorer.exe" && folderCommand.ArgumentList.Single() == reopened.Root, "explorer opens library folder");
                var group = reopened.Data.Posts.Single(p => p.Id == firstId);
                var cover = group.Images.First(p => p.Id == group.Cover);
                Assert(ExplorerNavigation.CreateStartInfo(reopened.Root, group).Arguments == "/select,\"" + reopened.Original(cover) + "\"", "explorer selects post cover in originals");
                Assert(ExplorerNavigation.CreateStartInfo(reopened.Root, new Post()).ArgumentList.Single() == Path.Combine(reopened.Root, "originals"), "empty post opens originals directory");
                var spaced = Path.Combine(root, "包含 空格"); Directory.CreateDirectory(spaced);
                Assert(ExplorerNavigation.CreateStartInfo(spaced).ArgumentList.Single() == Path.GetFullPath(spaced), "folder argument preserves spaces and unicode");
                bool missingPath = false;
                try { ExplorerNavigation.CreateStartInfo(Path.Combine(root, "missing")); } catch (DirectoryNotFoundException) { missingPath = true; }
                Assert(missingPath, "unavailable explorer target is rejected");
                settings.Remove(secondRoot);
                Assert(Directory.Exists(secondRoot) && File.Exists(Path.Combine(secondRoot, "library.json")), "unregister library keeps folder and index");
                settings.Add(secondRoot);
                var app = new Application();
                BrowseEnhancementTests.RunUi(root, Assert);
                PerformanceTests.RunUi(root, Assert);
                var window = new MainWindow(reopened, settings); app.MainWindow = window; window.Show();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                string? deletedFromViewer = null;
                string? openedAfterDrag = null;
                int phase = 0; Exception? uiError = null;
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        switch (phase++)
                        {
                            case 0:
                                Assert(!Descendants<TextBlock>(window).Any(t => t.Text.StartsWith("图库：")), "library footer path removed");
                                var navRoot = (TreeViewItem)Descendants<TreeView>(window).Single().Items[0];
                                Assert(((MenuItem)navRoot.ContextMenu.Items[0]).Items.Count == 2, "virtual root offers registered library folders");
                                var navLibrary = (TreeViewItem)navRoot.Items[0];
                                Assert(((MenuItem)navLibrary.ContextMenu.Items[0]).Header as string == "在资源管理器中打开", "library context menu exists");
                                Assert(((TreeViewItem)navLibrary.Items[0]).ContextMenu != null, "post context menu exists");
                                navLibrary.ContextMenu.PlacementTarget = navLibrary;
                                navLibrary.ContextMenu.IsOpen = true;
                                navLibrary.ContextMenu.UpdateLayout();
                                Assert(navLibrary.ContextMenu.Background is SolidColorBrush, "context menu resolves theme resources");
                                navLibrary.ContextMenu.IsOpen = false;
                                Assert(!Descendants<TextBlock>(window).Any(t => t.Text.Contains("IMAGE POST") || t.Text.Contains("你的灵感")), "decorative headings removed");
                                var sample = Descendants<Image>(window).First(i => i.Source != null);
                                Assert(Math.Abs(sample.ActualWidth / sample.ActualHeight - sample.Source.Width / sample.Source.Height) < .02, "uncropped image aspect ratio");
                                Assert(Descendants<TextBlock>(window).Any(t => t.Text == "▧  4"), "post count badge");
                                Assert(!Descendants<TextBlock>(Descendants<MasonryPanel>(window).Single()).Any(t => t.Text.Contains("张图片") || t.Text.Contains("未命名图组")), "cards have no caption strips");
                                Assert(!Descendants<Button>(window).Any(b => b.Content as string == "库"), "content breadcrumb removed");
                                var postCards = Descendants<MasonryPanel>(window).Single();
                                var draggedPost = (Border)postCards.Children[0];
                                var draggedPostName = draggedPost.ToolTip as string;
                                Assert(window.BeginCardDrag(draggedPost, new Point(10, 10), new Point(10, 10)), "begin post card drag");
                                window.UpdateCardDrag(new Point(10000, 10000));
                                window.CommitCardDrag();
                                WaitForEdit(window);
                                Assert(((Border)postCards.Children[^1]).ToolTip as string == draggedPostName, "drag moves post card to last display slot");
                                Assert(reopened.Data.Posts[0].DisplayTitle == draggedPostName, "drag saves post order in model");
                                var libraryCoverCard = Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().Single(c => c.ToolTip as string == "蓝色的梦");
                                Assert(libraryCoverCard.ContextMenu.Items.Cast<MenuItem>().Select(m => (string)m.Header).SequenceEqual(new[] { "设为封面", "移动到", "删除该图", "复制 ComfyUI 工作流", "复制图片文件", "导出原图…", "收藏" }), "photo menu separates workflow, file copy and original export");
                                ((MenuItem)libraryCoverCard.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                Assert(reopened.Data.Cover == cover.Id, "library-page photo menu sets library cover");
                                Snapshot(window, Path.Combine(output, "preview.png"));
                                Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().Single(c => c.ToolTip as string == "蓝色的梦").RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                                break;
                            case 1:
                                Assert(Descendants<Image>(window).Count() == 4, "open post image grid");
                                Snapshot(window, Path.Combine(output, "post-preview.png"));
                                Click(window, "选择");
                                Assert(!Descendants<CheckBox>(window).Any(), "selection has no image checkboxes");
                                var photoCard = (Border)Descendants<MasonryPanel>(window).Single().Children[1];
                                void ClickPhoto() => Descendants<Image>(photoCard).Single().RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                                ClickPhoto();
                                Assert(app.Windows.OfType<Viewer>().Count() == 0, "selection click does not open viewer");
                                Assert(System.Windows.Automation.AutomationProperties.GetName(photoCard).EndsWith("已选择"), "image click selects card");
                                ClickPhoto();
                                Assert(System.Windows.Automation.AutomationProperties.GetName(photoCard).EndsWith("未选择"), "image click deselects card");
                                ClickPhoto();
                                Click(window, "设为封面");
                                var post = reopened.Data.Posts.Single(p => p.Id == firstId);
                                var chosenId = post.Images.Single(p => p.OriginalName == photoCard.ToolTip as string).Id;
                                var displayIndex = post.Images.Count - 1 - post.Images.FindIndex(p => p.Id == chosenId);
                                Assert(post.Cover == chosenId, "set cover through UI");
                                Click(window, "← 前移");
                                Assert(post.Images[post.Images.Count - displayIndex].Id == chosenId, "reorder through UI");
                                break;
                            case 2:
                                Assert(Descendants<Image>(window).All(i => i.Source != null), "selection retains loaded thumbnails");
                                var hovered = (Border)Descendants<MasonryPanel>(window).Single().Children[0];
                                hovered.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
                                Assert(hovered.Effect is System.Windows.Media.Effects.DropShadowEffect, "card hover has elevation feedback");
                                Snapshot(window, Path.Combine(output, "organize-preview.png"));
                                hovered.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
                                Assert(hovered.Effect == null, "hover effect released on leave");
                                Click(window, "完成选择");
                                window.UpdateLayout();
                                var imageCards = Descendants<MasonryPanel>(window).Single();
                                var dragged = (Border)imageCards.Children[0];
                                var draggedName = dragged.ToolTip as string;
                                Assert(window.BeginCardDrag(dragged, new Point(10, 10), new Point(10, 10)), "begin image card drag");
                                window.UpdateCardDrag(new Point(10000, 10000));
                                window.CommitCardDrag();
                                WaitForEdit(window);
                                Assert(!window.IsCardDragActive && (string?)imageCards.Children[^1].GetValue(FrameworkElement.ToolTipProperty) == draggedName, "drag moves image card to last display slot");
                                Assert(group.Images[0].OriginalName == draggedName, "drag saves image order in model");
                                openedAfterDrag = ((Border)imageCards.Children[0]).ToolTip as string;
                                window.Dispatcher.BeginInvoke(() => ((Border)imageCards.Children[0]).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent }));
                                break;
                            case 3:
                                var viewer = app.Windows.OfType<Viewer>().Single();
                                Assert(openedAfterDrag != null && Descendants<TextBlock>(viewer).Any(t => t.Text.Contains(openedAfterDrag)), "dragged image card opens matching original");
                                Assert(Descendants<Image>(viewer).Single().Source != null, "viewer loads image");
                                Snapshot(viewer, Path.Combine(output, "viewer-preview.png"));
                                Descendants<Button>(viewer).Single(b => b.Content as string == "❯").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                Assert(Descendants<TextBlock>(viewer).Any(t => t.Text.StartsWith("2 / 4")), "viewer advances image");
                                void Wheel(int delta) => viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                                Wheel(120);
                                Assert(Descendants<TextBlock>(viewer).Any(t => t.Text.StartsWith("1 / 4")), "wheel up goes to previous image");
                                Wheel(120);
                                Assert(Descendants<TextBlock>(viewer).Any(t => t.Text.StartsWith("4 / 4")), "wheel wraps backwards at first image");
                                Wheel(-120);
                                Assert(Descendants<TextBlock>(viewer).Any(t => t.Text.StartsWith("1 / 4")), "wheel down wraps at last image");
                                Wheel(-40); Wheel(-40);
                                Assert(Descendants<TextBlock>(viewer).Any(t => t.Text.StartsWith("1 / 4")), "high resolution wheel accumulates partial input");
                                Wheel(-40);
                                Assert(Descendants<TextBlock>(viewer).Any(t => t.Text.StartsWith("2 / 4")), "high resolution wheel advances at threshold");
                                var comparison = new CompareViewer(reopened, group.Images[0], group.Images[1]) { Owner = viewer };
                                comparison.Show(); phase = 30; break;
                            case 30:
                                var compareWindow = app.Windows.OfType<CompareViewer>().Single();
                                Assert(Descendants<Image>(compareWindow).Count() == 2 && Descendants<Image>(compareWindow).All(image => image.Source != null), "comparison loads two independent images");
                                Descendants<CheckBox>(compareWindow).Single().IsChecked = true;
                                Descendants<Button>(compareWindow).Single(button => button.Content as string == "两侧 100%").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                phase = 31; break;
                            case 31:
                                var compareReady = app.Windows.OfType<CompareViewer>().Single();
                                Assert(Descendants<TextBlock>(compareReady).Count(text => text.Text.Contains("100%") && text.Text.Contains("原图")) == 2, "comparison shows full original pixels on both sides");
                                Snapshot(compareReady, Path.Combine(output, "compare-preview.png"));
                                compareReady.Close();
                                var viewerAfterCompare = app.Windows.OfType<Viewer>().Single();
                                var viewerCaption = Descendants<TextBlock>(viewerAfterCompare).Single(t => t.Text.Contains(" / 4")).Text;
                                deletedFromViewer = reopened.Original(group.Images.Single(p => viewerCaption.Contains($"    {p.OriginalName}    ")));
                                viewerAfterCompare.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(viewerAfterCompare), 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
                                phase = 40; break;
                            case 40:
                                Assert(deletedFromViewer != null && !File.Exists(deletedFromViewer) && group.Images.Count == 3, "Delete key removes current original and updates post");
                                Assert(app.Windows.OfType<Viewer>().Single().IsVisible, "viewer stays open with remaining images");
                                var deletingViewer = app.Windows.OfType<Viewer>().Single();
                                deletingViewer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(deletingViewer), 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
                                phase = 41; break;
                            case 41:
                                if (group.Images.Count > 0)
                                {
                                    var remainingViewer = app.Windows.OfType<Viewer>().Single();
                                    remainingViewer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(remainingViewer), 0, Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
                                    phase = 41;
                                }
                                else
                                {
                                    Assert(!app.Windows.OfType<Viewer>().Any() && group.Cover == null, "deleting last image closes viewer and clears cover");
                                    phase = 4;
                                }
                                break;
                            case 4:
                                Assert(Descendants<ScrollViewer>(window).Single(v => v.Content is MasonryPanel).FocusVisualStyle == null, "image area suppresses dashed focus adorner after viewer closes");
                                Assert(Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().All(c => c.FocusVisualStyle == null && c.Focusable), "cards retain keyboard access without dashed adorners");
                                Snapshot(window, Path.Combine(output, "after-viewer.png"));
                                ((TreeViewItem)Descendants<TreeView>(window).Single().Items[0]).IsSelected = true;
                                Assert(Descendants<MasonryPanel>(window).Single().Children.Count == 2, "root shows registered libraries");
                                
                                break;
                            case 5:
                                Assert(!Descendants<TextBlock>(window).Any(t => t.Text == "选择图库查看图组" || t.Text.StartsWith("图库：")), "root footer hint removed");
                                Assert(Descendants<Image>(window).All(i => i.Source != null), "library covers load");
                                Snapshot(window, Path.Combine(output, "libraries-dark.png"));
                                Descendants<ComboBox>(window).Single().SelectedIndex = 1;
                                Assert(((SolidColorBrush)window.Background).Color == WindowAppearance.BackgroundColor(true), "exact light background applies");
                                FeatureTests.CheckWindowColors(window, true, Assert);
                                Assert(Preferences.Load(settingsDirectory).Theme == "light", "theme preference persisted");
                                Snapshot(window, Path.Combine(output, "libraries-light.png"));
                                var libraryCard = Descendants<MasonryPanel>(window).Single();
                                var libraryCaption = libraryCard.Children.OfType<Border>().Single(c => c.ToolTip as string == "图库");
                                libraryCaption.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                                break;
                            case 6:
                                Assert(Descendants<MasonryPanel>(window).Single().Children.Count == 10, "library card opens post list");
                                Snapshot(window, Path.Combine(output, "posts-light.png"));
                                var moveCard = Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().Single(c => c.ToolTip as string == "留白");
                                var sourcePost = reopened.Data.Posts.Single(p => p.Title == "留白");
                                var movingPhoto = sourcePost.Images[0];
                                var moveMenu = (MenuItem)moveCard.ContextMenu.Items[1];
                                moveMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
                                moveMenu.Items.Cast<MenuItem>().Single(m => m.Header as string == "暮色研究").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                window.UpdateLayout();
                                Assert(sourcePost.Images.Count == 0 && reopened.Data.Posts.Single(p => p.Title == "暮色研究").Images.Contains(movingPhoto) && File.Exists(reopened.Original(movingPhoto)), "photo context move moves one image without deleting file");
                                var targetCard = Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().Single(c => c.ToolTip as string == "暮色研究");
                                ((MenuItem)targetCard.ContextMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                phase = 60; break;
                            case 60:
                                Assert(reopened.Data.Posts.Single(p => p.Title == "暮色研究").Images.Count == 0, "photo context delete updates post");
                                var sizeSlider = Descendants<Slider>(window).Single();
                                var masonry = Descendants<MasonryPanel>(window).Single();
                                Assert(Math.Abs(masonry.TileWidth - sizeSlider.Value) < .01, "image size slider starts from the stored value");
                                sizeSlider.Value = Preferences.MaximumCardWidth;
                                Assert(masonry.TileWidth > Preferences.DefaultCardWidth && Math.Abs(masonry.TileWidth - sizeSlider.Value) < .01, "image size slider drives the tile width of the image area");
                                sizeSlider.Value = Preferences.DefaultCardWidth;
                                Assert(Math.Abs(masonry.TileWidth - Preferences.DefaultCardWidth) < .01, "image size slider returns to the default width");
                                var backButton = Descendants<Button>(window).Single(b => b.Content as string == "← 返回");
                                Assert(((WrapPanel)VisualTreeHelper.GetParent(backButton)).Children.IndexOf(backButton) == 0, "back operator is pinned first in the action row");
                                backButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                Assert(Descendants<MasonryPanel>(window).Single().Children.Count == 2, "back operator returns from a library to the virtual root");
                                Assert(!Descendants<Button>(window).Any(b => b.Content as string == "← 返回"), "virtual root exposes no back operator");
                                Snapshot(window, Path.Combine(output, "root-back.png"));
                                var nav = Descendants<TreeView>(window).Single();
                                var rootNode = (TreeViewItem)nav.Items[0];
                                ((TreeViewItem)rootNode.Items[1]).IsSelected = true;
                                phase = 7; break;
                            case 7:
                                Assert(Descendants<MasonryPanel>(window).Single().Children.Count == 1, "sidebar switches between libraries");
                                Assert(Descendants<TextBlock>(window).Any(t => t.Text == "第二图库图组"), "second library content");
                                var tree = Descendants<TreeView>(window).Single();
                                var selectedLibrary = (TreeViewItem)((TreeViewItem)tree.Items[0]).Items[1];
                                ((TreeViewItem)selectedLibrary.Items[0]).IsSelected = true;
                                break;
                            case 8:
                                Assert(Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().Any(c => c.ToolTip as string == "second-cover.png"), "sidebar opens level two post");
                                window.Width = 940; window.UpdateLayout();
                                var image = Descendants<Image>(window).Single();
                                Assert(Math.Abs(image.ActualWidth / image.ActualHeight - 2.0) < .02, "landscape ratio survives narrow window resize");
                                Snapshot(window, Path.Combine(output, "narrow-post.png"));
                                Descendants<ComboBox>(window).Single().SelectedIndex = 0;
                                Assert(((SolidColorBrush)window.Background).Color == WindowAppearance.BackgroundColor(false), "exact dark background applies");
                                FeatureTests.CheckWindowColors(window, false, Assert);
                                var theme = Descendants<ComboBox>(window).Single();
                                Assert(theme.Items.Cast<string>().SequenceEqual(new[] { "深色", "浅色", "系统" }), "theme picker includes system");
                                theme.SelectedIndex = 2;
                                Assert(Preferences.Load(settingsDirectory).Theme == "system", "system mode persists without becoming light or dark");
                                Assert(((SolidColorBrush)window.Background).Color == WindowAppearance.BackgroundColor(SystemTheme.IsLight()), "system mode resolves Windows application theme");
                                FeatureTests.CheckWindowColors(window, SystemTheme.IsLight(), Assert);
                                theme.SelectedIndex = 0;
                                break;
                            case 9:
                                var secondOriginals = Path.Combine(secondRoot, "originals");
                                var filesBefore = Directory.GetFiles(secondOriginals);
                                var secondTree = Descendants<TreeView>(window).Single();
                                var secondNode = (TreeViewItem)((TreeViewItem)secondTree.Items[0]).Items[1];
                                var postNode = (TreeViewItem)secondNode.Items[0];
                                ((MenuItem)postNode.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                Assert(Directory.GetFiles(secondOriginals).SequenceEqual(filesBefore), "navigation unlink post preserves disk files");
                                Assert(Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().Count() == 0, "navigation unlink removes post card");
                                window.Dispatcher.BeginInvoke(() => typeof(MainWindow).GetMethod("ShowRecoveryCenter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)); phase = 90;
                                break;
                            case 90:
                                var recovery = app.Windows.Cast<Window>().Single(w => w.Title == "恢复中心");
                                var recoveryTabs = Descendants<TabControl>(recovery).Single();
                                Assert(recoveryTabs.Items.Count == 3, "recovery center exposes archived groups deleted images and history");
                                var archivedList = Descendants<ListBox>(recovery).Single();
                                Assert(archivedList.Items.Count == 1, "recovery UI lists the archived group");
                                archivedList.SelectedIndex = 0;
                                Descendants<Button>(recovery).Single(button => button.Content as string == "恢复图组").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                                phase = 91; break;
                            case 91:
                                var restoredDialog = app.Windows.Cast<Window>().Single(w => w.Title == "恢复中心");
                                Assert(Descendants<ListBox>(restoredDialog).Single().Items.Count == 0 && Descendants<MasonryPanel>(window).Single().Children.OfType<Border>().Count() == 1, "recovery UI restores the archived group and refreshes browser");
                                Descendants<TabControl>(restoredDialog).Single().SelectedIndex = 2;
                                restoredDialog.UpdateLayout();
                                Assert(Descendants<ListBox>(restoredDialog).Single().Items.Count > 0, "recovery UI displays stable index snapshots");
                                Snapshot(restoredDialog, Path.Combine(output, "recovery-preview.png"));
                                restoredDialog.Close();
                                var restoredTree = Descendants<TreeView>(window).Single();
                                var restoredNode = (TreeViewItem)((TreeViewItem)restoredTree.Items[0]).Items[1];
                                ((MenuItem)restoredNode.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                Assert(settings.Libraries.Count == 1 && Directory.GetFiles(Path.Combine(secondRoot, "originals")).Length > 0, "navigation unlink library preserves files");
                                phase = 10;
                                break;
                            default:
                                timer.Stop(); window.Close(); app.Shutdown(); break;
                        }
                    }
                    catch (Exception ex) { uiError = ex; timer.Stop(); foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close(); app.Shutdown(); }
                };
                timer.Start(); app.Run();
                if (uiError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(uiError).Throw();
            }
            File.WriteAllText(Path.Combine(output, "test-results.txt"), $"PASS: {checks} checks\nTest library: {libraryRoot}\n"); return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "test-results.txt"), ex.ToString()); return 1; }
    }
}
