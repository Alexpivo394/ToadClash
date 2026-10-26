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
    public void EmptyInput_BuildsEmptyMesh()
    {
        var mesh = new MeshBuilder(Origin).Build();

        mesh.Positions.ShouldBeEmpty();
        mesh.Indices.ShouldBeEmpty();
    }
}
