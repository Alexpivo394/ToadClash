using Shouldly;
using Alexpivo394.Revit.Clash.Revit;

namespace Alexpivo394.Revit.Clash.Tests.Revit;

public sealed class MeshBuilderTests
{
    private static readonly Vector3d Origin = new(1000, 2000, 0);

    [Test]
    public void MergesCoincidentVertices()
    {
        var builder = new MeshBuilder(Origin);
        builder.AddTriangle(new Vector3d(1000, 2000, 0), new Vector3d(1100, 2000, 0), new Vector3d(1100, 2100, 0));
        builder.AddTriangle(new Vector3d(1000.002, 2000, 0), new Vector3d(1100, 2100.001, 0), new Vector3d(1000, 2100, 0));

        var mesh = builder.Build();

        mesh.Positions.ShouldBe([0f, 0f, 0f, 100f, 0f, 0f, 100f, 100f, 0f, 0f, 100f, 0f]);
        mesh.Indices.ShouldBe([0, 1, 2, 0, 2, 3]);
    }

    [Test]
    public void DropsZeroAreaTriangles()
    {
        var builder = new MeshBuilder(Origin);
        builder.AddTriangle(new Vector3d(1000, 2000, 0), new Vector3d(1000.003, 2000, 0), new Vector3d(1000, 2100, 0));
        builder.AddTriangle(new Vector3d(1000, 2000, 50), new Vector3d(1010, 2000, 50), new Vector3d(1020, 2000, 50));
        builder.AddTriangle(new Vector3d(1000, 2000, 0), new Vector3d(1100, 2000, 0), new Vector3d(1100, 2100, 0));

        var mesh = builder.Build();

        mesh.TriangleCount.ShouldBe(1);
        mesh.Positions.ShouldBe([0f, 0f, 0f, 100f, 0f, 0f, 100f, 100f, 0f]);
        mesh.Indices.ShouldBe([0, 1, 2]);
    }

    [Test]
    public void IsClosedSince_ClosedTetrahedron_True_MissingFace_False()
    {
        var builder = new MeshBuilder(Origin);
        builder.AddTriangle(new Vector3d(0, 0, 0), new Vector3d(5, 0, 0), new Vector3d(0, 5, 0));
        var mark = builder.Mark();
        AddTetrahedron(builder, skipLast: true);

        builder.IsClosedSince(mark).ShouldBeFalse();

        var next = builder.Mark();
        AddTetrahedron(builder, skipLast: false, offset: 100);

        builder.IsClosedSince(next).ShouldBeTrue();
    }

    [Test]
    public void Rollback_RemovesTrianglesAndVerticesAddedAfterMark()
    {
        var builder = new MeshBuilder(Origin);
        builder.AddTriangle(new Vector3d(1000, 2000, 0), new Vector3d(1100, 2000, 0), new Vector3d(1100, 2100, 0));
        var mark = builder.Mark();
        builder.AddTriangle(new Vector3d(1000, 2000, 0), new Vector3d(1100, 2100, 0), new Vector3d(1000, 2100, 0));
        builder.AddTriangle(new Vector3d(1000, 2000, 0), new Vector3d(1000, 2100, 0), new Vector3d(1000, 2100, 50));

        builder.Rollback(mark);
        builder.AddTriangle(new Vector3d(1000, 2000, 0), new Vector3d(1100, 2100, 0), new Vector3d(1000, 2100, 0));

        var mesh = builder.Build();
        mesh.Positions.ShouldBe([0f, 0f, 0f, 100f, 0f, 0f, 100f, 100f, 0f, 0f, 100f, 0f]);
        mesh.Indices.ShouldBe([0, 1, 2, 0, 2, 3]);
    }

    private static void AddTetrahedron(MeshBuilder builder, bool skipLast, double offset = 0)
    {
        var a = new Vector3d(offset, 0, 0);
        var b = new Vector3d(offset + 10, 0, 0);
        var c = new Vector3d(offset, 10, 0);
        var d = new Vector3d(offset, 0, 10);
        builder.AddTriangle(a, c, b);
        builder.AddTriangle(a, b, d);
        builder.AddTriangle(a, d, c);
        if (!skipLast) builder.AddTriangle(b, c, d);
    }

    [Test]
    public void EmptyInput_BuildsEmptyMesh()
    {
        var mesh = new MeshBuilder(Origin).Build();

        mesh.Positions.ShouldBeEmpty();
        mesh.Indices.ShouldBeEmpty();
    }
}
