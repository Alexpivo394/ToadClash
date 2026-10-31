using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Alexpivo394.Revit.Clash.Render;

/// <summary>Кодировщик PNG без сторонних библиотек: RGB 8 бит, фильтр Up у всех строк, один блок IDAT.</summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Кодирует изображение. <paramref name="rgb"/> — строки сверху вниз, по три байта на пиксель.</summary>
    /// <exception cref="ArgumentException">Длина буфера не равна width × height × 3.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        var stride = width * 3;
        if (rgb.Length != stride * height)
            throw new ArgumentException($"Длина буфера {rgb.Length}, ожидалось {stride * height}.", nameof(rgb));

        var filtered = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (stride + 1);
            filtered[row] = 2;
            var current = rgb.Slice(y * stride, stride);
            if (y == 0)
            {
                current.CopyTo(filtered.AsSpan(row + 1));
                continue;
            }

            var previous = rgb.Slice((y - 1) * stride, stride);
            for (var x = 0; x < stride; x++)
                filtered[row + 1 + x] = (byte)(current[x] - previous[x]);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(filtered);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;

        using var output = new MemoryStream((int)compressed.Length + 64);
        output.Write(Signature);
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData.AsSpan(4));
        output.Write(typeAndData);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.HashToUInt32(typeAndData));
        output.Write(crc);
    }
}
