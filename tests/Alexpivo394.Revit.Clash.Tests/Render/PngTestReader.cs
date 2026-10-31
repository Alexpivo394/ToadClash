using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Alexpivo394.Revit.Clash.Tests.Render;

/// <summary>Разбор PNG, который пишет Render: блоки с проверкой CRC и обратный фильтр Up.</summary>
public static class PngTestReader
{
    public static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public sealed record Chunk(string Type, byte[] Data, bool CrcOk);

    public static List<Chunk> Chunks(byte[] png)
    {
        var chunks = new List<Chunk>();
        var offset = Signature.Length;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var typeAndData = png.AsSpan(offset + 4, 4 + length);
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            chunks.Add(new Chunk(Encoding.ASCII.GetString(typeAndData[..4]), typeAndData[4..].ToArray(), Crc32.HashToUInt32(typeAndData) == crc));
            offset += 12 + length;
        }

        return chunks;
    }

    /// <summary>Ширина, высота и RGB по строкам.</summary>
    public static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
    {
        var chunks = Chunks(png);
        var header = chunks.Single(c => c.Type == "IHDR").Data;
        var width = BinaryPrimitives.ReadInt32BigEndian(header);
        var height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        using var zlib = new ZLibStream(new MemoryStream(chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray()), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var filtered = raw.ToArray();
        var stride = width * 3;
        var rgb = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (stride + 1)];
            if (filter != 2) throw new InvalidDataException($"Строка {y}: фильтр {filter}, ожидался Up.");
            for (var x = 0; x < stride; x++)
            {
                var up = y == 0 ? 0 : rgb[(y - 1) * stride + x];
                rgb[y * stride + x] = (byte)(filtered[y * (stride + 1) + 1 + x] + up);
            }
        }

        return (width, height, rgb);
    }
}
