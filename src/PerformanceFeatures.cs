using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ImagePostManager;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer dragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Border dropMarker = new() { Width = 4, Background = Accent, IsHitTestVisible = false };
    private readonly Dictionary<string, (Post Post, string Signature, Border Card)> postCards = [];
    private readonly LinkedList<string> postCardOrder = [];
    private static string PostCardKey(Post post) => $"{post.Id}|{CoverPhoto(post)?.Id}|{post.Title}|{post.Images.Count}";
    private void ConfigurePostCards()
    {
        var posts = library.Data.Posts.AsEnumerable().Reverse().ToList();
        cards.SetVirtual(posts.Select(PostCardKey).ToArray(),
            i => PostCard(posts[i]), i => PhotoRatio(CoverPhoto(posts[i])));
    }
    private Border PostCard(Post post)
    {
        string key = library.Root + "|" + post.Id;
        var photo = CoverPhoto(post);
        string signature = $"{post.Title}|{photo?.Id}|{post.Images.Count}";
        postCardOrder.Remove(key); postCardOrder.AddFirst(key);
        if (postCards.TryGetValue(key, out var cached) && ReferenceEquals(cached.Post, post) && cached.Signature == signature)
        {
            if (photo != null)
            {
                var image = Images(cached.Card).First();
                if (image.Source == null) LoadThumbnail(photo, image, generation);
            }
            return cached.Card;
        }
        var card = MakeCard(photo, post.DisplayTitle, "", generation, () => EnterPost(post), post.Images.Count,
            photoOwner: post, order: library.Data.Posts, orderItem: post);
        postCards[key] = (post, signature, card);
        while (postCards.Count > 96 && postCardOrder.Last is { } last)
        { postCards.Remove(last.Value); postCardOrder.RemoveLast(); }
        return card;
    }
    private static IEnumerable<Image> Images(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image image) yield return image;
            foreach (var nested in Images(child)) yield return nested;
        }
    }
    private void ShowDropMarker(Rect rect)
    {
        if (!dragOverlay.Children.Contains(dropMarker)) dragOverlay.Children.Add(dropMarker);
        var point = cards.TranslatePoint(rect.TopLeft, dragOverlay);
        dropMarker.Height = rect.Height;
        Canvas.SetLeft(dropMarker, point.X); Canvas.SetTop(dropMarker, point.Y);
    }
    private void RemoveDropMarker() => dragOverlay.Children.Remove(dropMarker);
    // All UI mutation is excluded until this one immutable snapshot commits. File journals
    // continue using synchronous Save at their original durable boundaries.
    private TaskCompletionSource? editFinished;
    internal double LastSnapshotMs { get; private set; }
    private async Task<bool> PersistEdit(Action rollback)
    {
        SetBusy(true); editFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var post in library.Data.Posts.Concat(library.Data.ArchivedPosts))
                foreach (var photo in post.Images)
                    if (photoRatios.TryGetValue(photo.Id, out var aspect)) photo.AspectRatio = aspect;
            var snapshot = library.Snapshot(); LastSnapshotMs = watch.Elapsed.TotalMilliseconds;
            await library.SaveSnapshotAsync(snapshot); return true;
        }
        catch (Exception ex) { rollback(); ShowDetails("修改未保存", ex.Message); return false; }
        finally { SetBusy(false); var finished = editFinished; editFinished = null; finished?.SetResult(); }
    }
    private async Task ChangeFavorites(IEnumerable<Photo> photos, bool value)
    {
        var previous = photos.Select(p => (Photo: p, Favorite: p.IsFavorite)).ToArray();
        foreach (var item in previous) item.Photo.IsFavorite = value;
        RefreshFavorites();
        await PersistEdit(() => { foreach (var item in previous) item.Photo.IsFavorite = item.Favorite; RefreshFavorites(); });
    }
    private void RefreshFavorites()
    {
        if (favoritesOnly) { Render(); return; }
        foreach (var image in Images(cards))
            if (image.Parent is AspectSurface surface && surface.Tag is ValueTuple<Photo, TextBlock> state)
                state.Item2.Visibility = state.Item1.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
    }
}
