using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Render;
using Alexpivo394.Revit.Clash.Tests.Engine;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Render;

public sealed class CollisionScenesTests
{
    private static ElementGeometry Element(string uniqueId, ElementMesh mesh) =>
        new(new ElementInfo(uniqueId, 1, "OST_Walls", null, null, null, null, []), mesh, Aabb.FromPositions(mesh.Positions), 0);

    private static ElementGeometry BoxElement(string uniqueId, Vector3 min, Vector3 max) => Element(uniqueId, TestScenes.Box(min, max));

    private static ModelGeometry Model(string key, (double X, double Y, double Z) origin, params ElementGeometry[] elements) =>
        new(new ModelHeader(key, $"{key}.rvt", 2024, DateTimeOffset.UnixEpoch, true, "3D", origin.X, origin.Y, origin.Z),
            elements, elements.Select(e => e.Info.UniqueId).ToList());

    /// <summary>Модель в памяти: слепок записывается в байты, оглавление читается из них, сетки — по требованию.</summary>
    private static SceneModel Scene(string key, (double X, double Y, double Z) origin, Func<Stream, Stream>? wrap, params ElementGeometry[] elements)
    {
        var file = new MemoryStream();
        ModelGeometryFile.Write(file, Model(key, origin, elements));
        var bytes = file.ToArray();
        var index = ModelGeometryFile.ReadIndex(new MemoryStream(bytes));
        return new SceneModel(index, () => wrap is null ? new MemoryStream(bytes) : wrap(new MemoryStream(bytes)));
    }

    private static SceneModel Scene(string key, (double X, double Y, double Z) origin, params ElementGeometry[] elements) =>
        Scene(key, origin, null, elements);

    private static RenderScene Build(IReadOnlyList<SceneModel> models, string modelA, string a, string modelB, string b, double x, double y, double z,
        long budget = CollisionScenes.DefaultContextTriangleBudget) =>
        CollisionScenes.Build(models, new SceneTarget(modelA, a, modelB, b, x, y, z), budget);

    /// <summary>Поток-обёртка: считает <see cref="Stream.Seek" /> и прочитанные байты.</summary>
    private sealed class CountingStream(Stream inner, Counters counters) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            counters.BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            counters.Seeks++;
            return inner.Seek(offset, origin);
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Counters
    {
        public int Seeks;
        public long BytesRead;
    }

    /// <summary>Сетка из <paramref name="count"/> мелких треугольников в кубике 50 мм с углом в <paramref name="at"/>.</summary>
    private static ElementMesh Many(Vector3 at, int count)
    {
        var positions = new float[count * 9];
        var indices = new int[count * 3];
        for (var i = 0; i < count; i++)
        {
            var offset = at + new Vector3(i % 50, i / 50 % 50, i / 2500 % 50);
            Vector3[] v = [offset, offset + Vector3.UnitX, offset + Vector3.UnitY];
            for (var k = 0; k < 3; k++)
            {
                positions[i * 9 + k * 3] = v[k].X;
                positions[i * 9 + k * 3 + 1] = v[k].Y;
                positions[i * 9 + k * 3 + 2] = v[k].Z;
                indices[i * 3 + k] = i * 3 + k;
            }
        }

        return new ElementMesh(positions, indices);
    }

    [Test]
    public void Build_PairAndContextInsideBox()
    {
        SceneModel[] models = [
            Scene("ar", (0, 0, 0),
                BoxElement("a", new Vector3(-100), new Vector3(100)),
                BoxElement("near", new Vector3(300, 300, -50), new Vector3(400, 400, 50)),
                BoxElement("far", new Vector3(10_000, 0, 0), new Vector3(10_100, 100, 100))),
            Scene("ov", (0, 0, 0), BoxElement("b", new Vector3(-50), new Vector3(150)))
        ];

        var scene = Build(models, "ar", "a", "ov", "b", 0, 0, 0);

        scene.Meshes.Count(m => m.Role == RenderRole.A).ShouldBe(1);
        scene.Meshes.Count(m => m.Role == RenderRole.B).ShouldBe(1);
        scene.Meshes.Count(m => m.Role == RenderRole.Context).ShouldBe(1);
        scene.ContextTriangles.ShouldBe(12);
        scene.Truncated.ShouldBeFalse();
        scene.HalfSizeMm.ShouldBe(600);
        var context = scene.Meshes.Single(m => m.Role == RenderRole.Context);
        Aabb.FromPositions(context.Positions).ShouldBe(new Aabb(new Vector3(300, 300, -50), new Vector3(400, 400, 50)));
    }

    [Test]
    public void Build_Budget_NearestFirst_Truncated()
    {
        var elements = new List<ElementGeometry> { BoxElement("a", new Vector3(-20), new Vector3(20)) };
        // Перечислены от дальних к ближним: порядок в модели не должен влиять на выбор
        for (var i = 49; i >= 0; i--)
            elements.Add(Element($"c{i}", Many(new Vector3(30 + i * 10, 0, 0), 10_000)));
        SceneModel[] models = [
            Scene("ar", (0, 0, 0), elements.ToArray()),
            Scene("ov", (0, 0, 0), BoxElement("b", new Vector3(-10), new Vector3(10)))
        ];

        var scene = Build(models, "ar", "a", "ov", "b", 0, 0, 0, budget: 100_000);

        scene.ContextTriangles.ShouldBe(100_000);
        scene.Truncated.ShouldBeTrue();
        var context = scene.Meshes.Where(m => m.Role == RenderRole.Context).ToList();
        context.Count.ShouldBe(10);
        context.Max(m => Aabb.FromPositions(m.Positions).Min.X).ShouldBe(30 + 9 * 10, 0.01);
    }

