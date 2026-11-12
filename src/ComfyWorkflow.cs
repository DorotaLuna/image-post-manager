using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ImagePostManager;

internal static class ComfyWorkflow
{
    private const int MaxTextBytes = 16 * 1024 * 1024;
    internal static string Read(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (file.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("该图片不是 PNG，无法读取 ComfyUI 工作流。请使用包含工作流元数据的原始 PNG 图片。");
        while (file.Position < file.Length)
        {
            file.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header[4..]);
            if (length > file.Length - file.Position - 4) throw new InvalidDataException("PNG 文件不完整，无法读取工作流。");
            if (type == "IEND") break;
            if (type is not ("tEXt" or "iTXt" or "zTXt")) { file.Position += length + 4L; continue; }
            if (length > MaxTextBytes) throw new InvalidDataException("图片文本元数据过大（超过 16 MB）。");
            var data = new byte[(int)length]; file.ReadExactly(data); file.Position += 4; // CRC
            int end = Array.IndexOf(data, (byte)0);
            if (end < 0 || Encoding.ASCII.GetString(data, 0, end) != "workflow") continue;
            int offset = end + 1;
            bool compressed = false;
            if (type == "zTXt")
            {
                if (offset >= data.Length || data[offset++] != 0) throw InvalidMetadata();
                compressed = true;
            }
            else if (type == "iTXt")
            {
                if (offset + 2 > data.Length || data[offset] > 1 || data[offset + 1] != 0) throw InvalidMetadata();
                compressed = data[offset] == 1; offset += 2;
                for (int i = 0; i < 2; i++)
                {
                    int separator = Array.IndexOf(data, (byte)0, offset);
                    if (separator < 0) throw InvalidMetadata();
                    offset = separator + 1;
                }
            }
            byte[] text;
            if (compressed)
            {
                using var source = new MemoryStream(data, offset, data.Length - offset);
                using var zip = new ZLibStream(source, CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                byte[] buffer = new byte[8192]; int count;
                while ((count = zip.Read(buffer)) > 0)
                {
                    if (expanded.Length + count > MaxTextBytes) throw new InvalidDataException("解压后的工作流超过 16 MB。");
                    expanded.Write(buffer, 0, count);
                }
                text = expanded.ToArray();
            }
            else text = data[offset..];
            // ComfyUI writes UTF-8 JSON, including non-ASCII node names, into PNG text chunks.
            string json = new UTF8Encoding(false, true).GetString(text);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
                throw InvalidMetadata();
            return json; // Preserve subgraphs, custom-node fields and all other workflow data verbatim.
        }
        throw new InvalidDataException("该图片未包含 ComfyUI workflow 元数据。仅有 prompt 参数不能还原完整画布，请使用保存了工作流的原始 PNG 图片。");
    }
    private static InvalidDataException InvalidMetadata() => new("图片中的 ComfyUI 工作流格式无效。");
}
