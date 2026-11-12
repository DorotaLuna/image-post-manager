using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImagePostManager;

internal static class LibraryEnhancementTests
{
    private static void Fixture(string path, byte seed)
    {
        var bytes = new byte[8 * 8 * 4];
        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = seed; bytes[i + 1] = (byte)(255 - seed); bytes[i + 2] = (byte)(i % 251); bytes[i + 3] = 255; }
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, bytes, 8 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static bool Throws<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
    private static Catalog Read(string path) => JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path))!;

    public static void Run(string root, Action<bool, string> assert)
    {
        root = Path.GetFullPath(Path.Combine(root, "library-enhancements")); Directory.CreateDirectory(root);
        var libraryRoot = Path.Combine(root, "library");
        var legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "library.json"), "{\"Version\":1,\"Posts\":[]}");
        using (var old = new Library(legacy))
            assert(old.Data.ArchivedPosts.Count == 0 && old.Data.DeletedPhotos.Count == 0, "version-one indexes gain empty recovery lists");

        string batchRoot = Path.Combine(root, "batch-undo-library");
        string[] expected;
        using (var batch = new Library(batchRoot))
        {
            batch.RemoveOriginal = File.Delete;
            var group = batch.AddPost("batch");
            for (int i = 0; i < 4; i++) { var photo = new Photo { OriginalName = $"batch-{i}.png" }; group.Images.Add(photo); Fixture(batch.Original(photo), (byte)(i * 20)); }
            expected = group.Images.Select(p => p.Id).ToArray();
            group.Cover = expected[1]; batch.Data.Cover = expected[1]; batch.Save();
            var images = group.Images.ToArray();
            batch.DeletePhoto(group, images[1], "batch-test");
            batch.DeletePhoto(group, images[3], "batch-test");
            batch.DeletePhoto(group, images[0], "batch-test");
        }
        using (var batch = new Library(batchRoot))
        {
            var records = batch.LastDeletionBatch();
            assert(records.Count == 3, "batch undo survives reopening the library");
            foreach (var record in records) batch.RestoreDeletedPhoto(record.Photo.Id);
            var group = batch.Data.Posts.Single();
            assert(group.Images.Select(p => p.Id).SequenceEqual(expected), "batch undo restores original image order");
            assert(group.Cover == expected[1] && batch.Data.Cover == expected[1], "batch undo restores original covers");
        }

        string postId, firstId, secondId, originalHash;
        using (var library = new Library(libraryRoot))
        {
            var before = library.AddPost("before");
            var post = library.AddPost("恢复顺序"); postId = post.Id;
            var after = library.AddPost("after");
            var source = Path.Combine(root, "原始名字.png"); Fixture(source, 30);
            var first = library.Import(post, source); firstId = first.Id; originalHash = Library.Digest(library.Original(first));
            Fixture(source, 45); var second = library.Import(post, source); secondId = second.Id;
            library.Data.Cover = first.Id; library.SetFavorite([first.Id], true);
            library.UnlinkPost(post);
            assert(library.Data.Posts.SequenceEqual(new[] { before, after }) && library.Data.ArchivedPosts.Single() == post, "unlink archives entire post with its prior position");
            assert(post.Images.Select(p => p.Id).SequenceEqual(new[] { first.Id, second.Id }) && post.Cover == first.Id && first.IsFavorite, "archive keeps image order cover and favorites");
            assert(File.Exists(library.Original(first)) && File.Exists(library.Thumb(first)) && library.Data.Cover is null, "archive preserves files and clears active library cover");
            var duplicate = Path.Combine(root, "duplicate.png"); File.Copy(library.Original(first), duplicate);
            var match = library.FindDuplicate(duplicate);
            assert(match?.Post == post && match?.Photo == first && File.Exists(duplicate), "exact duplicate lookup includes archived photos without touching source");
            Fixture(duplicate, 96);
            assert(library.FindDuplicate(duplicate) is null, "different image bytes are not duplicates");
            library.RestorePost(post.Id);
            assert(library.Data.Posts.SequenceEqual(new[] { before, post, after }) && library.Data.Cover == first.Id, "restore post reinstates position and library cover");
            assert(library.Data.ArchivedPosts.Count == 0 && post.ArchivedAt is null, "restore clears the archived marker");
        }

        using (var library = new Library(libraryRoot))
        {
            var post = library.Data.Posts.Single(p => p.Id == postId);
            var first = post.Images.Single(p => p.Id == firstId); var second = post.Images.Single(p => p.Id == secondId);
            assert(first.IsFavorite && !second.IsFavorite, "favorite state persists across restarts");
            var trash = Path.Combine(root, "trash"); Directory.CreateDirectory(trash);
            library.RemoveOriginal = path =>
            {
                var disk = Read(Path.Combine(library.Root, "library.json"));
                var journal = disk.DeletedPhotos.Single(r => r.Photo.Id == first.Id);
                assert(journal.Pending && journal.RecoveryAvailable && disk.Posts.SelectMany(p => p.Images).Single(p => p.Id == first.Id).Deleting,
                    "verified recovery copy and deletion journal reach disk before recycle");
                assert(Library.Digest(Path.Combine(library.Root, "recovery", first.Id + first.Extension)) == originalHash, "recovery stores exact original bytes");
                File.Move(path, Path.Combine(trash, Path.GetFileName(path)), true);
            };
            library.DeletePhoto(post, first);
            var record = library.Data.DeletedPhotos.Single();
            assert(record.PostId == post.Id && record.PostTitle == post.Title && record.ImageIndex == 0 && !record.Pending, "deleted photo retains complete restore metadata");
            assert(!post.Images.Contains(first) && post.Cover == second.Id && library.Data.Cover is null, "deletion repairs active covers and removes live record");
            library.RestoreDeletedPhoto(first.Id);
            assert(post.Images[0] == first && post.Cover == first.Id && library.Data.Cover == first.Id && first.IsFavorite, "undo deletion restores position covers and favorite");
            assert(Library.Digest(library.Original(first)) == originalHash && library.Data.DeletedPhotos.Count == 0, "undo deletion restores exact bytes and consumes deleted entry");
            assert(File.Exists(Path.Combine(trash, Path.GetFileName(library.Original(first)))), "undo leaves the independent Windows recycle adapter copy intact");

            library.RemoveOriginal = _ => throw new IOException("denied");
            assert(Throws<IOException>(() => library.DeletePhoto(post, first)) && !first.Deleting && post.Images.Contains(first) && library.Data.DeletedPhotos.Count == 0,
                "denied recycle operation rolls back live deletion state");
            library.RemoveOriginal = File.Delete;
            library.DeletePhoto(post, second); library.UnlinkPost(post); library.RestoreDeletedPhoto(second.Id);
            assert(library.Data.ArchivedPosts.Single().Images.Any(p => p.Id == second.Id) && !library.Data.Posts.Contains(post), "restoring image into archived parent preserves its archive state");
            library.RestorePost(post.Id);
            library.DeletePhoto(post, second); library.Data.Posts.Remove(post); library.Save(); library.RestoreDeletedPhoto(second.Id);
            var recreated = library.Data.Posts.Single(p => p.Id == post.Id);
            assert(recreated.Title == post.Title && recreated.CreatedAt == post.CreatedAt && recreated.Images.Single().Id == second.Id,
                "image restoration recreates a missing parent with original metadata");
            // Reinstate the unrelated preserved first record for the export/history checks.
            recreated.Images.Insert(0, first); recreated.Cover = first.Id; library.Save();

            var exports = Path.Combine(root, "exports"); Directory.CreateDirectory(exports);
            var existing = Path.Combine(exports, "原始名字.png"); File.WriteAllText(existing, "keep-existing");
            var outputs = library.ExportPhotos(recreated.Images, exports);
            assert(outputs.Count == 2 && outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 && File.ReadAllText(existing) == "keep-existing", "export gives duplicate names unique suffixes without overwriting");
            assert(outputs.Select(Library.Digest).SequenceEqual(recreated.Images.Select(p => Library.Digest(library.Original(p)))), "export preserves exact original bytes and selection order");
            assert(File.Exists(library.Original(first)) && File.Exists(library.Original(second)), "export keeps managed originals");
            first.OriginalName = "..\\..\\CON.png"; var sanitized = library.ExportPhotos([first], exports).Single();
            assert(Path.GetDirectoryName(sanitized) == exports && Path.GetFileName(sanitized) == "_CON.png", "export strips directories and sanitizes reserved Windows device names");
            assert(Throws<IOException>(() => library.ExportPhotos([first], Path.Combine(library.Root, "originals", "export"))), "export rejects internal library directories");
            assert(Throws<IOException>(() => library.ExportPhotos([new Photo()], exports)), "export rejects photos outside this library");

            var count = library.Data.Posts.Count;
            Directory.CreateDirectory(Path.Combine(library.Root, "library.json.tmp"));
            assert(Throws<UnauthorizedAccessException>(() => library.AddPost("failed")) && library.Data.Posts.Count == count, "failed index write rolls back newly added post");
            assert(Throws<UnauthorizedAccessException>(() => library.SetFavorite([first.Id], false)) && first.IsFavorite, "failed index write rolls back favorite changes");
            Directory.Delete(Path.Combine(library.Root, "library.json.tmp"));
            library.Save();
            assert(File.Exists(Path.Combine(library.Root, "library.json.bak")) && Read(Path.Combine(library.Root, "library.json.bak")).Version == 1,
                "atomic save leaves a valid previous-index backup");
        }

        var historyRoot = Path.Combine(root, "history-library");
        using (var library = new Library(historyRoot))
        {
            library.RemoveOriginal = File.Delete;
            var post = library.AddPost("history");
            var file = Path.Combine(root, "history-one.png"); Fixture(file, 70); var one = library.Import(post, file);
            library.Save();
            var snapshot = library.GetHistoryFiles().First(path => Read(path).Posts.SelectMany(p => p.Images).Any(p => p.Id == one.Id));
            file = Path.Combine(root, "history-two.png"); Fixture(file, 90); var two = library.Import(post, file);
            var newPost = library.AddPost("newer post");
            file = Path.Combine(root, "history-three.png"); Fixture(file, 110); var three = library.Import(newPost, file);
            library.DeletePhoto(post, one);
            var diagnostics = library.RestoreHistory(snapshot);
            assert(diagnostics.Count == 0 && library.Data.Posts.Single(p => p.Id == post.Id).Images.Single().Id == one.Id && File.Exists(library.Original(one)),
                "history restores grouping and recovers deleted original from local copy");
            assert(library.Data.ArchivedPosts.SelectMany(p => p.Images).Any(p => p.Id == two.Id) && library.Data.ArchivedPosts.SelectMany(p => p.Images).Any(p => p.Id == three.Id),
                "history retains newer photos and posts in recoverable archives");
            assert(File.Exists(library.Original(two)) && File.Exists(library.Original(three)) && library.Data.DeletedPhotos.All(r => r.Photo.Id != one.Id),
                "history keeps newer files and prevents duplicate deleted entries for restored photos");
            assert(library.GetHistoryFiles().Any(path => Read(path).Posts.Any(p => p.Id == newPost.Id)), "history preserves the pre-restore current index as a snapshot");
            File.Delete(library.Original(one));
            File.Delete(Path.Combine(library.Root, "recovery", one.Id + one.Extension));
            library.Save();
            var missingSnapshot = library.GetHistoryFiles()[0];
            assert(library.RestoreHistory(missingSnapshot).Any(s => s.Contains(one.Id)), "history restoration reports missing originals explicitly");
            assert(Throws<IOException>(() => library.RestoreHistory(Path.Combine(library.Root, "library.json"))), "history restoration rejects paths outside its history snapshots");
            var live = library.Data.Posts.SelectMany(p => p.Images).First(); live.Source = Path.Combine(root, "pending.png");
            assert(Throws<IOException>(() => library.RestoreHistory(missingSnapshot)), "history refuses to replace pending import journals");
            live.Source = null;
            for (int i = 0; i < 35; i++) { library.Data.Posts[0].Title = "save " + i; library.Save(); }
            assert(library.GetHistoryFiles().Count == 30, "history retention is bounded to thirty indexes");
        }

        var crashRoot = Path.Combine(root, "crash-library"); string crashId;
        using (var library = new Library(crashRoot))
        {
            var post = library.AddPost("crash"); var file = Path.Combine(root, "crash.png"); Fixture(file, 123);
            var photo = library.Import(post, file); crashId = photo.Id; photo.Deleting = true;
            File.Copy(library.Original(photo), Path.Combine(library.Root, "recovery", photo.Id + photo.Extension));
            library.Data.DeletedPhotos.Add(new DeletedPhotoRecord { Photo = photo, PostId = post.Id, PostTitle = post.Title,
                PostCreatedAt = post.CreatedAt, Pending = true, RecoveryAvailable = true, PostCover = photo.Id });
            library.Save(); File.Delete(library.Original(photo));
        }
        using (var library = new Library(crashRoot))
        {
            assert(library.Recover().Count == 0 && library.Data.Posts.Single().Images.Count == 0 && !library.Data.DeletedPhotos.Single().Pending,
                "reopen completes deletion after recycle before index commit");
            library.RestoreDeletedPhoto(crashId);
            assert(File.Exists(library.Original(library.Data.Posts.Single().Images.Single())), "recovered deletion remains reversible after restart");
        }

        using (var library = new Library(Path.Combine(root, "legacy-deletion")))
        {
            library.RemoveOriginal = File.Delete;
            var post = library.AddPost("old deletion journal");
            var file = Path.Combine(root, "old-pending.png"); Fixture(file, 130); var photo = library.Import(post, file);
            photo.Deleting = true; library.Save();
            assert(library.Recover().Count == 0 && library.Data.DeletedPhotos.Single().RecoveryAvailable, "older deletion journal with original gains a verified recovery copy");
            library.RestoreDeletedPhoto(photo.Id);
            assert(File.Exists(library.Original(photo)) && post.Images.Contains(photo), "older pending deletion can be undone after upgrading");
            file = Path.Combine(root, "old-already-removed.png"); Fixture(file, 140); var removed = library.Import(post, file);
            removed.Deleting = true; library.Save(); File.Delete(library.Original(removed));
            assert(library.Recover().Count == 0 && !post.Images.Contains(removed) && !library.Data.DeletedPhotos.Single().RecoveryAvailable,
                "older deletion journal already recycled still completes without inventing missing recovery bytes");
            assert(Throws<IOException>(() => library.RestoreDeletedPhoto(removed.Id)) && library.Data.DeletedPhotos.Count == 1,
                "unavailable old recovery copy keeps deleted metadata and reports failure");
        }

        var invalid = Path.Combine(root, "invalid"); Directory.CreateDirectory(invalid);
        File.WriteAllText(Path.Combine(invalid, "library.json"), JsonSerializer.Serialize(new Catalog {
            ArchivedPosts = [new Post { Images = [new Photo { Id = "../../escape" }] }] }));
        assert(Throws<InvalidDataException>(() => { using var ignored = new Library(invalid); }), "archived photo paths receive the same traversal validation as active photos");
    }
}
