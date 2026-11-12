using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace ImagePostManager;

public sealed class Photo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Extension { get; set; } = ".png";
    public string OriginalName { get; set; } = "";
    public string? Source { get; set; }
    public string? Hash { get; set; }
    public bool Deleting { get; set; }
    public bool IsFavorite { get; set; }
    public double AspectRatio { get; set; }
    internal Photo Snapshot() => (Photo)MemberwiseClone();
}
public sealed class Post
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string? Cover { get; set; }
    public List<Photo> Images { get; set; } = [];
    public DateTime? ArchivedAt { get; set; }
    public int? ArchivedIndex { get; set; }
    public string? ArchivedLibraryCover { get; set; }
    internal Post Snapshot() { var copy = (Post)MemberwiseClone(); copy.Images = Images.Select(p => p.Snapshot()).ToList(); return copy; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未命名图组" : Title;
}
public sealed class Catalog
{
    public int Version { get; set; } = 1;
    public string? Cover { get; set; }
    public List<Post> Posts { get; set; } = [];
    public List<Post> ArchivedPosts { get; set; } = [];
    public List<DeletedPhotoRecord> DeletedPhotos { get; set; } = [];
}
public sealed class DeletedPhotoRecord
{
    public Photo Photo { get; set; } = new();
    public string PostId { get; set; } = "";
    public string PostTitle { get; set; } = "";
    public DateTime PostCreatedAt { get; set; }
    public int PostIndex { get; set; }
    public int ImageIndex { get; set; }
    public string? PostCover { get; set; }
    public string? LibraryCover { get; set; }
    public DateTime DeletedAt { get; set; } = DateTime.Now;
    public bool Pending { get; set; }
    public bool RecoveryAvailable { get; set; }
    public string? DeleteBatchId { get; set; }
    internal DeletedPhotoRecord Snapshot() { var copy = (DeletedPhotoRecord)MemberwiseClone(); copy.Photo = Photo.Snapshot(); return copy; }
}
public sealed partial class Library : IDisposable
{
    public string Root { get; }
    public Catalog Data { get; private set; }
    private readonly FileStream libraryLock;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Original(Photo p) { ValidatePhoto(p); return Path.Combine(Root, "originals", p.Id + p.Extension); }
    public string Thumb(Photo p) { ValidatePhoto(p); return Path.Combine(Root, "thumbnails", p.Id + ".jpg"); }
    public Library(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        libraryLock = new FileStream(Path.Combine(Root, "library.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Directory.CreateDirectory(Path.Combine(Root, "originals"));
            Directory.CreateDirectory(Path.Combine(Root, "thumbnails"));
            Directory.CreateDirectory(Path.Combine(Root, "recovery"));
            Directory.CreateDirectory(Path.Combine(Root, "history"));
            var index = Path.Combine(Root, "library.json");
            Data = File.Exists(index) ? JsonSerializer.Deserialize<Catalog>(File.ReadAllText(index)) ?? throw new InvalidDataException("图库索引为空") : new();
            ValidateCatalog(Data);
            if (!File.Exists(index)) Save();
        }
        catch { libraryLock.Dispose(); throw; }
    }
    private readonly object saveLock = new();
    internal Catalog Snapshot() => new() { Version = Data.Version, Cover = Data.Cover,
        Posts = Data.Posts.Select(p => p.Snapshot()).ToList(), ArchivedPosts = Data.ArchivedPosts.Select(p => p.Snapshot()).ToList(),
        DeletedPhotos = Data.DeletedPhotos.Select(r => r.Snapshot()).ToList() };
    internal Task SaveSnapshotAsync(Catalog snapshot) => Task.Run(() => SaveSnapshot(snapshot));
    public void Save() => SaveSnapshot(Data);
    internal sealed record SaveTiming(double ValidateMs, double SerializeFlushMs, double HistoryMs, double ReplaceMs);
    internal SaveTiming? LastSaveTiming { get; private set; }
    private void SaveSnapshot(Catalog snapshot)
    {
        lock (saveLock)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ValidateCatalog(snapshot);
            double validate = watch.Elapsed.TotalMilliseconds;
            var dest = Path.Combine(Root, "library.json");
            var tmp = dest + ".tmp";
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, snapshot, Json);
                file.Flush(true);
            }
            double serialized = watch.Elapsed.TotalMilliseconds;
            if (File.Exists(dest)) SnapshotStableIndex(dest);
            double history = watch.Elapsed.TotalMilliseconds;
            if (File.Exists(dest)) File.Replace(tmp, dest, dest + ".bak");
            else File.Move(tmp, dest);
            LastSaveTiming = new(validate, serialized - validate, history - serialized, watch.Elapsed.TotalMilliseconds - history);
        }
    }
    public Post AddPost(string title)
    {
        var p = new Post { Title = title.Trim() };
        // The UI displays posts in reverse order, so appended posts appear on top.
        Data.Posts.Add(p);
        try { Save(); } catch { Data.Posts.Remove(p); throw; }
        return p;
    }
    public static string Digest(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }
    public Photo Import(Post post, string source)
    {
        source = Path.GetFullPath(source);
        if (!Data.Posts.Contains(post)) throw new InvalidOperationException("图组已不在此图库中。");
        if (IsWithin(source, Root) || IsWithin(PhysicalDirectory(Path.GetDirectoryName(source)!), PhysicalDirectory(Root)))
            throw new IOException("不能重复导入图库内部的文件，请使用「移动到图组」。");
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg")) throw new IOException("仅支持 PNG 和 JPG 图片。");
        if (AllPosts().SelectMany(p => p.Images).Any(p => string.Equals(p.Source, source, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("该文件有未完成的导入，请先重新打开图库以恢复。");
        // Decode before touching the source, rejecting mislabeled or damaged inputs.
        _ = ReadBitmap(source, 32);
        var photo = new Photo { Extension = ext, OriginalName = Path.GetFileName(source), Source = source, Hash = Digest(source) };
        var previousCover = post.Cover;
        post.Images.Add(photo);
        post.Cover ??= photo.Id;
        try { Save(); } // Durable journal before touching files.
        catch { post.Images.Remove(photo); post.Cover = previousCover; throw; }
        FinishImport(photo);
        return photo;
    }
    private void FinishImport(Photo p)
    {
        if (p.Source is null) return;
        var destination = Original(p);
        if (!File.Exists(destination))
        {
            if (!File.Exists(p.Source)) throw new IOException($"来源文件不存在：{p.OriginalName}");
            var temp = destination + ".importing";
            using (var input = File.OpenRead(p.Source))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            if (Digest(temp) != p.Hash) throw new IOException("来源文件发生变化；原文件已保留。");
            File.Move(temp, destination);
        }
        if (Digest(destination) != p.Hash) throw new IOException("图库副本校验失败；原文件已保留。");
        EnsureThumbnail(p);
        // Failure here is safe: both copies and the journal remain for recovery.
        if (File.Exists(p.Source))
        {
            if (Digest(p.Source) != p.Hash) throw new IOException("来源文件发生变化；原文件已保留。");
            File.Delete(p.Source);
        }
        var source = p.Source;
        p.Source = null;
        try { Save(); } catch { p.Source = source; throw; }
    }
    internal Action<string> RemoveOriginal { get; set; } = path =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);

    public void UnlinkPost(Post post)
    {
        if (post.Images.Any(p => p.Source != null || p.Deleting)) throw new IOException("请先完成该图组未完成的文件操作。");
        if (!Data.Posts.Contains(post)) return;
        var index = Data.Posts.IndexOf(post); var oldCover = Data.Cover;
        Data.Posts.Remove(post);
        post.ArchivedAt = DateTime.Now;
        post.ArchivedIndex = index;
        post.ArchivedLibraryCover = oldCover;
        Data.ArchivedPosts.Add(post);
        if (post.Images.Any(p => p.Id == Data.Cover)) Data.Cover = null;
        try { Save(); }
        catch
        {
            Data.ArchivedPosts.Remove(post); Data.Posts.Insert(index, post); Data.Cover = oldCover;
            post.ArchivedAt = null; post.ArchivedIndex = null; post.ArchivedLibraryCover = null; throw;
        }
    }
    public void DeletePhoto(Post post, Photo photo, string? batchId = null)
    {
        if (!Data.Posts.Contains(post) || !post.Images.Contains(photo)) throw new InvalidOperationException("图片已不在该图组中。");
        if (photo.Source != null) throw new IOException("请先完成这张图片的导入恢复。");
        if (!Guid.TryParseExact(photo.Id, "N", out _) || photo.Extension is not (".png" or ".jpg" or ".jpeg")) throw new InvalidDataException("无效图片路径");
        if (photo.Deleting) throw new IOException("请先完成这张图片的删除恢复。");
        var record = CreateDeletionRecord(post, photo);
        record.DeleteBatchId = batchId;
        photo.Deleting = true;
        Data.DeletedPhotos.Add(record);
        try { Save(); } catch { photo.Deleting = false; Data.DeletedPhotos.Remove(record); throw; }
        try { FinishDelete(post, photo); }
        catch
        {
            if (File.Exists(Original(photo)))
            {
                photo.Deleting = false; Data.DeletedPhotos.Remove(record);
                try { Save(); }
                catch { photo.Deleting = true; Data.DeletedPhotos.Add(record); throw; }
            }
            throw;
        }
    }
    private void FinishDelete(Post post, Photo photo)
    {
        var record = Data.DeletedPhotos.SingleOrDefault(r => r.Photo.Id == photo.Id);
        if (record is null)
        {
            // Older version-one journals did not keep a recovery copy.
            record = CreateDeletionRecord(post, photo); Data.DeletedPhotos.Add(record); Save();
        }
        record.Photo = photo;
        record.RecoveryAvailable = File.Exists(RecoveryFile(photo));
        if (File.Exists(Original(photo)))
        {
            EnsureRecoveryCopy(photo);
            record.RecoveryAvailable = true;
            Save(); // Commit the verified local copy before calling the Windows Recycle Bin.
        }
        if (File.Exists(Original(photo))) RemoveOriginal(Original(photo));
        if (File.Exists(Original(photo))) throw new IOException("文件未被移除，已保留图片记录。");
        // Cache cleanup is noncritical and can race with a short thumbnail read.
        try { File.Delete(Thumb(photo)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        int index = post.Images.IndexOf(photo);
        var oldCover = post.Cover; var oldLibraryCover = Data.Cover;
        post.Images.Remove(photo);
        if (post.Cover == photo.Id) post.Cover = post.Images.FirstOrDefault()?.Id;
        if (Data.Cover == photo.Id) Data.Cover = null;
        photo.Deleting = false;
        record.Pending = false;
        try { Save(); }
        catch
        {
            post.Images.Insert(index, photo); post.Cover = oldCover; Data.Cover = oldLibraryCover;
            photo.Deleting = true; record.Pending = true;
            throw; // The persisted deletion journal remains recoverable.
        }
    }
    public List<string> Recover()
    {
        List<string> errors = [];
        foreach (var post in AllPosts())
            foreach (var photo in post.Images.Where(p => p.Deleting).ToList())
                try { FinishDelete(post, photo); } catch (Exception ex) { errors.Add($"{photo.OriginalName}: {ex.Message}"); }
        foreach (var p in AllPosts().SelectMany(p => p.Images).Where(p => p.Source != null && !p.Deleting).ToList())
            try { FinishImport(p); } catch (Exception ex) { errors.Add($"{p.OriginalName}: {ex.Message}"); }
        return errors;
    }
    public static BitmapImage ReadBitmap(string file, int width)
    {
        using var stream = File.OpenRead(file);
        var b = new BitmapImage();
        b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad;
        b.DecodePixelWidth = width; b.StreamSource = stream; b.EndInit(); b.Freeze();
        return b;
    }
    public void EnsureThumbnail(Photo p)
    {
        if (File.Exists(Thumb(p))) return;
        var b = ReadBitmap(Original(p), 480);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(b));
        var temp = Thumb(p) + ".tmp";
        using (var file = File.Create(temp)) encoder.Save(file);
        File.Move(temp, Thumb(p), true);
    }
    public void Transfer(Post from, Post to, IReadOnlyCollection<string> ids)
    {
        if (from == to) return;
        if (!Data.Posts.Contains(from) || !Data.Posts.Contains(to)) throw new InvalidOperationException("移动的图组已不在此图库中。");
        var selected = from.Images.Where(p => ids.Contains(p.Id)).ToList();
        if (selected.Any(p => p.Source != null || p.Deleting)) throw new IOException("请先完成所选图片的导入恢复。");
        var fromImages = from.Images.ToList(); var toImages = to.Images.ToList();
        var oldFromCover = from.Cover; var oldToCover = to.Cover;
        from.Images.RemoveAll(p => ids.Contains(p.Id));
        to.Images.AddRange(selected);
        if (!from.Images.Any(p => p.Id == from.Cover)) from.Cover = from.Images.FirstOrDefault()?.Id;
        to.Cover ??= to.Images.FirstOrDefault()?.Id;
        try { Save(); }
        catch { from.Images = fromImages; to.Images = toImages; from.Cover = oldFromCover; to.Cover = oldToCover; throw; }
    }
    public void Dispose() => libraryLock.Dispose();
}
