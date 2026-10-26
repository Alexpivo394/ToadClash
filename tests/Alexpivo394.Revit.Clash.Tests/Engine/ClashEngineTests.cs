using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class ClashEngineTests
{
    private static readonly (double, double, double) Zero = (0, 0, 0);
    private static readonly SelectionSet PipeSet = TestScenes.CategorySet("OV", "OST_PipeCurves");
    private static readonly SelectionSet WallSet = TestScenes.CategorySet("AR", "OST_Walls");

    private static ClashConfiguration Config(double tolerance = 20) =>
        new([PipeSet, WallSet], [new MatrixCell("OV", "AR", true, tolerance)], []);

    private static ClashRunResult Run(ClashConfiguration config, params ModelInput[] models) =>
        new ClashEngine(new EngineOptions(4)).Run(models, config, CancellationToken.None);

    private static ClashRunResult RunPair(ElementMesh pipe, ElementMesh wall, double tolerance = 20) =>
        Run(Config(tolerance),
            TestScenes.Model("ov", "OV", Zero, [new("p", "OST_PipeCurves", pipe)]),
            TestScenes.Model("ar", "AR", Zero, [new("w", "OST_Walls", wall)]));

    private static ElementMesh Cube(float x, float y = 0, float z = 0, float size = 1000) =>
        TestScenes.Box(new Vector3(x, y, z), new Vector3(x + size, y + size, z + size));

    private static ElementMesh Merge(params ElementMesh[] meshes)
    {
        var positions = new List<float>();
        var indices = new List<int>();
        foreach (var mesh in meshes)
        {
            var offset = positions.Count / 3;
            positions.AddRange(mesh.Positions);
            indices.AddRange(mesh.Indices.Select(i => i + offset));
        }

        return new ElementMesh([.. positions], [.. indices]);
    }

    [Test]
    public void Boxes_Overlap500_DepthIs500()
    {
        var result = RunPair(Cube(0), Cube(500));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(500f, 1f);
        result.Hits[0].PointX.ShouldBe(750, 25);
        result.Stats.CandidatePairs.ShouldBe(1);
        result.Stats.Elements.ShouldBe(2);
        result.Stats.Triangles.ShouldBe(24);
        result.Stats.PeakManagedBytes.ShouldBeGreaterThan(0);
    }

    [Test]
    public void Boxes_Overlap500_NotFlush_DepthIs500()
    {
        var result = RunPair(Cube(0), Cube(500, 100, 100));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(500f, 1f);
    }

    [Test]
    public void Boxes_Overlap10_BelowTolerance_NoHit() => RunPair(Cube(0), Cube(990)).Hits.ShouldBeEmpty();

    [Test]
    public void Boxes_Overlap25_Hit()
    {
        var result = RunPair(Cube(0), Cube(975));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(25f, 1f);
    }

    [Test]
    public void CoplanarTouchingFaces_NoHit()
    {
        RunPair(Cube(0), Cube(1000), tolerance: 0).Hits.ShouldBeEmpty();
        RunPair(Cube(0), Cube(1000, 100, 100), tolerance: 0).Hits.ShouldBeEmpty();
    }

    [Test]
    public void BoxInsideBox_HitByContainment()
    {
        var result = RunPair(Cube(400, 400, 400, 200), Cube(0));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBeGreaterThan(20f);
        result.Hits[0].PointX.ShouldBe(500, 1e-3);
        result.Hits[0].PointY.ShouldBe(500, 1e-3);
        result.Hits[0].PointZ.ShouldBe(500, 1e-3);
    }

    [Test]
    public void BoxInsideBox_EitherOrder_HitByContainment() =>
        RunPair(Cube(0), Cube(400, 400, 400, 200)).Hits.Count.ShouldBe(1);

    [Test]
    public void OpenMesh_InsideClosedBox_NoContainmentHit()
    {
        var closed = Cube(400, 400, 400, 200);
        var open = new ElementMesh(closed.Positions, closed.Indices[..^6]);

        RunPair(open, Cube(0)).Hits.ShouldBeEmpty();
    }

    [Test]
    public void CrossingCylinders_R50_DepthAbout100()
    {
        var pipe = TestScenes.Cylinder(new Vector3(-500, 0, 0), new Vector3(500, 0, 0), 50, 24);
        var duct = TestScenes.Cylinder(new Vector3(0, -500, 0), new Vector3(0, 500, 0), 50, 24);

        var result = RunPair(pipe, duct);

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBeInRange(95f, 101f);
        result.Hits[0].PointX.ShouldBe(0, 1);
        result.Hits[0].PointY.ShouldBe(0, 1);
        result.Hits[0].PointZ.ShouldBe(0, 1);
    }

    [Test]
    public void PipeThroughWall_Hit()
    {
        var pipe = TestScenes.Cylinder(new Vector3(-500, 0, 0), new Vector3(700, 0, 0), 50, 24);
        var wall = TestScenes.Box(new Vector3(0, -1000, -1000), new Vector3(200, 1000, 1000));

        var result = RunPair(pipe, wall);

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBeGreaterThan(20f);
        result.Hits[0].PointX.ShouldBe(100, 1);
    }

    [Test]
    public void PipeThroughHoleInSlab_NoHit()
    {
        // Плита-рамка 2000×2000×200 с отверстием 300×300 в центре: четыре перекрывающихся бокса одного элемента.
        var slab = Merge(
            TestScenes.Box(new Vector3(-1000, -1000, 0), new Vector3(-150, 1000, 200)),
            TestScenes.Box(new Vector3(150, -1000, 0), new Vector3(1000, 1000, 200)),
            TestScenes.Box(new Vector3(-1000, -1000, 0), new Vector3(1000, -150, 200)),
            TestScenes.Box(new Vector3(-1000, 150, 0), new Vector3(1000, 1000, 200)));
        var pipe = TestScenes.Cylinder(new Vector3(0, 0, -500), new Vector3(0, 0, 700), 50, 24);

        var result = RunPair(pipe, slab);

        result.Stats.CandidatePairs.ShouldBe(1);
        result.Hits.ShouldBeEmpty();
    }

    [Test]
    public void FarFromOrigin_ClashFoundWithMillimeterAccuracy()
    {
        const double far = 6_000_000;
        var config = Config();
        var result = Run(config,
            TestScenes.Model("ov", "OV", (far, far, far), [new("p", "OST_PipeCurves", Cube(0))]),
            TestScenes.Model("ar", "AR", (far, far, far), [new("w", "OST_Walls", Cube(975, 100, 100))]));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(25f, 1f);
        result.Hits[0].PointX.ShouldBe(far + 987.5, 25);
        result.Hits[0].PointY.ShouldBe(far + 550, 25);
        result.Hits[0].PointZ.ShouldBe(far + 550, 25);
    }

    [Test]
    public void ToleranceFromMatrixCell_IsUsed() => RunPair(Cube(0), Cube(975), tolerance: 30).Hits.ShouldBeEmpty();

    [Test]
    public void Hits_SortedAndCanonical()
    {
        // Модель "zz" идёт первой, поэтому её элементы получают меньшие индексы сцены, но больший ElementRef.
        var config = Config();
        var result = Run(config,
            TestScenes.Model("zz", "OV", Zero, [
                new("p2", "OST_PipeCurves", Cube(3000)),
                new("p1", "OST_PipeCurves", Cube(0))]),
            TestScenes.Model("aa", "AR", Zero, [
                new("w2", "OST_Walls", Cube(3500)),
                new("w1", "OST_Walls", Cube(500))]));

        result.Hits.Select(h => (h.A, h.B)).ShouldBe([
            (new ElementRef("aa", "w1"), new ElementRef("zz", "p1")),
            (new ElementRef("aa", "w2"), new ElementRef("zz", "p2"))]);
    }

    [Test]
    public void Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var models = new[]
        {
            TestScenes.Model("ov", "OV", Zero, [new("p", "OST_PipeCurves", Cube(0))]),
            TestScenes.Model("ar", "AR", Zero, [new("w", "OST_Walls", Cube(500))])
        };

        Should.Throw<OperationCanceledException>(() =>
            new ClashEngine(new EngineOptions()).Run(models, Config(), cts.Token));
    }
}
