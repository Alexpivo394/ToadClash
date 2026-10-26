using Shouldly;
using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

public sealed class AabbTests
{
    [Test]
    public void Overlaps_TouchingFaces_True()
    {
        var a = new Aabb(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
        var b = new Aabb(new Vector3(1, 0, 0), new Vector3(2, 1, 1));

        a.Overlaps(b).ShouldBeTrue();
    }

    [Test]
    public void Overlaps_Separated_False()
    {
        var a = new Aabb(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
        var b = new Aabb(new Vector3(1.5f, 0, 0), new Vector3(2, 1, 1));

        a.Overlaps(b).ShouldBeFalse();
    }

    [Test]
    public void FromPositions_ComputesMinMax()
    {
        var box = Aabb.FromPositions([1, 5, -3, -2, 4, 7, 0, 9, 0]);

        box.Min.ShouldBe(new Vector3(-2, 4, -3));
        box.Max.ShouldBe(new Vector3(1, 9, 7));
    }

    [Test]
    public void Union_WithEmpty_ReturnsOther()
    {
        var a = new Aabb(new Vector3(0, 0, 0), new Vector3(1, 1, 1));

        Aabb.Empty.Union(a).ShouldBe(a);
    }

    [Test]
    public void Expand_GrowsAllSides()
    {
        var a = new Aabb(new Vector3(0, 0, 0), new Vector3(1, 1, 1)).Expand(2);

        a.Min.ShouldBe(new Vector3(-2, -2, -2));
        a.Max.ShouldBe(new Vector3(3, 3, 3));
        a.Center.ShouldBe(new Vector3(0.5f, 0.5f, 0.5f));
    }
}
