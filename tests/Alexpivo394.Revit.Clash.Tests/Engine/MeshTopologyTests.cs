using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class MeshTopologyTests
{
    private static readonly ElementMesh Cube = TestScenes.Box(Vector3.Zero, new Vector3(10));

    private static int[] Inverted(int[] indices)
    {
        var result = (int[])indices.Clone();
        for (var t = 0; t < result.Length; t += 3) (result[t + 1], result[t + 2]) = (result[t + 2], result[t + 1]);
        return result;
    }

    [Test]
    public void OutwardClosedMesh_KeptAsIs()
    {
        var indices = MeshTopology.Orient(Cube.Positions, Cube.Indices, out var closed);

        closed.ShouldBeTrue();
        indices.ShouldBeSameAs(Cube.Indices);
    }

    [Test]
    public void InvertedClosedMesh_FlippedCopy_InputUntouched()
    {
        var inverted = Inverted(Cube.Indices);
        var before = (int[])inverted.Clone();

        var indices = MeshTopology.Orient(Cube.Positions, inverted, out var closed);

        closed.ShouldBeTrue();
        indices.ShouldBe(Cube.Indices);
        inverted.ShouldBe(before);
    }

    [Test]
    public void EachShellOrientedSeparately()
    {
        var other = TestScenes.Box(new Vector3(100), new Vector3(110));
        float[] positions = [.. Cube.Positions, .. other.Positions];
        int[] expected = [.. Cube.Indices, .. other.Indices.Select(i => i + 8)];
        int[] mixed = [.. Cube.Indices, .. Inverted(other.Indices).Select(i => i + 8)];

        MeshTopology.Orient(positions, mixed, out var closed).ShouldBe(expected);
        closed.ShouldBeTrue();
    }

    [Test]
    public void SeparateVerticesPerFace_WeldedAndClosed()
    {
        // Каждый треугольник со своими вершинами, как при потреугольной выгрузке.
        var positions = Cube.Indices.SelectMany(i => Cube.Positions.Skip(i * 3).Take(3)).ToArray();
        var indices = Inverted(Enumerable.Range(0, Cube.Indices.Length).ToArray());

        var oriented = MeshTopology.Orient(positions, indices, out var closed);

        closed.ShouldBeTrue();
        oriented.ShouldBe(Enumerable.Range(0, Cube.Indices.Length).ToArray());
    }

    [Test]
    public void BodiesTouchingAlongEdge_Closed_EachOrientedSeparately()
    {
        // Корпус и фланец касаются по ребру: после сварки ребро общее для четырёх треугольников.
        var other = TestScenes.Box(new Vector3(10, 10, 0), new Vector3(20, 20, 10));
        float[] positions = [.. Cube.Positions, .. other.Positions];
        int[] expected = [.. Cube.Indices, .. other.Indices.Select(i => i + 8)];
        int[] mixed = [.. Cube.Indices, .. Inverted(other.Indices).Select(i => i + 8)];

        var oriented = MeshTopology.Orient(positions, mixed, out var closed);

        closed.ShouldBeTrue();
        oriented.ShouldBe(expected);
    }

    [Test]
    public void BodiesTouchingAlongEdge_WithHole_Open()
    {
        var other = TestScenes.Box(new Vector3(10, 10, 0), new Vector3(20, 20, 10));
        float[] positions = [.. Cube.Positions, .. other.Positions];
        int[] indices = [.. Cube.Indices, .. other.Indices[..^3].Select(i => i + 8)];

        MeshTopology.Orient(positions, indices, out var closed);

        closed.ShouldBeFalse();
    }

    [Test]
    public void Shells_NumberedPerBody_OpenMeshHasNone()
    {
        var other = TestScenes.Box(new Vector3(10, 10, 0), new Vector3(20, 20, 10));
        float[] positions = [.. Cube.Positions, .. other.Positions];
        int[] indices = [.. Cube.Indices, .. other.Indices.Select(i => i + 8)];

        MeshTopology.Orient(Cube.Positions, Cube.Indices, out _, out var one);
        MeshTopology.Orient(positions, indices, out _, out var two);
        MeshTopology.Orient(Cube.Positions, Cube.Indices[..^6], out _, out var open);

        one.ShouldNotBeNull().Distinct().ShouldBe([0]);
        two.ShouldNotBeNull()[..12].Distinct().ShouldHaveSingleItem();
        two[12..].Distinct().ShouldHaveSingleItem();
        two[0].ShouldNotBe(two[12]);
        open.ShouldBeNull();
    }

    [Test]
    public void OpenMesh_KeepsWinding()
    {
        var open = Inverted(Cube.Indices[..^6]);

        var indices = MeshTopology.Orient(Cube.Positions, open, out var closed);

        closed.ShouldBeFalse();
        indices.ShouldBeSameAs(open);
    }
}
