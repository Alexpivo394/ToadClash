using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class BroadPhaseTests
{
    private static readonly (double, double, double) Zero = (0, 0, 0);
    private static readonly SelectionSet PipeSet = TestScenes.CategorySet("OV", "OST_PipeCurves");
    private static readonly SelectionSet WallSet = TestScenes.CategorySet("AR", "OST_Walls");

    private static ClashConfiguration Config(params MatrixCell[] cells) => new([PipeSet, WallSet], cells, []);

    private static List<CandidatePair> Run(ClashConfiguration config, out ClashScene scene, params ModelInput[] models)
    {
        scene = ClashScene.Build(models, SetMembership.Evaluate(config, models));
        return BroadPhase.Find(scene, config, new PairFilter(scene, config), 4);
    }

    [Test]
    public void OverlappingBoxes_AreCandidates()
    {
        var pairs = Run(Config(new MatrixCell("OV", "AR", true, 25)), out var scene,
            TestScenes.Model("ov", "OV", Zero, [new("p", "OST_PipeCurves", TestScenes.Box(Vector3.Zero, new Vector3(10)))]),
            TestScenes.Model("ar", "AR", Zero, [
                new("w1", "OST_Walls", TestScenes.Box(new Vector3(5), new Vector3(20))),
                new("w2", "OST_Walls", TestScenes.Box(new Vector3(100), new Vector3(110)))]));

        pairs.Count.ShouldBe(1);
        pairs[0].A.ShouldBe(0);
        pairs[0].B.ShouldBe(1);
        pairs[0].ToleranceMm.ShouldBe(25f);
        scene.ElementCount.ShouldBe(3);
        scene.TriangleCount.ShouldBe(36);
    }

    [Test]
    public void EmptyAndDegenerateMeshes_AreSkipped()
    {
        var degenerate = new ElementMesh([0, 0, 0, 1, 0, 0, 2, 0, 0], [0, 1, 2]);
        var pairs = Run(Config(new MatrixCell("OV", "AR", true)), out var scene,
            TestScenes.Model("ov", "OV", Zero, [
                new("empty", "OST_PipeCurves", new ElementMesh([], [])),
                new("flat", "OST_PipeCurves", degenerate),
                new("ok", "OST_PipeCurves", TestScenes.Box(Vector3.Zero, new Vector3(10)))]),
            TestScenes.Model("ar", "AR", Zero, [new("w", "OST_Walls", TestScenes.Box(Vector3.Zero, new Vector3(10)))]));

        scene.ElementCount.ShouldBe(2);
        scene.Elements.Select(e => e.Ref.UniqueId).ShouldBe(["ok", "w"]);
        pairs.Count.ShouldBe(1);
    }

    [Test]
    public void ElementsOutsideSets_AreSkipped()
    {
        Run(Config(new MatrixCell("OV", "AR", true)), out var scene,
            TestScenes.Model("ov", "OV", Zero, [new("x", "OST_Doors", TestScenes.Box(Vector3.Zero, new Vector3(10)))]));

        scene.ElementCount.ShouldBe(0);
    }

    [Test]
    public void EachPairOnce()
    {
        var pipes = Enumerable.Range(0, 300)
            .Select(i => new TestScenes.Item($"p{i}", "OST_PipeCurves",
                TestScenes.Box(new Vector3(i % 20 * 5, i / 20 * 5, 0), new Vector3(i % 20 * 5 + 8, i / 20 * 5 + 8, 8))))
            .ToList();
        var walls = Enumerable.Range(0, 100)
            .Select(i => new TestScenes.Item($"w{i}", "OST_Walls",
                TestScenes.Box(new Vector3(i % 10 * 9, i / 10 * 9, 0), new Vector3(i % 10 * 9 + 12, i / 10 * 9 + 12, 8))))
            .ToList();
        var config = Config(new MatrixCell("OV", "OV", true), new MatrixCell("OV", "AR", true));

        var pairs = Run(config, out var scene,
            TestScenes.Model("ov", "OV", Zero, pipes), TestScenes.Model("ar", "AR", Zero, walls));

        // Эталон: полный перебор по габаритам; пары AR-AR отключены.
        var expected = new HashSet<(int, int)>();
        for (var a = 0; a < scene.Elements.Count; a++)
        {
            for (var b = a + 1; b < scene.Elements.Count; b++)
            {
                var da = scene.Elements[a].Discipline;
                var db = scene.Elements[b].Discipline;
                var enabled = da != db || scene.Disciplines[da] == "OV";
                if (enabled && scene.Elements[a].Bounds.Overlaps(scene.Elements[b].Bounds)) expected.Add((a, b));
            }
        }

        pairs.Count.ShouldBe(expected.Count);
        expected.SetEquals(pairs.Select(p => (p.A, p.B))).ShouldBeTrue();
        pairs.ShouldAllBe(p => p.A < p.B);
        expected.Count.ShouldBeGreaterThan(100);
    }

    [Test]
    public void ModelsWithDifferentOrigins_AreRebasedToSceneOrigin()
    {
        var config = Config(new MatrixCell("OV", "AR", true));
        var pairs = Run(config, out var scene,
            TestScenes.Model("ov", "OV", (1000, 2000, 3000), [new("p", "OST_PipeCurves", TestScenes.Box(Vector3.Zero, new Vector3(10)))]),
            TestScenes.Model("ar", "AR", (1005, 2005, 3005), [new("w", "OST_Walls", TestScenes.Box(Vector3.Zero, new Vector3(10)))]));

        scene.OriginX.ShouldBe(1000);
        scene.OriginY.ShouldBe(2000);
        scene.OriginZ.ShouldBe(3000);
        scene.Elements[1].Bounds.Min.ShouldBe(new Vector3(5));
        pairs.Count.ShouldBe(1);

        var far = Run(config, out _,
            TestScenes.Model("ov", "OV", (1000, 0, 0), [new("p", "OST_PipeCurves", TestScenes.Box(Vector3.Zero, new Vector3(10)))]),
            TestScenes.Model("ar", "AR", (1500, 0, 0), [new("w", "OST_Walls", TestScenes.Box(Vector3.Zero, new Vector3(10)))]));
        far.ShouldBeEmpty();
    }

    [Test]
    public void SameOrigin_ReusesMeshArrays()
    {
        var mesh = TestScenes.Box(Vector3.Zero, new Vector3(10));
        Run(Config(), out var scene, TestScenes.Model("ov", "OV", (5, 5, 5), [new("p", "OST_PipeCurves", mesh)]));

        scene.Elements[0].Positions.ShouldBeSameAs(mesh.Positions);
    }

    [Test]
    public void ElementRef_CanonicalOrder_Ordinal()
    {
        var upper = new ElementRef("B", "x");
        var lower = new ElementRef("a", "x");
        var byId = new ElementRef("B", "y");

        upper.CompareTo(lower).ShouldBeLessThan(0);
        upper.CompareTo(byId).ShouldBeLessThan(0);
        upper.CompareTo(upper).ShouldBe(0);
        new[] { byId, lower, upper }.Order().ShouldBe([upper, byId, lower]);
    }
}
