using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

public sealed class ModelGeometryFileTests
{
    private static ModelGeometry Sample()
    {
        var header = new ModelHeader("arch", @"C:\models\arch.rvt", 2025,
            new DateTimeOffset(2026, 10, 26, 12, 30, 0, TimeSpan.FromHours(3)), true, "{3D}", 1000.5, -2000.25, 3.75);
        var triangle = new ElementMesh([0, 0, 0, 100, 0, 0, 0, 100, 50], [0, 1, 2]);
        var quad = new ElementMesh([0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], [0, 1, 2, 0, 2, 3]);
        var empty = new ElementMesh([], []);

        ElementGeometry Make(ElementInfo info, ElementMesh mesh, ulong fp) =>
            new(info, mesh, Aabb.FromPositions(mesh.Positions), fp);

        var elements = new[]
        {
            Make(new ElementInfo("uid-1", 101, "OST_Walls", "Стена", "Базовая 200", "Уровень 1", "Workset1", ["uid-2", "uid-3"]), triangle, 0xDEADBEEFCAFEBABEUL),
            Make(new ElementInfo("uid-2", 5_000_000_000, "OST_PipeCurves", null, null, null, null, [], "uid-1"), quad, 42UL),
            Make(new ElementInfo("uid-3", 103, "OST_Floors", "Floor", "Generic", "L2", null, []), empty, 0UL),
        };
        return new ModelGeometry(header, elements, ["uid-1", "uid-2", "uid-3", "uid-4", "uid-5"]);
    }

    private static byte[] Serialize(ModelGeometry model)
    {
        using var ms = new MemoryStream();
        ModelGeometryFile.Write(ms, model);
        return ms.ToArray();
    }

    [Test]
    public void RoundTrip_PreservesAllFields()
    {
        var original = Sample();

        var loaded = ModelGeometryFile.Read(new MemoryStream(Serialize(original)));

        loaded.Header.ShouldBe(original.Header);
        loaded.Inventory.ShouldBe(original.Inventory);
        loaded.Elements.Count.ShouldBe(3);
        for (var i = 0; i < 3; i++)
        {
            var a = original.Elements[i];
            var b = loaded.Elements[i];
            b.Info.UniqueId.ShouldBe(a.Info.UniqueId);
            b.Info.ElementId.ShouldBe(a.Info.ElementId);
            b.Info.Category.ShouldBe(a.Info.Category);
            b.Info.Family.ShouldBe(a.Info.Family);
            b.Info.Type.ShouldBe(a.Info.Type);
            b.Info.Level.ShouldBe(a.Info.Level);
            b.Info.Workset.ShouldBe(a.Info.Workset);
            b.Info.ConnectedUniqueIds.ShouldBe(a.Info.ConnectedUniqueIds);
            b.Info.RootComponentUniqueId.ShouldBe(a.Info.RootComponentUniqueId);
            b.Bounds.ShouldBe(a.Bounds);
            b.Fingerprint.ShouldBe(a.Fingerprint);
            b.Mesh.Positions.ShouldBe(a.Mesh.Positions);
            b.Mesh.Indices.ShouldBe(a.Mesh.Indices);
        }

        loaded.Elements[0].Info.Family.ShouldBe("Стена");
        loaded.Elements[2].Mesh.TriangleCount.ShouldBe(0);
    }

