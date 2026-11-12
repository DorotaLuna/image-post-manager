using System.Windows.Media.Imaging;

namespace ImagePostManager;

// UI-thread owned LRU; only frozen bitmaps cross the worker boundary.
internal sealed class ThumbnailCache
{
    internal const long Capacity = 64 * 1024 * 1024;
    private long bytes;
    private readonly Dictionary<string, (BitmapSource Bitmap, LinkedListNode<string> Node, long Bytes)> entries = [];
    private readonly LinkedList<string> lru = [];
    private readonly Dictionary<string, Task<BitmapSource>> pending = [];
    private readonly SemaphoreSlim gate = new(2);
    internal long Bytes => bytes;
    internal int Decodes { get; private set; }
    internal BitmapSource? Get(string key)
    {
        if (!entries.TryGetValue(key, out var value)) return null;
        lru.Remove(value.Node); lru.AddFirst(value.Node); return value.Bitmap;
    }
    internal Task<BitmapSource> Load(string key, Func<BitmapSource> read)
    {
        if (Get(key) is { } hit) return Task.FromResult(hit);
        if (pending.TryGetValue(key, out var task)) return task;
        task = Decode(); pending.Add(key, task); return task;
        async Task<BitmapSource> Decode()
        {
            // Publish the in-flight task before even an immediately completed decode can remove it.
            await Task.Yield();
            await gate.WaitAsync();
            try
            {
                Decodes++;
                var bitmap = await Task.Run(read);
                long size = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
                if (size <= Capacity)
                {
                    while (bytes + size > Capacity && lru.Last is { } last)
                    { bytes -= entries[last.Value].Bytes; entries.Remove(last.Value); lru.RemoveLast(); }
                    entries[key] = (bitmap, lru.AddFirst(key), size); bytes += size;
                }
                return bitmap;
            }
            finally { gate.Release(); pending.Remove(key); }
        }
    }
}
