using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

public sealed class ElementMeshTests
{
    [Test]
    public void ElementMesh_IndexOutOfRange_Throws()
    {
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];

        Should.Throw<ArgumentException>(() => new ElementMesh(positions, [0, 1, 3]));
        Should.Throw<ArgumentException>(() => new ElementMesh(positions, [0, 1, -1]));
        Should.NotThrow(() => new ElementMesh(positions, [0, 1, 2]));
    }
}