    [Test]
    public void Read_Version1File_RootComponentIsNull()
    {
        // Файл записан форматом версии 1, до поля корневого семейства.
        const string version1 =
            "VENHMQEAAAAEYXJjaAhhcmNoLnJ2dOkHAAAAXNu0QzPfCLQAAAABBHszRH0AAAAAAESPQAAAAAAAQZ/AAAAAAAAADkACAAAABXVpZC0xZQAAAAAAAAAJT1NUX1dhbGxzAQrQodGC0LXQvdCwARLQkdCw0LfQvtCy0LDRjyAyMDABENCj0YDQvtCy0LXQvdGMIDEBCFdvcmtzZXQxAQAAAAV1aWQtMgAAAAAAAAAAAAAAAAAAyEIAAMhCAABIQgcAAAAAAAAAAAAAAAAAAAAeAAAABXVpZC0yZgAAAAAAAAAOT1NUX1BpcGVDdXJ2ZXMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAyEIAAMhCAABIQggAAAAAAAAAHgAAAAAAAAAeAAAAPAAAAAAAAABiZkAHJ5xQRUB8DyeYOkYGBgYmBgYGAAAAAP//AwBiZkAHJ5xQRUB8DyeYOkYGBgYmBgYGAAAAAP//AwACAAAABXVpZC0xBXVpZC0y";

        var loaded = ModelGeometryFile.Read(new MemoryStream(Convert.FromBase64String(version1)));

        loaded.Elements.Select(e => e.Info.UniqueId).ShouldBe(["uid-1", "uid-2"]);
        loaded.Elements[0].Info.ConnectedUniqueIds.ShouldBe(["uid-2"]);
        loaded.Elements.ShouldAllBe(e => e.Info.RootComponentUniqueId == null);
        loaded.Inventory.ShouldBe(["uid-1", "uid-2"]);
    }

    [Test]
    public void RoundTrip_NonSeekableOutput_Works()
    {
        var original = Sample();
        using var backing = new MemoryStream();

        ModelGeometryFile.Write(new NonSeekableStream(backing), original);

        var loaded = ModelGeometryFile.Read(new MemoryStream(backing.ToArray()));
        loaded.Elements.Count.ShouldBe(3);
        loaded.Elements[1].Mesh.Indices.ShouldBe([0, 1, 2, 0, 2, 3]);
    }

    [Test]
    public void ReadHeader_DoesNotNeedMeshes()
    {
        var original = Sample();
        var bytes = Serialize(original);
        var meshBlockStart = (int)ModelGeometryFile.ReadMeshBlockOffset(new MemoryStream(bytes));
        var truncated = bytes.AsSpan(0, meshBlockStart).ToArray();

        var header = ModelGeometryFile.ReadHeader(new MemoryStream(truncated));

        header.ShouldBe(original.Header);
    }

    [Test]
    public void Read_WrongMagic_Throws()
    {
        var bytes = Serialize(Sample());
        bytes[0] ^= 0xFF;

        Should.Throw<InvalidDataException>(() => ModelGeometryFile.Read(new MemoryStream(bytes)));
        Should.Throw<InvalidDataException>(() => ModelGeometryFile.ReadHeader(new MemoryStream(bytes)));
    }

    [Test]
    public void Read_WrongVersion_Throws()
    {
        var bytes = Serialize(Sample());
        bytes[4] = 99;

        Should.Throw<InvalidDataException>(() => ModelGeometryFile.Read(new MemoryStream(bytes)));
    }

    [Test]
    public void Read_TruncatedFile_ThrowsInvalidData()
    {
        var bytes = Serialize(Sample());

        Should.Throw<InvalidDataException>(() =>
            ModelGeometryFile.Read(new MemoryStream(bytes, 0, bytes.Length - 10)));
    }

    [Test]
    public void Read_TamperedMeshOffset_Throws()
    {
        var bytes = Serialize(Sample());
        var blockLengthPosition = (int)ModelGeometryFile.ReadMeshBlockOffset(new MemoryStream(bytes)) - sizeof(long);
        // Last table entry ends with offset (long) and length (int), right before the block length.
        var offsetPosition = blockLengthPosition - sizeof(long) - sizeof(int);
        bytes[offsetPosition] ^= 1;

        Should.Throw<InvalidDataException>(() => ModelGeometryFile.Read(new MemoryStream(bytes)));
    }

    [Test]
    public void Read_BlockLengthNotMatchingMeshLengths_Throws()
    {
        var bytes = Serialize(Sample());
        var blockLengthPosition = (int)ModelGeometryFile.ReadMeshBlockOffset(new MemoryStream(bytes)) - sizeof(long);
        bytes[blockLengthPosition] += 1;

        Should.Throw<InvalidDataException>(() => ModelGeometryFile.Read(new MemoryStream(bytes)));
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