    [Test]
    public void Build_HugeNearElement_SkippedFarSmallStillIncluded()
    {
        SceneModel[] models = [
            Scene("ar", (0, 0, 0),
                BoxElement("a", new Vector3(-20), new Vector3(20)),
                Element("huge", Many(new Vector3(30, 0, 0), 400_000)),
                Element("small1", Many(new Vector3(300, 0, 0), 10)),
                Element("small2", Many(new Vector3(400, 0, 0), 10))),
            Scene("ov", (0, 0, 0), BoxElement("b", new Vector3(-10), new Vector3(10)))
        ];

        var scene = Build(models, "ar", "a", "ov", "b", 0, 0, 0);

        var context = scene.Meshes.Where(m => m.Role == RenderRole.Context).ToList();
        context.Count.ShouldBe(2);
        context.ShouldAllBe(m => m.Indices.Length == 30);
        scene.Truncated.ShouldBeTrue();
        scene.ContextTriangles.ShouldBe(20);
    }

    [Test]
    public void Build_ReadsOnlyPickedMeshes()
    {
        var counters = new Counters();
        var ar = Scene("ar", (0, 0, 0), s => new CountingStream(s, counters),
            BoxElement("a", new Vector3(-100), new Vector3(100)),
            BoxElement("near", new Vector3(300, 300, -50), new Vector3(400, 400, 50)),
            Element("farBig", Many(new Vector3(50_000, 0, 0), 20_000)),
            BoxElement("farBox", new Vector3(10_000, 0, 0), new Vector3(10_100, 100, 100)));
        SceneModel[] models = [ar, Scene("ov", (0, 0, 0), BoxElement("b", new Vector3(-50), new Vector3(150)))];

        var scene = Build(models, "ar", "a", "ov", "b", 0, 0, 0);

        scene.Meshes.Count(m => m.Role == RenderRole.Context).ShouldBe(1);
        var picked = ar.Index.Elements.Where(e => e.Info.UniqueId is "a" or "near").Sum(e => (long)e.MeshLength);
        var farLength = ar.Index.Elements.Single(e => e.Info.UniqueId == "farBig").MeshLength;
        farLength.ShouldBeGreaterThan(1000);
        counters.BytesRead.ShouldBeLessThanOrEqualTo(picked);
        counters.Seeks.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Build_ElementWithoutTriangles_Ignored()
    {
        SceneModel[] models = [
            Scene("ar", (0, 0, 0),
                BoxElement("a", new Vector3(-100), new Vector3(100)),
                Element("empty", new ElementMesh([], [])),
                BoxElement("near", new Vector3(300, 300, -50), new Vector3(400, 400, 50))),
            Scene("ov", (0, 0, 0), BoxElement("b", new Vector3(-50), new Vector3(150)))
        ];

        var scene = Build(models, "ar", "a", "ov", "b", 0, 0, 0);

        scene.Meshes.Count(m => m.Role == RenderRole.Context).ShouldBe(1);
        scene.Meshes.ShouldAllBe(m => m.Indices.Length > 0);
    }

    [Test]
    public void Build_TwoModelsDifferentOrigins_Aligned()
    {
        SceneModel[] models = [
            Scene("ar", (0, 0, 0), BoxElement("a", new Vector3(1000, 0, 0), new Vector3(1100, 100, 100))),
            Scene("ov", (500, 0, 0), BoxElement("b", new Vector3(500, 0, 0), new Vector3(600, 100, 100)))
        ];

        var scene = Build(models, "ar", "a", "ov", "b", 1050, 50, 50);

        var a = scene.Meshes.Single(m => m.Role == RenderRole.A).Positions;
        var b = scene.Meshes.Single(m => m.Role == RenderRole.B).Positions;
        b.ShouldBe(a);
        Aabb.FromPositions(a).ShouldBe(new Aabb(new Vector3(-50), new Vector3(50)));
    }

    [Test]
    public void Build_FarFromOrigin_Precise()
    {
        var mesh = new ElementMesh([1000.5f, 0, 0, 1100, 0, 0, 1000.5f, 100, 0], [0, 1, 2]);
        SceneModel[] models = [
            Scene("ar", (7_000_000, 7_000_000, 0), Element("a", mesh)),
            Scene("ov", (7_000_000, 7_000_000, 0), Element("b", mesh))
        ];

        var scene = Build(models, "ar", "a", "ov", "b", 7_001_000, 7_000_000, 0);

        var a = scene.Meshes.Single(m => m.Role == RenderRole.A).Positions;
        a[0].ShouldBe(0.5f, 0.01f);
        a[1].ShouldBe(0f, 0.01f);
    }

    [Test]
    public void Build_MissingElement_Throws()
    {
        SceneModel[] models = [Scene("ar", (0, 0, 0), BoxElement("a", new Vector3(-100), new Vector3(100)))];

        Should.Throw<KeyNotFoundException>(() => Build(models, "ar", "a", "ar", "missing", 0, 0, 0));
        Should.Throw<KeyNotFoundException>(() => Build(models, "nope", "a", "ar", "a", 0, 0, 0));
    }

    [Test]
    public void Render_Scene_UsesSceneHalfSize()
    {
        SceneModel[] models = [
            Scene("ar", (0, 0, 0), BoxElement("a", new Vector3(-100), new Vector3(100))),
            Scene("ov", (0, 0, 0), BoxElement("b", new Vector3(-50), new Vector3(150)))
        ];

        var image = CollisionRenderer.Render(Build(models, "ar", "a", "ov", "b", 0, 0, 0));

        image.HalfSizeMm.ShouldBe(600);
        image.Width.ShouldBe(640);
    }
}
