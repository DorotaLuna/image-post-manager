using System.IO;
using System.Text.Json;

namespace ImagePostManager;

public sealed partial class Library
{
    private const int HistoryLimit = 30;
    private IEnumerable<Post> AllPosts() => Data.Posts.Concat(Data.ArchivedPosts);
    private string RecoveryFile(Photo photo) => Path.Combine(Root, "recovery", photo.Id + photo.Extension);

    private static void ValidatePhoto(Photo photo)
    {
        if (photo is null || !Guid.TryParseExact(photo.Id, "N", out _) || photo.Extension is not (".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("图库索引包含无效的图片路径。");
    }

    private static void ValidateCatalog(Catalog catalog)
    {
        if (catalog.Version != 1) throw new InvalidDataException("不支持此图库版本。");
        if (catalog.Posts is null || catalog.ArchivedPosts is null || catalog.DeletedPhotos is null)
            throw new InvalidDataException("图库索引包含空的列表。");
        var postIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var photos = new Dictionary<string, Photo>(StringComparer.OrdinalIgnoreCase);
        foreach (var post in catalog.Posts.Concat(catalog.ArchivedPosts))
        {
            if (post is null || string.IsNullOrWhiteSpace(post.Id) || !postIds.Add(post.Id) || post.Images is null)
                throw new InvalidDataException("图库索引包含无效或重复的图组。");
            foreach (var photo in post.Images)
            {
                ValidatePhoto(photo);
                if (!photos.TryAdd(photo.Id, photo)) throw new InvalidDataException("图库索引包含重复的图片。");
            }
        }
        var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in catalog.DeletedPhotos)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.PostId)) throw new InvalidDataException("删除恢复记录无效。");
            ValidatePhoto(record.Photo);
            if (!deletedIds.Add(record.Photo.Id)) throw new InvalidDataException("删除恢复记录重复。");
            if (photos.TryGetValue(record.Photo.Id, out var live) &&
                (!record.Pending || !live.Deleting || live.Extension != record.Photo.Extension))
                throw new InvalidDataException("删除恢复记录与图库图片冲突。");
            if (record.Pending && !photos.ContainsKey(record.Photo.Id)) throw new InvalidDataException("删除操作缺少原图组记录。");
        }
    }

    private static bool HasPending(Catalog catalog) =>
        catalog.Posts.Concat(catalog.ArchivedPosts).SelectMany(p => p.Images).Any(p => p.Source is not null || p.Deleting) ||
        catalog.DeletedPhotos.Any(r => r.Pending || r.Photo.Source is not null || r.Photo.Deleting);

    private static bool IsWithin(string path, string directory)
    {
        path = Path.GetFullPath(path); directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public Post RestorePost(string postId)
    {
        var post = Data.ArchivedPosts.SingleOrDefault(p => p.Id == postId) ?? throw new InvalidOperationException("找不到已移除的图组。");
        if (post.Images.Any(p => p.Source is not null || p.Deleting)) throw new IOException("请先完成该图组未完成的文件操作。");
        int archivedIndex = Data.ArchivedPosts.IndexOf(post);
        int index = Math.Clamp(post.ArchivedIndex ?? Data.Posts.Count, 0, Data.Posts.Count);
        var oldCover = Data.Cover; var archivedAt = post.ArchivedAt; var originalIndex = post.ArchivedIndex;
        var archivedCover = post.ArchivedLibraryCover;
        Data.ArchivedPosts.Remove(post); Data.Posts.Insert(index, post);
        if (Data.Cover is null && post.Images.Any(p => p.Id == archivedCover)) Data.Cover = archivedCover;
        post.ArchivedAt = null; post.ArchivedIndex = null; post.ArchivedLibraryCover = null;
        try { Save(); }
        catch
        {
            Data.Posts.Remove(post); Data.ArchivedPosts.Insert(archivedIndex, post); Data.Cover = oldCover;
            post.ArchivedAt = archivedAt; post.ArchivedIndex = originalIndex; post.ArchivedLibraryCover = archivedCover; throw;
        }
        return post;
    }

    private DeletedPhotoRecord CreateDeletionRecord(Post post, Photo photo) => new()
    {
        Photo = photo, PostId = post.Id, PostTitle = post.Title, PostCreatedAt = post.CreatedAt,
        PostIndex = Data.Posts.IndexOf(post), ImageIndex = post.Images.IndexOf(photo),
        PostCover = post.Cover, LibraryCover = Data.Cover, Pending = true
    };

    private static void CopyVerified(string source, string destination, string? expectedHash = null)
    {
        var hash = Digest(source);
        if (expectedHash is not null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("原图内容与索引校验值不一致，文件已保留。");
        if (File.Exists(destination))
        {
            if (Digest(destination).Equals(hash, StringComparison.OrdinalIgnoreCase)) return;
            throw new IOException("恢复位置已存在内容不同的文件，文件已保留。");
        }
        var temp = destination + ".copying-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            if (!Digest(temp).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复副本校验失败，原文件已保留。");
            File.Move(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void EnsureRecoveryCopy(Photo photo)
    {
        CopyVerified(Original(photo), RecoveryFile(photo), photo.Hash);
        photo.Hash ??= Digest(RecoveryFile(photo));
    }

    internal List<DeletedPhotoRecord> LastDeletionBatch()
    {
        var last = Data.DeletedPhotos.LastOrDefault();
        if (last == null) return [];
        // Reverse journal order reverses index shifts and restores the original covers.
        return Data.DeletedPhotos.Where(r => last.DeleteBatchId == null ? r == last : r.DeleteBatchId == last.DeleteBatchId).Reverse().ToList();
    }

    public Photo RestoreDeletedPhoto(string photoId)
    {
        var record = Data.DeletedPhotos.SingleOrDefault(r => r.Photo.Id == photoId) ?? throw new InvalidOperationException("找不到已删除的图片。");
        if (record.Pending || record.Photo.Deleting || record.Photo.Source is not null)
            throw new IOException("请先重新打开图库，完成未完成的文件操作。");
        var photo = record.Photo;
        var original = Original(photo);
        if (!File.Exists(original))
        {
            if (!File.Exists(RecoveryFile(photo))) throw new IOException("本地恢复副本不存在。请先从 Windows 回收站还原原图，再重试恢复记录。");
            CopyVerified(RecoveryFile(photo), original, photo.Hash);
        }
        else if (photo.Hash is not null && !Digest(original).Equals(photo.Hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("原图位置的文件校验失败，恢复已停止，现有文件已保留。");
        var post = AllPosts().SingleOrDefault(p => p.Id == record.PostId);
        bool created = post is null;
        post ??= new Post { Id = record.PostId, Title = record.PostTitle, CreatedAt = record.PostCreatedAt };
        if (post.Images.Any(p => p.Source is not null || p.Deleting)) throw new IOException("请先完成该图组未完成的文件操作。");
        var oldPostCover = post.Cover; var oldCover = Data.Cover;
        if (created) Data.Posts.Insert(Math.Clamp(record.PostIndex, 0, Data.Posts.Count), post);
        post.Images.Insert(Math.Clamp(record.ImageIndex, 0, post.Images.Count), photo);
        if (post.Cover is null || record.PostCover == photo.Id) post.Cover = photo.Id;
        if (Data.Posts.Contains(post) && Data.Cover is null && record.LibraryCover == photo.Id) Data.Cover = photo.Id;
        int recordIndex = Data.DeletedPhotos.IndexOf(record); Data.DeletedPhotos.Remove(record);
        try { Save(); }
        catch
        {
            post.Images.Remove(photo); post.Cover = oldPostCover; Data.Cover = oldCover;
            if (created) Data.Posts.Remove(post);
            Data.DeletedPhotos.Insert(recordIndex, record); throw;
        }
        // A thumbnail is a disposable cache; restoration must still succeed when cache writing fails.
        try { EnsureThumbnail(photo); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return photo;
    }

    public void SetFavorite(IEnumerable<string> ids, bool value)
    {
        var selected = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var photos = AllPosts().SelectMany(p => p.Images).Where(p => selected.Contains(p.Id)).ToList();
        var previous = photos.Select(p => p.IsFavorite).ToArray();
        for (int i = 0; i < photos.Count; i++) photos[i].IsFavorite = value;
        try { Save(); } catch { for (int i = 0; i < photos.Count; i++) photos[i].IsFavorite = previous[i]; throw; }
    }

    public (Post Post, Photo Photo)? FindDuplicate(string source)
    {
        source = Path.GetFullPath(source);
        var sourceSize = new FileInfo(source).Length;
        var hash = Digest(source);
        foreach (var post in AllPosts())
            foreach (var photo in post.Images.Where(p => !p.Deleting && p.Source is null))
            {
                var path = Original(photo);
                if (!File.Exists(path) || new FileInfo(path).Length != sourceSize) continue;
                if (photo.Hash is not null && !photo.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)) continue;
                if (Digest(path).Equals(hash, StringComparison.OrdinalIgnoreCase)) return (post, photo);
            }
        return null;
    }

    public List<string> ExportPhotos(IEnumerable<Photo> photos, string directory)
    {
        directory = Path.GetFullPath(directory);
        if (IsWithin(directory, Root) || IsWithin(PhysicalDirectory(directory), PhysicalDirectory(Root)))
            throw new IOException("请选择图库文件夹之外的导出位置。");
        var owned = AllPosts().SelectMany(p => p.Images).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var selected = photos.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var photo in selected)
            if (!owned.TryGetValue(photo.Id, out var stored) || stored.Extension != photo.Extension || stored.Source is not null || stored.Deleting)
                throw new IOException("所选图片已不在图库中，或有未完成的文件操作。");
        Directory.CreateDirectory(directory);
        List<string> outputs = [];
        try
        {
            foreach (var selection in selected)
            {
                var photo = owned[selection.Id];
                var name = ExportName(photo);
                string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
                for (int suffix = 1; ; suffix++)
                {
                    var path = Path.Combine(directory, suffix == 1 ? name : $"{stem} ({suffix}){extension}");
                    FileStream output;
                    try { output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                    catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { continue; }
                    outputs.Add(path);
                    using (output)
                    using (var input = File.OpenRead(Original(photo))) { input.CopyTo(output); output.Flush(true); }
                    if (Digest(path) != Digest(Original(photo))) throw new IOException("导出文件校验失败，原图已保留。");
                    break;
                }
            }
            return outputs;
        }
        catch
        {
            foreach (var path in outputs) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string PhysicalDirectory(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                current = directory.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return Path.GetFullPath(current);
    }

    private static string ExportName(Photo photo)
    {
        var name = Path.GetFileName(photo.OriginalName ?? "");
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = name.Trim().TrimEnd('.', ' ');
        var stem = Path.GetFileNameWithoutExtension(name).TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(stem)) stem = "image";
        if (stem.Length > 160) stem = stem[..160].TrimEnd('.', ' ');
        var device = stem.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && "123456789¹²³".Contains(device[3]))) stem = "_" + stem;
        return stem + photo.Extension;
    }

    public IReadOnlyList<string> GetHistoryFiles() => Directory.GetFiles(Path.Combine(Root, "history"), "*.json")
        .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();

    private void SnapshotStableIndex(string index)
    {
        // Import/delete journal transitions are useful for recovery, but are never offered as history restore points.
        var previous = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(index)) ?? throw new InvalidDataException("图库索引为空。");
        ValidateCatalog(previous);
        if (HasPending(previous)) return;
        WriteHistoryCopy(index);
    }

    private string WriteHistoryCopy(string source)
    {
        var path = Path.Combine(Root, "history", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var temp = path + ".tmp";
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        foreach (var old in GetHistoryFiles().Skip(HistoryLimit)) File.Delete(old);
        return path;
    }

    public List<string> RestoreHistory(string path)
    {
        if (HasPending(Data)) throw new IOException("图库有未完成的导入或删除操作，请先重新打开图库完成恢复。");
        path = Path.GetFullPath(path);
        if (!GetHistoryFiles().Contains(path, StringComparer.OrdinalIgnoreCase)) throw new IOException("只能恢复此图库历史记录中的索引。");
        var candidate = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path)) ?? throw new InvalidDataException("历史索引为空。");
        ValidateCatalog(candidate);
        if (HasPending(candidate)) throw new IOException("此历史记录包含未完成的文件操作，不能恢复。");
        PreserveCurrentRecords(candidate);
        ValidateCatalog(candidate);
        var current = Data;
        // Explicitly persist the current in-memory state before switching, even if a caller has unsaved edits.
        Save();
        WriteHistoryCopy(Path.Combine(Root, "library.json"));
        foreach (var photo in candidate.Posts.Concat(candidate.ArchivedPosts).SelectMany(p => p.Images))
            if (!File.Exists(Original(photo)) && File.Exists(RecoveryFile(photo))) CopyVerified(RecoveryFile(photo), Original(photo), photo.Hash);
        Data = candidate;
        try { Save(); } catch { Data = current; throw; }
        return candidate.Posts.Concat(candidate.ArchivedPosts).SelectMany(p => p.Images).Where(p => !File.Exists(Original(p)))
            .Select(p => $"原图缺失：{p.OriginalName}（{p.Id}）").ToList();
    }

    private void PreserveCurrentRecords(Catalog candidate)
    {
        var targetPosts = candidate.Posts.Concat(candidate.ArchivedPosts).ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var targetImages = targetPosts.Values.SelectMany(p => p.Images).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateDeleted = candidate.DeletedPhotos.ToDictionary(r => r.Photo.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var current in AllPosts())
        {
            // Photos absent from the selected snapshot remain discoverable in an archived group.
            var extra = current.Images.Where(p => !targetImages.Contains(p.Id) && !candidateDeleted.ContainsKey(p.Id)).ToList();
            if (!targetPosts.ContainsKey(current.Id) && !candidateDeleted.Values.Any(r => r.PostId == current.Id))
            {
                var preserved = Clone(current);
                preserved.Images = extra; preserved.ArchivedAt ??= DateTime.Now;
                preserved.ArchivedIndex ??= Data.Posts.IndexOf(current);
                if (!extra.Any(p => p.Id == preserved.Cover)) preserved.Cover = extra.FirstOrDefault()?.Id;
                candidate.ArchivedPosts.Add(preserved); targetPosts.Add(preserved.Id, preserved);
            }
            else if (extra.Count > 0)
            {
                var preserved = new Post { Title = current.DisplayTitle + "（历史恢复前保留）", CreatedAt = current.CreatedAt,
                    Images = extra.Select(Clone).ToList(), ArchivedAt = DateTime.Now,
                    Cover = extra.Any(p => p.Id == current.Cover) ? current.Cover : extra[0].Id };
                candidate.ArchivedPosts.Add(preserved);
            }
            foreach (var photo in extra) targetImages.Add(photo.Id);
        }
        // A historical deletion can hide a current original, but it must retain its durable bytes.
        foreach (var record in candidate.DeletedPhotos)
        {
            if (File.Exists(Original(record.Photo)))
            {
                EnsureRecoveryCopy(record.Photo); record.RecoveryAvailable = true;
            }
        }
        foreach (var record in Data.DeletedPhotos)
            if (!targetImages.Contains(record.Photo.Id) && !candidateDeleted.ContainsKey(record.Photo.Id)) candidate.DeletedPhotos.Add(Clone(record));
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
