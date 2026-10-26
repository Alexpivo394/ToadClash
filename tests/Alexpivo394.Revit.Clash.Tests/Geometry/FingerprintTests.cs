using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

public sealed class FingerprintTests
{
    private static readonly int[] Indices = [0, 1, 2];

    private static ModelHeader Header(double x = 0, double y = 0, double z = 0) =>
        new("m", "m.rvt", 2025, DateTimeOffset.UnixEpoch, true, "3D", x, y, z);

    private static ElementMesh Mesh(params float[] positions) => new(positions, Indices);

    private static readonly float[] Base = [10, 20, 30, 110, 20, 30, 10, 120, 30];

    [Test]
    public void Fingerprint_SameGeometry_Equal()
    {
        Fingerprint.Compute(Mesh(Base), Header()).ShouldBe(Fingerprint.Compute(Mesh(Base), Header()));
    }

    [Test]
    public void Fingerprint_MovedBy1mm_Different()
    {
        var moved = Base.Select((v, i) => i == 0 ? v + 1 : v).ToArray();

        Fingerprint.Compute(Mesh(moved), Header()).ShouldNotBe(Fingerprint.Compute(Mesh(Base), Header()));
    }

    [Test]
    public void Fingerprint_JitterBelow0_05mm_Equal()
    {
        var jittered = Base.Select((v, i) => v + (i % 2 == 0 ? 0.04f : -0.04f)).ToArray();

        Fingerprint.Compute(Mesh(jittered), Header()).ShouldBe(Fingerprint.Compute(Mesh(Base), Header()));
    }

    [Test]
    public void Fingerprint_SameShapeDifferentOrigin_SameAbsolute_Equal()
    {
        var shifted = Base.Select((v, i) => i % 3 == 0 ? v - 1000 : v).ToArray();

        Fingerprint.Compute(Mesh(shifted), Header(1000)).ShouldBe(Fingerprint.Compute(Mesh(Base), Header()));
    }
}
