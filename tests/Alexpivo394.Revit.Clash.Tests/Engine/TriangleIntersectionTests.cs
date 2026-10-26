using Alexpivo394.Revit.Clash.Engine;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class TriangleIntersectionTests
{
    private static Vec3d V(double x, double y, double z) => new(x, y, z);

    // Горизонтальный треугольник z = 0 с нормалью +z.
    private static readonly Vec3d[] Floor = [V(0, 0, 0), V(10, 0, 0), V(0, 10, 0)];

    private static bool Test(Vec3d[] t, Vec3d[] u, out Vec3d p, out Vec3d q) =>
        TriangleIntersection.Intersect(t[0], t[1], t[2], u[0], u[1], u[2], out p, out q);

    [Test]
    public void PiercingTriangles_Intersect_WithSegmentInBothPlanes()
    {
        Vec3d[] wall = [V(2, -5, -5), V(2, 15, -5), V(2, 5, 5)];

        Test(Floor, wall, out var p, out var q).ShouldBeTrue();

        p.Z.ShouldBe(0, 1e-9);
        q.Z.ShouldBe(0, 1e-9);
        p.X.ShouldBe(2, 1e-9);
        q.X.ShouldBe(2, 1e-9);
        Math.Abs(p.Y - q.Y).ShouldBe(8, 1e-9);
    }

    [Test]
    public void Symmetric()
    {
        Vec3d[] wall = [V(2, -5, -5), V(2, 15, -5), V(2, 5, 5)];

        Test(wall, Floor, out _, out _).ShouldBeTrue();
    }

    [Test]
    public void SeparatedTriangles_DoNotIntersect()
    {
        Vec3d[] wall = [V(20, -5, -5), V(20, 15, -5), V(20, 5, 5)];

        Test(Floor, wall, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void CrossingPlanesButDisjointIntervals_DoNotIntersect()
    {
        Vec3d[] wall = [V(2, 11, -5), V(2, 20, -5), V(2, 15, 5)];

        Test(Floor, wall, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void Coplanar_DoNotIntersect()
    {
        Vec3d[] other = [V(1, 1, 0), V(8, 1, 0), V(1, 8, 0)];

        Test(Floor, other, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void SharedEdge_DoNotIntersect()
    {
        Vec3d[] hinge = [V(0, 0, 0), V(10, 0, 0), V(5, 0, 7)];

        Test(Floor, hinge, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void VertexTouch_DoNotIntersect()
    {
        Vec3d[] above = [V(2, 2, 0), V(2, 6, 5), V(6, 2, 5)];

        Test(Floor, above, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void EdgeRestingOnFaceFromFront_DoNotIntersect()
    {
        // Ребро лежит на треугольнике, сам треугольник над ним (со стороны нормали +z).
        Vec3d[] above = [V(1, 2, 0), V(6, 2, 0), V(3, 2, 5)];

        Test(Floor, above, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void EdgeRestingOnFaceFromBehind_Intersect()
    {
        // Ребро лежит на треугольнике, сам треугольник за ним (против нормали): сетка уходит внутрь тела.
        Vec3d[] below = [V(1, 2, 0), V(6, 2, 0), V(3, 2, -5)];

        Test(Floor, below, out var p, out var q).ShouldBeTrue();
        Math.Abs(p.X - q.X).ShouldBe(5, 1e-9);
    }

    [Test]
    public void NearlyTouchingWithinEpsilon_DoNotIntersect()
    {
        Vec3d[] above = [V(2, 2, 0.00005), V(2, 6, 5), V(6, 2, 5)];

        Test(Floor, above, out _, out _).ShouldBeFalse();
    }
}
