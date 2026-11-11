using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

/// <summary>
///     Глубина там, где одно тело стоит в вырезе другого (перегородка в отверстии пола): нахлёст у края выреза — это
///     его толщина, а не толщина пола. Сцены повторяют выгрузку Revit корпуса 5: стена под углом 80°, в 105 м от начала.
/// </summary>
public sealed class LocalDepthTests
{
    private const float WallThickness = 75;
    private const float WallLength = 1300;
    private const float WallHeight = 3250;
    private const float SlabThickness = 100;

    private static readonly SelectionSet WallSet = TestScenes.CategorySet("AR", "OST_Walls");
    private static readonly SelectionSet FloorSet = TestScenes.CategorySet("KR", "OST_Floors");

    private static ClashRunResult Run(ElementMesh wall, ElementMesh floor, double tolerance = 20) =>
        new ClashEngine(new EngineOptions(1)).Run(
            [
                TestScenes.Model("ar", "AR", (0, 0, 0), [new("w", "OST_Walls", wall)]),
                TestScenes.Model("kr", "KR", (0, 0, 0), [new("f", "OST_Floors", floor)])
            ],
            TestScenes.Config([WallSet, FloorSet], [TestScenes.Cell("AR", "KR", true, tolerance)], []),
            CancellationToken.None);

    [Test]
    [Arguments(0f)]
    [Arguments(80f)]
    public void WallInOpening_FacesTouch_NoHit(float angle) =>
        Run(Wall(angle), SlabAroundWall(angle, 0, 0)).Hits.ShouldBeEmpty();

    [Test]
    [Arguments(0f, 0.02f)]
    [Arguments(80f, 0.02f)]
    [Arguments(0f, 1f)]
    [Arguments(80f, 1f)]
    [Arguments(80f, 5f)]
    public void WallInOpening_OverlapBothSides_BelowTolerance_NoHit(float angle, float overlap) =>
        Run(Wall(angle), SlabAroundWall(angle, overlap, overlap)).Hits.ShouldBeEmpty();

    [Test]
    [Arguments(0f)]
    [Arguments(80f)]
    public void WallInOpening_Overlap1mmOneSide_NoHit(float angle) =>
        Run(Wall(angle), SlabAroundWall(angle, 1, 0)).Hits.ShouldBeEmpty();

    [Test]
    [Arguments(0f)]
    [Arguments(80f)]
    public void WallInOpening_Overlap30mmOneSide_HitWithDepth30(float angle)
    {
        var result = Run(Wall(angle), SlabAroundWall(angle, 30, 0));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(30f, 1f);
    }

    [Test]
    public void WallInOpening_Overlap30mmOneSide_ToleranceFromCellStillApplies() =>
        Run(Wall(80), SlabAroundWall(80, 30, 0), tolerance: 40).Hits.ShouldBeEmpty();

    [Test]
    [Arguments(0f)]
    [Arguments(80f)]
    public void ThinPlasterThroughSolidSlab_Hit(float angle)
    {
        // Штукатурка 20 мм проходит сквозь пол 100 мм: пол с обеих сторон, тело внутри по всей толщине пола.
        var plaster = Place(TestScenes.Box(new Vector3(0, 1000, 0), new Vector3(WallLength, 1020, WallHeight)), angle);

        var result = Run(plaster, SolidSlab(angle));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(100f, 1f);
    }

    [Test]
    public void ThinPipeThroughSlab_Hit()
    {
        // Труба Ø10 сквозь пол: тоньше допуска, но окружена полом — это коллизия.
        var pipe = Place(TestScenes.Cylinder(new Vector3(500, 500, -300), new Vector3(500, 500, 400), 5, 16), 0);

        Run(pipe, SolidSlab(0)).Hits.Count.ShouldBe(1);
    }

    [Test]
    public void PipeThroughWallOfTwoTouchingBodies_Hit()
    {
        // Стена из двух тел, касающихся гранью (как изделие из нескольких тел): луч изнутри не должен выходить на
        // внутренней грани, иначе труба сквозь стену выглядит неглубокой.
        float[] split = [0, 60, 200];
        var wall = Merge(
            TestScenes.Box(new Vector3(split[0], -1000, -1000), new Vector3(split[1], 1000, 1000)),
            TestScenes.Box(new Vector3(split[1], -1000, -1000), new Vector3(split[2], 1000, 1000)));
        var pipe = TestScenes.Cylinder(new Vector3(-500, 0, 0), new Vector3(700, 0, 0), 15, 16);

        var result = Run(Place(wall, 80), Place(pipe, 80));

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBeGreaterThan(40f);
    }

