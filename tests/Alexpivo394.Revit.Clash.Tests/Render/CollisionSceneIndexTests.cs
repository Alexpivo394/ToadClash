using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Render;
using Alexpivo394.Revit.Clash.Tests.Engine;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Render;

public sealed class CollisionSceneIndexTests
{
    private static ElementGeometry Element(string uniqueId, ElementMesh mesh) =>
        new(new ElementInfo(uniqueId, 1, "OST_Walls", null, null, null, null, []), mesh, Aabb.FromPositions(mesh.Positions), 0);

    private static ElementGeometry BoxElement(string uniqueId, Vector3 min, Vector3 max) => Element(uniqueId, TestScenes.Box(min, max));

    private static ModelGeometry Model(string key, (double X, double Y, double Z) origin, params ElementGeometry[] elements) =>
        new(new ModelHeader(key, $"{key}.rvt", 2024, DateTimeOffset.UnixEpoch, true, "3D", origin.X, origin.Y, origin.Z),
            elements, elements.Select(e => e.Info.UniqueId).ToList());

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
    public void Collect_PairAndContextInsideBox()
    {
        var index = new CollisionSceneIndex([
            Model("ar", (0, 0, 0),
                BoxElement("a", new Vector3(-100), new Vector3(100)),
                BoxElement("near", new Vector3(300, 300, -50), new Vector3(400, 400, 50)),
                BoxElement("far", new Vector3(10_000, 0, 0), new Vector3(10_100, 100, 100))),
            Model("ov", (0, 0, 0), BoxElement("b", new Vector3(-50), new Vector3(150)))
        ]);

        var scene = index.Collect("ar", "a", "ov", "b", 0, 0, 0);

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
    public void Collect_Budget_NearestFirst_Truncated()
    {
        var elements = new List<ElementGeometry> { BoxElement("a", new Vector3(-20), new Vector3(20)) };
        // Перечислены от дальних к ближним: порядок в модели не должен влиять на выбор
        for (var i = 49; i >= 0; i--)
            elements.Add(Element($"c{i}", Many(new Vector3(30 + i * 10, 0, 0), 10_000)));
        var index = new CollisionSceneIndex([
            Model("ar", (0, 0, 0), elements.ToArray()),
            Model("ov", (0, 0, 0), BoxElement("b", new Vector3(-10), new Vector3(10)))
        ]);

        var scene = index.Collect("ar", "a", "ov", "b", 0, 0, 0, contextTriangleBudget: 100_000);

        scene.ContextTriangles.ShouldBe(100_000);
        scene.Truncated.ShouldBeTrue();
        var context = scene.Meshes.Where(m => m.Role == RenderRole.Context).ToList();
        context.Count.ShouldBe(10);
        context.Max(m => Aabb.FromPositions(m.Positions).Min.X).ShouldBe(30 + 9 * 10, 0.01);
    }

    [Test]
    public void Collect_TwoModelsDifferentOrigins_Aligned()
    {
        var index = new CollisionSceneIndex([
            Model("ar", (0, 0, 0), BoxElement("a", new Vector3(1000, 0, 0), new Vector3(1100, 100, 100))),
            Model("ov", (500, 0, 0), BoxElement("b", new Vector3(500, 0, 0), new Vector3(600, 100, 100)))
        ]);

        var scene = index.Collect("ar", "a", "ov", "b", 1050, 50, 50);

        var a = scene.Meshes.Single(m => m.Role == RenderRole.A).Positions;
        var b = scene.Meshes.Single(m => m.Role == RenderRole.B).Positions;
        b.ShouldBe(a);
        Aabb.FromPositions(a).ShouldBe(new Aabb(new Vector3(-50), new Vector3(50)));
    }

    [Test]
    public void Collect_FarFromOrigin_Precise()
    {
        var mesh = new ElementMesh([1000.5f, 0, 0, 1100, 0, 0, 1000.5f, 100, 0], [0, 1, 2]);
        var index = new CollisionSceneIndex([
            Model("ar", (7_000_000, 7_000_000, 0), Element("a", mesh)),
            Model("ov", (7_000_000, 7_000_000, 0), Element("b", mesh))
        ]);

        var scene = index.Collect("ar", "a", "ov", "b", 7_001_000, 7_000_000, 0);

        var a = scene.Meshes.Single(m => m.Role == RenderRole.A).Positions;
        a[0].ShouldBe(0.5f, 0.01f);
        a[1].ShouldBe(0f, 0.01f);
    }

    [Test]
    public void Collect_MissingElement_Throws()
    {
        var index = new CollisionSceneIndex([Model("ar", (0, 0, 0), BoxElement("a", new Vector3(-100), new Vector3(100)))]);

        Should.Throw<KeyNotFoundException>(() => index.Collect("ar", "a", "ar", "missing", 0, 0, 0));
        Should.Throw<KeyNotFoundException>(() => index.Collect("nope", "a", "ar", "a", 0, 0, 0));
    }

    [Test]
    public void Render_Scene_UsesSceneHalfSize()
    {
        var index = new CollisionSceneIndex([
            Model("ar", (0, 0, 0), BoxElement("a", new Vector3(-100), new Vector3(100))),
            Model("ov", (0, 0, 0), BoxElement("b", new Vector3(-50), new Vector3(150)))
        ]);

        var image = CollisionRenderer.Render(index.Collect("ar", "a", "ov", "b", 0, 0, 0));

        image.HalfSizeMm.ShouldBe(600);
        image.Width.ShouldBe(640);
    }
}
