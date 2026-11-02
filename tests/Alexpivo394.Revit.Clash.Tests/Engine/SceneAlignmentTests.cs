using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class SceneAlignmentTests
{
    private static readonly SelectionSet PipeSet = TestScenes.CategorySet("OV", "OST_PipeCurves");
    private static readonly SelectionSet WallSet = TestScenes.CategorySet("AR", "OST_Walls");

    private static ClashConfiguration Config() => TestScenes.Config([PipeSet, WallSet], [TestScenes.Cell("OV", "AR", true)], []);

    private static ElementMesh Cube(float x, float y = 0, float z = 0, float size = 1000) =>
        TestScenes.Box(new Vector3(x, y, z), new Vector3(x + size, y + size, z + size));

    private static ModelInput Pipes(string key, (double, double, double) origin, params float[] xs) =>
        TestScenes.Model(key, "OV", origin, xs.Select((x, i) => new TestScenes.Item($"p{i}", "OST_PipeCurves", Cube(x))));

    private static ModelInput Walls(string key, (double, double, double) origin, params float[] xs) =>
        TestScenes.Model(key, "AR", origin, xs.Select((x, i) => new TestScenes.Item($"w{i}", "OST_Walls", Cube(x))));

    [Test]
    public void Origin_SameForAnyModelOrder()
    {
        var a = Pipes("ov", (1924000, 1490000, 142000), -50000, 0, 20000);
        var b = Walls("ar", (1789000, 1482000, 142000), 100000, 130000, 160000);

        SceneAlignment.Origin([a, b]).ShouldBe(SceneAlignment.Origin([b, a]));
    }

    [Test]
    public void FarModel_Throws_WithKeyAndDistance()
    {
        var near = Pipes("ov", (0, 0, 0), 0);
        var far = Walls("ar", (6e9, 6e9, 0), 0, 5000);

        var error = Should.Throw<ModelsNotAlignedException>(() => SceneAlignment.Origin([near, far]));

        error.Models.Count.ShouldBe(1);
        error.Models[0].ModelKey.ShouldBe("ov");
        error.Models[0].DistanceMm.ShouldBeGreaterThan(8e9);
        error.Message.ShouldContain("ov");
    }

    [Test]
    public void StrayElement_DoesNotMakeModelMisaligned()
    {
        var xs = Enumerable.Range(0, 200).Select(i => i * 1500f).Append(50_000_000f).ToArray();
        var withStray = Walls("ar", (0, 0, 0), xs);
        var other = Pipes("ov", (0, 0, 0), 0, 3000, 6000);

        Should.NotThrow(() => SceneAlignment.Origin([withStray, other]));
    }

    [Test]
    public void SingleModel_And_EmptyModel_DoNotThrow()
    {
        var empty = TestScenes.Model("empty", "OV", (6e9, 6e9, 0), []);

        Should.NotThrow(() => SceneAlignment.Origin([Walls("ar", (0, 0, 0), 0)]));
        Should.NotThrow(() => SceneAlignment.Origin([Walls("ar", (0, 0, 0), 0), empty]));
        Should.NotThrow(() => SceneAlignment.Origin([]));
    }

    [Test]
    public void ClashScene_FarModel_Throws()
    {
        var config = Config();
        ModelInput[] models = [Pipes("ov", (0, 0, 0), 0), Walls("ar", (6e9, 6e9, 0), 0, 5000)];

        Should.Throw<ModelsNotAlignedException>(() => ClashScene.Build(models, SetMembership.Evaluate(config, models)));
    }

    [Test]
    public void FarFromSharedOrigin_SameSite_KeepsMillimeterAccuracy()
    {
        var pipe = Pipes("ov", (6e9, 6e9, 1e5), 0);
        var wall = Walls("ar", (6e9 + 150000, 6e9, 1e5), 975 - 150000);

        var result = new ClashEngine(new EngineOptions(2)).Run([pipe, wall], Config(), CancellationToken.None);

        result.Hits.Count.ShouldBe(1);
        result.Hits[0].DepthMm.ShouldBe(25f, 1f);
    }
}