    [Test]
    public void SlabOfSeparateIslands_WallInOpeningOfOne_Overlap1mm_NoHit()
    {
        // Пол квартиры из нескольких отдельных участков: перегородка в вырезе одного из них.
        var island = SlabAroundWall(80, 1, 1);
        var other = Place(TestScenes.Box(new Vector3(-3000, 3500, 0), new Vector3(WallLength + 3000, 6000, SlabThickness)), 80);

        Run(Wall(80), Merge(island, other)).Hits.ShouldBeEmpty();
    }

    [Test]
    public void DuplicateOfThinSheetBox_Hit()
    {
        // Полый короб из листов 5 мм и его копия на том же месте: местная глубина - толщина листа, но это дубликат.
        var box = Merge(
            TestScenes.Box(new Vector3(0, 0, 0), new Vector3(300, 5, 200)),
            TestScenes.Box(new Vector3(0, 195, 0), new Vector3(300, 200, 200)),
            TestScenes.Box(new Vector3(0, 5, 0), new Vector3(300, 195, 5)),
            TestScenes.Box(new Vector3(0, 5, 195), new Vector3(300, 195, 200)));

        Run(Place(box, 80), Place(box, 80)).Hits.Count.ShouldBe(1);
    }

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

    private static ElementMesh Wall(float angle) =>
        Place(TestScenes.Box(new Vector3(0, -WallThickness / 2, 0), new Vector3(WallLength, WallThickness / 2, WallHeight)), angle);

    private static ElementMesh SolidSlab(float angle) =>
        Place(TestScenes.Box(new Vector3(-3000, -3000, 0), new Vector3(WallLength + 3000, 3000, SlabThickness)), angle);

    /// <summary>
    ///     Пол толщиной 100 мм с вырезом под стену; вырез уже стены на <paramref name="overlapRight" /> с одной стороны и
    ///     на <paramref name="overlapLeft" /> с другой. Одна замкнутая оболочка, как у выгрузки Revit.
    /// </summary>
    private static ElementMesh SlabAroundWall(float angle, float overlapRight, float overlapLeft)
    {
        Vector3[] outer = [new(-3000, -3000, 0), new(WallLength + 3000, -3000, 0), new(WallLength + 3000, 3000, 0), new(-3000, 3000, 0)];
        float right = -WallThickness / 2 + overlapRight, left = WallThickness / 2 - overlapLeft;
        Vector3[] hole = [new(0, right, 0), new(WallLength, right, 0), new(WallLength, left, 0), new(0, left, 0)];
        return Place(Ring(outer, hole, SlabThickness), angle);
    }

    // Плита между внешним контуром и вырезом (оба обходятся в одном направлении), нормали наружу.
    private static ElementMesh Ring(Vector3[] outer, Vector3[] hole, float height)
    {
        var positions = new List<float>();
        var indices = new List<int>();
        var up = new Vector3(0, 0, height);
        var outerCenter = (outer[0] + outer[2]) / 2;
        var holeCenter = (hole[0] + hole[2]) / 2;
        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            Triangle(outer[i], outer[j], hole[j], -Vector3.UnitZ);
            Triangle(outer[i], hole[j], hole[i], -Vector3.UnitZ);
            Triangle(outer[i] + up, outer[j] + up, hole[j] + up, Vector3.UnitZ);
            Triangle(outer[i] + up, hole[j] + up, hole[i] + up, Vector3.UnitZ);
            var outward = (outer[i] + outer[j]) / 2 - outerCenter;
            Triangle(outer[i], outer[j], outer[j] + up, outward);
            Triangle(outer[i], outer[j] + up, outer[i] + up, outward);
            var intoHole = holeCenter - (hole[i] + hole[j]) / 2;
            Triangle(hole[i], hole[j], hole[j] + up, intoHole);
            Triangle(hole[i], hole[j] + up, hole[i] + up, intoHole);
        }

        return new ElementMesh([.. positions], [.. indices]);

        void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0) (b, c) = (c, b);
            foreach (var p in new[] { a, b, c })
            {
                indices.Add(positions.Count / 3);
                positions.AddRange([p.X, p.Y, p.Z]);
            }
        }
    }

    /// <summary>Поворот вокруг вертикали на <paramref name="angle" />° и перенос на 105 м, как у модели в общих координатах.</summary>
    private static ElementMesh Place(ElementMesh mesh, float angle)
    {
        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        var positions = new float[mesh.Positions.Length];
        for (var i = 0; i < positions.Length; i += 3)
        {
            double x = mesh.Positions[i], y = mesh.Positions[i + 1];
            positions[i] = (float)(104956.49 + x * cos - y * sin);
            positions[i + 1] = (float)(15469.25 + x * sin + y * cos);
            positions[i + 2] = mesh.Positions[i + 2] + 32850;
        }

        return new ElementMesh(positions, mesh.Indices);
    }
}
