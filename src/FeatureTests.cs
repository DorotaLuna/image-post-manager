using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace ImagePostManager;

internal static class FeatureTests
{
    internal static void CheckWindowColors(Window window, bool light, Action<bool, string> assert)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // These DWM color attributes support Set only, not Get (E_INVALIDARG).
        assert(WindowAppearance.Apply(window, light), $"DWM accepts exact border and caption colors ({(light ? "light" : "dark")})");
    }

    internal static void Run(string root, string? sample, Action<bool, string> assert)
    {
        assert(SystemTheme.Resolve("system", true) && !SystemTheme.Resolve("system", false), "system theme resolves both OS modes");
        assert(SystemTheme.Resolve("light", false) && !SystemTheme.Resolve("dark", true), "explicit theme overrides OS mode");
        const string json = "{\"nodes\":[{\"id\":1,\"title\":\"中文节点 🖼\"}],\"links\":[],\"definitions\":{\"subgraphs\":[{}]},\"extra\":{\"custom\":true}}";
        string path = Path.Combine(root, "metadata.png");
        foreach (var type in new[] { "tEXt", "zTXt", "iTXt", "iTXt-compressed" })
        {
            WritePng(path, type, "workflow", json);
            string hash = Library.Digest(path);
            assert(ComfyWorkflow.Read(path) == json, $"{type} preserves full workflow including Unicode and subgraphs");
            assert(Library.Digest(path) == hash, "metadata read preserves original bytes");
        }
        WritePng(path, "tEXt", "prompt", "{\"1\":{\"class_type\":\"KSampler\"}}");
        Reject(path, "API prompt is not mistaken for full workflow", assert);
        WritePng(path, "tEXt", "workflow", "{bad JSON");
        Reject(path, "invalid workflow JSON is rejected", assert);
        WritePng(path, "tEXt", "workflow", "{\"nodes\":{}}");
        Reject(path, "invalid workflow shape is rejected", assert);
        WritePng(path, "tEXt", "workflow", json);
        using (var file = File.OpenWrite(path)) file.SetLength(24);
        Reject(path, "truncated PNG is rejected", assert);
        File.WriteAllText(path, "not a PNG");
        Reject(path, "unsupported file is rejected", assert);
        WritePng(path, "zTXt", "workflow", new string(' ', 16 * 1024 * 1024 + 1));
        Reject(path, "compressed metadata expansion is bounded", assert);
        if (sample != null)
        {
            string hash = Library.Digest(sample);
            string workflow = ComfyWorkflow.Read(sample);
            using var document = JsonDocument.Parse(workflow);
            assert(document.RootElement.GetProperty("nodes").GetArrayLength() > 0, "supplied real ComfyUI image yields workflow nodes");
            assert(Library.Digest(sample) == hash, "supplied real image remains unchanged");
            File.WriteAllText(Path.Combine(root, "sample-workflow.json"), workflow, new UTF8Encoding(false));
        }
    }

    private static void Reject(string path, string label, Action<bool, string> assert)
    {
        bool rejected = false;
        try { ComfyWorkflow.Read(path); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or EndOfStreamException) { rejected = true; }
        assert(rejected, label);
    }

    private static void WritePng(string path, string type, string key, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        bool compressed = type is "zTXt" or "iTXt-compressed";
        if (compressed)
        {
            using var output = new MemoryStream();
            using (var zip = new ZLibStream(output, CompressionMode.Compress, true)) zip.Write(payload);
            payload = output.ToArray();
        }
        using var metadata = new MemoryStream();
        metadata.Write(Encoding.ASCII.GetBytes(key)); metadata.WriteByte(0);
        if (type == "zTXt") metadata.WriteByte(0);
        if (type.StartsWith("iTXt")) metadata.Write(new byte[] { (byte)(compressed ? 1 : 0), 0, 0, 0 });
        metadata.Write(payload);
        // A valid 1x1 PNG; insert text before IEND so tests cover real chunk boundaries.
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9foAAAAASUVORK5CYII=");
        using var file = File.Create(path);
        file.Write(png.AsSpan(0, png.Length - 12));
        byte[] data = metadata.ToArray();
        Span<byte> number = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(number, data.Length); file.Write(number);
        byte[] chunkType = Encoding.ASCII.GetBytes(type[..4]); file.Write(chunkType); file.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte b in chunkType.Concat(data))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc); file.Write(number);
        file.Write(png.AsSpan(png.Length - 12));
    }
}
