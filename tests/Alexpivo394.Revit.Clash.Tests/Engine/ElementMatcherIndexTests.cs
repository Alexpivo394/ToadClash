using System.Numerics;
using Alexpivo394.Revit.Clash.Engine.Identity;
using Alexpivo394.Revit.Clash.Geometry;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class ElementMatcherIndexTests
{
    private static ElementGeometry Element(string id, float x, string type = "T")
    {
        var mesh = TestScenes.Box(new Vector3(x, 0, 0), new Vector3(x + 100, 100, 100));
        return new(new(id, 1, "OST_PipeCurves", "F", type, null, null, []), mesh, Aabb.FromPositions(mesh.Positions), 0);
    }

    private static ModelGeometry Model(double originX, params ElementGeometry[] elements) =>
        new(new("model", "", 2022, DateTimeOffset.UnixEpoch, true, "3D", originX, 0, 0), elements, ["inventory-only"]);

    private static ModelIndex Index(ModelGeometry model)
    {
        using var stream = new MemoryStream();
        ModelGeometryFile.Write(stream, model);
        stream.Position = 0;
        return ModelGeometryFile.ReadIndex(stream);
    }

    [Test]
    public void Matcher_Index_SameAsGeometry()
    {
        // Сохранён kept, пересоздан old1 → new1 (тот же тип, рядом), old2 удалён, new2 другого типа добавлен; начала координат разные.
        var oldModel = Model(0, Element("kept", 0), Element("old1", 1000), Element("old2", 3000));
        var newModel = Model(500, Element("kept", -500), Element("new1", 510), Element("new2", 2500, type: "other"));

        var byGeometry = ElementMatcher.Compare(oldModel, newModel);
        var byIndex = ElementMatcher.Compare(Index(oldModel), Index(newModel));

        byIndex.ShouldBeEquivalentTo(byGeometry);
        byIndex.FallbackMatches.ShouldBe([new IdentityMatch("old1", "new1")]);
        (byIndex.Kept, byIndex.Removed, byIndex.Added).ShouldBe((1, 1, 1));
    }
}
