using System.Buffers.Binary;
using Alexpivo394.Revit.Clash.Render;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Render;

public sealed class PngTests
{
    [Test]
    public void Encode_ValidSignatureChunksAndCrc()
    {
        const int width = 640, height = 400;
        var rgb = new byte[width * height * 3];
        var random = new Random(7);
        random.NextBytes(rgb);

        var png = Png.Encode(rgb, width, height);

        png.AsSpan(0, 8).ToArray().ShouldBe(PngTestReader.Signature);
        var chunks = PngTestReader.Chunks(png);
        chunks.Select(c => c.Type).ShouldBe(["IHDR", "IDAT", "IEND"]);
        chunks.ShouldAllBe(c => c.CrcOk);
        var header = chunks[0].Data;
        BinaryPrimitives.ReadInt32BigEndian(header).ShouldBe(width);
        BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)).ShouldBe(height);
        header[8].ShouldBe((byte)8);
        header[9].ShouldBe((byte)2);
        var decoded = PngTestReader.Decode(png);
        decoded.Rgb.ShouldBe(rgb);
    }

    [Test]
    public void Encode_WrongBufferLength_Throws()
    {
        Should.Throw<ArgumentException>(() => Png.Encode(new byte[10], 4, 4));
    }
}
