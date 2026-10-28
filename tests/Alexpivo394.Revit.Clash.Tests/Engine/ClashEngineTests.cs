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
    public void SmallBoxAlmostInsideLargeBox_ProtrudesThroughTop_DepthCountsBuriedPart()
    {
        // Труба почти целиком внутри воздуховода и выступает над ним на 4 мм: поверхности пересекаются только у верхней
        // грани, но тела перекрываются на всю высоту трубы.
        var pipe = TestScenes.Box(new Vector3(2000, 2000, 400), new Vector3(2100, 2100, 504));
        var duct = TestScenes.Box(Vector3.Zero, new Vector3(5000, 5000, 500));

        var result = RunPair(pipe, duct);

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(100f, 1f);
    }

    [Test]
    public void DuctThroughWallOpening_FacesOffsetByMicrons_NoHit()
    {
        // Проём и воздуховод триангулированы независимо: грани касаются с погрешностью 3 мкм.
        var duct = TestScenes.Box(new Vector3(999.997f, -100, 999.997f), new Vector3(2000.003f, 300, 2000.003f));

        RunPair(duct, WallWithOpening()).Hits.ShouldBeEmpty();
    }

    // Стена 3000 × 200 × 3000 с проёмом 1000 × 1000 в центре - одно замкнутое тело, как у Revit.
    private static ElementMesh WallWithOpening()
    {
        float[] grid = [0, 1000, 2000, 3000];
        const float front = 0, back = 200;
        var positions = new List<float>();
        var indices = new List<int>();
        var index = new Dictionary<Vector3, int>();
        bool Filled(int i, int j) => i is >= 0 and < 3 && j is >= 0 and < 3 && !(i == 1 && j == 1);

        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        {
            if (!Filled(i, j)) continue;
            float x0 = grid[i], x1 = grid[i + 1], z0 = grid[j], z1 = grid[j + 1];
            Quad(new(x0, front, z0), new(x1, front, z0), new(x1, front, z1), new(x0, front, z1));
            Quad(new(x0, back, z0), new(x0, back, z1), new(x1, back, z1), new(x1, back, z0));
            if (!Filled(i - 1, j)) Quad(new(x0, front, z0), new(x0, front, z1), new(x0, back, z1), new(x0, back, z0));
            if (!Filled(i + 1, j)) Quad(new(x1, front, z0), new(x1, back, z0), new(x1, back, z1), new(x1, front, z1));
            if (!Filled(i, j - 1)) Quad(new(x0, front, z0), new(x0, back, z0), new(x1, back, z0), new(x1, front, z0));
            if (!Filled(i, j + 1)) Quad(new(x0, front, z1), new(x1, front, z1), new(x1, back, z1), new(x0, back, z1));
        }

        return new ElementMesh([.. positions], [.. indices]);

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int[] v = [Vertex(a), Vertex(b), Vertex(c), Vertex(d)];
            indices.AddRange([v[0], v[1], v[2], v[0], v[2], v[3]]);
        }

        int Vertex(Vector3 p)
        {
            if (index.TryGetValue(p, out var existing)) return existing;
            index[p] = positions.Count / 3;
            positions.AddRange([p.X, p.Y, p.Z]);
            return index[p];
        }
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
    public void BoxInsideRotatedBox_ZeroTolerance_HitByContainment()
    {
        // Повёрнутый воздуховод: габариты треугольников его боковых граней задевают зону вложенного отвода,
        // а сами вершины далеко. При малом повороте в зону попадает только ближняя боковая грань.
        var duct = Rotated(TestScenes.Box(new Vector3(-300, -1500, 0), new Vector3(300, 1500, 200)), 3.5f);
        var fitting = Rotated(TestScenes.Box(new Vector3(-240, 180, 5), new Vector3(-195, 250, 70)), 3.5f);

        var result = RunPair(fitting, duct, tolerance: 0);

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(45f, 1f);
    }

    private static ElementMesh Rotated(ElementMesh mesh, float degrees)
    {
        var (sin, cos) = MathF.SinCos(degrees * MathF.PI / 180);
        var positions = (float[])mesh.Positions.Clone();
        for (var i = 0; i < positions.Length; i += 3)
        {
            var x = positions[i];
            var y = positions[i + 1];
            positions[i] = x * cos - y * sin;
            positions[i + 1] = x * sin + y * cos;
        }

        return new ElementMesh(positions, mesh.Indices);
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
        result.Hits[0].PointX.ShouldBe(far + 987.5, 1);
        result.Hits[0].PointY.ShouldBe(far + 550, 1);
        result.Hits[0].PointZ.ShouldBe(far + 550, 1);
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

    private static ElementMesh Inverted(ElementMesh mesh)
    {
        var indices = (int[])mesh.Indices.Clone();
        for (var t = 0; t < indices.Length; t += 3) (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
        return new ElementMesh(mesh.Positions, indices);
    }

    private static ElementMesh Wall(float x0, float y0, float x1, float y1) =>
        TestScenes.Box(new Vector3(x0, y0, 0), new Vector3(x1, y1, 3000));

    [Test]
    public void IdenticalBoxes_Hit()
    {
        var result = RunPair(Cube(0), Cube(0));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(1000f, 1f);
        result.Hits[0].PointX.ShouldBe(500, 1);
    }

    [Test]
    public void IdenticalWalls_Hit()
    {
        var result = RunPair(Wall(0, 0, 5000, 200), Wall(0, 0, 5000, 200));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(200f, 1f);
    }

    [Test]
    public void BoxesFaceToFace_OppositeNormals_NoHit()
    {
        RunPair(Cube(0), Cube(0, 0, 1000), tolerance: 0).Hits.ShouldBeEmpty();
        RunPair(Cube(0), Cube(0, 1000), tolerance: 0).Hits.ShouldBeEmpty();
    }

    [Test]
    public void FlushOverlap_BothInverted_Hit()
    {
        var result = RunPair(Inverted(Cube(0)), Inverted(Cube(500)));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(500f, 1f);
    }

    [Test]
    public void FlushOverlap_OneInverted_Hit() =>
        RunPair(Cube(0), Inverted(Cube(500))).Hits.Single().DepthMm.ShouldBe(500f, 1f);

    [Test]
    public void Duplicates_OneInverted_Hit() =>
        RunPair(Cube(0), Inverted(Cube(0))).Hits.Single().DepthMm.ShouldBe(1000f, 1f);

    [Test]
    [Arguments(20)]
    [Arguments(0)]
    public void WallStandingOnLargerSlab_NoHit(double tolerance)
    {
        var slab = TestScenes.Box(new Vector3(-2000, -2000, -200), new Vector3(8000, 8000, 0));

        RunPair(Wall(0, 0, 5000, 200), slab, tolerance).Hits.ShouldBeEmpty();
        RunPair(Wall(0, 0, 5000, 200), Inverted(slab), tolerance).Hits.ShouldBeEmpty();
    }

    [Test]
    [Arguments(20)]
    [Arguments(0)]
    public void WallTJunction_NoHit(double tolerance) =>
        RunPair(Wall(0, 0, 5000, 200), Wall(2000, 200, 2200, 3000), tolerance).Hits.ShouldBeEmpty();

    [Test]
    [Arguments(20)]
    [Arguments(0)]
    public void WallButtJoint_NoHit(double tolerance) =>
        RunPair(Wall(0, 0, 5000, 200), Wall(5000, 0, 10000, 200), tolerance).Hits.ShouldBeEmpty();

    [Test]
    [Arguments(20)]
    [Arguments(0)]
    public void PipeRestingOnTray_NoHit(double tolerance)
    {
        // Лоток: дно 2 мм и борта; нижняя образующая трубы лежит на дне.
        var tray = Merge(
            TestScenes.Box(new Vector3(0, -150, -2), new Vector3(5000, 150, 0)),
            TestScenes.Box(new Vector3(0, -150, 0), new Vector3(5000, -148, 100)),
            TestScenes.Box(new Vector3(0, 148, 0), new Vector3(5000, 150, 100)));
        var pipe = TestScenes.Cylinder(new Vector3(-100, 0, 50), new Vector3(5100, 0, 50), 50, 24);

        RunPair(pipe, tray, tolerance).Hits.ShouldBeEmpty();
    }
}
