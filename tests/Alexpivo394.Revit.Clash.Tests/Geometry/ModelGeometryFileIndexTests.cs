using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

public sealed class ModelGeometryFileIndexTests
{
    /// <summary>Файл версии 2 (до числа треугольников в таблице): два элемента, второй вложен в первый.</summary>
    private const string Version2 =
        "VENHMQIAAAABawVwLnJ2dOYHAAAAAOVohjTfCAAAAAABBHszRH0AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAAABXVpZC0xAQAAAAAAAAAJT1NUX1dhbGxzAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADIQgAAyEIAAEhCBwAAAAAAAAAAAAAAAAAAAB4AAAAFdWlkLTICAAAAAAAAAA5PU1RfUGlwZUN1cnZlcwAAAAAAAAAAAQV1aWQtMQAAAAAAAAAAAAAAAAAAgD8AAIA/AAAAAAgAAAAAAAAAHgAAAAAAAAAmAAAARAAAAAAAAABiZkAHJ5xQRUB8DyeYOkYGBgYmBgYGAAAAAP//AwBiYUAHDfYIERAbhGEiEDYblMvIwMDABGWDaGYGBgYAAAAA//8DAAIAAAAFdWlkLTEFdWlkLTI=";

    private static ModelGeometry Sample()
    {
        var header = new ModelHeader("arch", @"C:\models\arch.rvt", 2025,
            new DateTimeOffset(2026, 10, 26, 12, 30, 0, TimeSpan.FromHours(3)), true, "{3D}", 1000.5, -2000.25, 3.75);
        var triangle = new ElementMesh([0, 0, 0, 100, 0, 0, 0, 100, 50], [0, 1, 2]);
        var quad = new ElementMesh([0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], [0, 1, 2, 0, 2, 3]);
        var empty = new ElementMesh([], []);

        ElementGeometry Make(ElementInfo info, ElementMesh mesh, ulong fp) =>
            new(info, mesh, Aabb.FromPositions(mesh.Positions), fp);

        return new ModelGeometry(header,
        [
            Make(new ElementInfo("uid-1", 101, "OST_Walls", "Стена", "Базовая 200", "Уровень 1", "Workset1", ["uid-2"]), triangle, 0xDEADBEEFUL),
            Make(new ElementInfo("uid-2", 102, "OST_GenericModel", "Изделие", "Тип", null, null, [], "uid-1"), quad, 42UL),
            Make(new ElementInfo("uid-3", 103, "OST_Floors", "Floor", "Generic", "L2", null, []), empty, 0UL),
        ], ["uid-1", "uid-2", "uid-3", "uid-4"]);
    }

    private static byte[] Serialize(ModelGeometry model)
    {
        using var ms = new MemoryStream();
        ModelGeometryFile.Write(ms, model);
        return ms.ToArray();
    }

    [Test]
    public void ReadIndex_MatchesFullRead()
    {
        var bytes = Serialize(Sample());
        var full = ModelGeometryFile.Read(new MemoryStream(bytes));

        var index = ModelGeometryFile.ReadIndex(new MemoryStream(bytes));

        index.Header.ShouldBe(full.Header);
        index.Inventory.ShouldBe(full.Inventory);
        index.Elements.Count.ShouldBe(full.Elements.Count);
        for (var i = 0; i < full.Elements.Count; i++)
        {
            var entry = index.Elements[i];
            var element = full.Elements[i];
            entry.Info.UniqueId.ShouldBe(element.Info.UniqueId);
            entry.Info.RootComponentUniqueId.ShouldBe(element.Info.RootComponentUniqueId);
            entry.Info.ConnectedUniqueIds.ShouldBe(element.Info.ConnectedUniqueIds);
            entry.Bounds.ShouldBe(element.Bounds);
            entry.Fingerprint.ShouldBe(element.Fingerprint);
            entry.TriangleCount.ShouldBe(element.Mesh.TriangleCount);
        }

        index.TriangleCount.ShouldBe(3L);
    }

    [Test]
    public void ReadIndex_DoesNotDecompressMeshes()
    {
        var bytes = Serialize(Sample());
        var meshBlockStart = (int)ModelGeometryFile.ReadMeshBlockOffset(new MemoryStream(bytes));
        var meshBlockLength = BitConverter.ToInt64(bytes, meshBlockStart - sizeof(long));
        for (var i = 0; i < meshBlockLength; i++) bytes[meshBlockStart + i] = 0xAB;

        var index = ModelGeometryFile.ReadIndex(new MemoryStream(bytes));

        index.Elements.Count.ShouldBe(3);
        index.Inventory.ShouldBe(["uid-1", "uid-2", "uid-3", "uid-4"]);
        Should.Throw<InvalidDataException>(() => ModelGeometryFile.Read(new MemoryStream(bytes)));
    }

    [Test]
    public void ReadIndex_Version2File_TriangleCountUnknown()
    {
        var bytes = Convert.FromBase64String(Version2);

        var index = ModelGeometryFile.ReadIndex(new MemoryStream(bytes));
        var full = ModelGeometryFile.Read(new MemoryStream(bytes));

        index.Elements.Select(e => e.Info.UniqueId).ShouldBe(["uid-1", "uid-2"]);
        index.Elements.ShouldAllBe(e => e.TriangleCount == -1);
        index.Elements[1].Info.RootComponentUniqueId.ShouldBe("uid-1");
        index.Inventory.ShouldBe(["uid-1", "uid-2"]);
        full.Elements.Select(e => e.Mesh.TriangleCount).ShouldBe([1, 2]);
    }

    [Test]
    public void ReadIndex_NonSeekableStream_Throws()
    {
        var bytes = Serialize(Sample());

        Should.Throw<ArgumentException>(() => ModelGeometryFile.ReadIndex(new ForwardOnlyStream(new MemoryStream(bytes))));
    }

    [Test]
    public void ReadIndex_TruncatedInventory_ThrowsInvalidData()
    {
        var bytes = Serialize(Sample());

        Should.Throw<InvalidDataException>(() => ModelGeometryFile.ReadIndex(new MemoryStream(bytes, 0, bytes.Length - 3)));
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
