using System.IO.Compression;
using Alexpivo394.Revit.Clash.Geometry;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

/// <summary>Повреждённые слепки: огромные счётчики, ложные размеры сеток, NaN — InvalidDataException без больших выделений.</summary>
public sealed class FormatHardeningTests
{
    private const long AllocationLimit = 64L << 20;
    private const int HugeCount = 1 << 26;

    private static readonly ModelHeader Header = new("m", "m.rvt", 2022, DateTimeOffset.UnixEpoch, true, "3D", 0, 0, 0);

    // Пустая модель: префикс, число элементов (int), длина блока сеток (long), число строк описи (int).
    private static byte[] EmptyModel()
    {
        using var stream = new MemoryStream();
        ModelGeometryFile.Write(stream, new ModelGeometry(Header, [], []));
        return stream.ToArray();
    }

    private static byte[] WithHugeElementCount()
    {
        var file = EmptyModel();
        var prefix = file.Length - 16;
        return [.. file.AsSpan(0, prefix), .. BitConverter.GetBytes(HugeCount)];
    }

    private static byte[] WithHugeInventoryCount()
    {
        var file = EmptyModel();
        return [.. file.AsSpan(0, file.Length - 4), .. BitConverter.GetBytes(HugeCount)];
    }

    [Test]
    public void HugeCountsInTinyFile_InvalidDataWithoutBigAllocation()
    {
        ShouldFailCheaply(() => ModelGeometryFile.Read(new MemoryStream(WithHugeElementCount())));
        ShouldFailCheaply(() => ModelGeometryFile.ReadIndex(new MemoryStream(WithHugeElementCount())));
        ShouldFailCheaply(() => ModelGeometryFile.Read(new MemoryStream(WithHugeInventoryCount())));
        ShouldFailCheaply(() => ModelGeometryFile.ReadIndex(new MemoryStream(WithHugeInventoryCount())));
        ShouldFailCheaply(() => ModelParametersFile.Read(new MemoryStream(Parameters(writer =>
        {
            writer.Write(1);
            writer.Write(HugeCount);
        }))));
    }

    [Test]
    public void MeshCountsBeyondCompressedLength_InvalidData()
    {
        var mesh = Deflate(writer =>
        {
            writer.Write(HugeCount / 3);
            writer.Write(0f);
        });

        ShouldFailCheaply(() => ReadMesh(mesh));
    }

    [Test]
    public void NaNPosition_InvalidData()
    {
        var mesh = Deflate(writer =>
        {
            writer.Write(3);
            foreach (var value in new[] { 0f, 0, 0, 1, 0, 0, float.NaN, 1, 0 }) writer.Write(value);
            writer.Write(3);
            foreach (var index in new[] { 0, 1, 2 }) writer.Write(index);
        });

        Should.Throw<InvalidDataException>(() => ReadMesh(mesh));
    }

    [Test]
    public void TooManyConnections_RejectedOnWrite()
    {
        var connected = Enumerable.Range(0, 4097).Select(i => $"c{i}").ToArray();
        var element = new ElementGeometry(new ElementInfo("u", 1, "OST_PipeCurves", null, null, null, null, connected),
            new ElementMesh([], []), new Aabb(default, default), 0);

        Should.Throw<ArgumentException>(() => ModelGeometryFile.Write(new MemoryStream(), new ModelGeometry(Header, [element], [])));
    }

    private static ElementMesh ReadMesh(byte[] compressed)
    {
        var entry = new ModelIndexEntry(new ElementInfo("u", 1, "OST_Walls", null, null, null, null, []), new Aabb(default, default), 0, 1, 0,
            compressed.Length);
        return ModelGeometryFile.ReadMesh(new MemoryStream(compressed), entry);
    }

    private static void ShouldFailCheaply(Action read)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        Should.Throw<InvalidDataException>(read);
        (GC.GetAllocatedBytesForCurrentThread() - before).ShouldBeLessThan(AllocationLimit);
    }

    private static byte[] Deflate(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, true))
        using (var writer = new BinaryWriter(deflate))
            write(writer);
        return buffer.ToArray();
    }

    private static byte[] Parameters(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        buffer.Write(BitConverter.GetBytes(ModelParametersFile.Magic));
        var payload = Deflate(write);
        buffer.Write(payload);
        return buffer.ToArray();
    }
}
