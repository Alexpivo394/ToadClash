using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class PairFilterTests
{
    private static readonly (double, double, double) Zero = (0, 0, 0);
    private static readonly SelectionSet PipeSet = TestScenes.CategorySet("OV", "OST_PipeCurves");
    private static readonly SelectionSet WallSet = TestScenes.CategorySet("AR", "OST_Walls");

    private static ElementMesh Cube() => TestScenes.Box(Vector3.Zero, new Vector3(10));

    private static (ClashScene Scene, PairFilter Filter) Setup(ClashConfiguration config, params ModelInput[] models)
    {
        var scene = ClashScene.Build(models, SetMembership.Evaluate(config, models));
        return (scene, new PairFilter(scene, config));
    }

    private static ClashConfiguration Config(params SetPairExclusion[] exclusions) =>
        TestScenes.Config([PipeSet, WallSet], [TestScenes.Cell("OV", "AR", true, 15)], exclusions);

    private static ModelInput[] PipeAndWall() =>
    [
        TestScenes.Model("ov", "OV", Zero, [new("p", "OST_PipeCurves", Cube())]),
        TestScenes.Model("ar", "AR", Zero, [new("w", "OST_Walls", Cube())])
    ];

    [Test]
    public void DisabledCell_NoCandidates()
    {
        var config = Config() with { Matrix = [TestScenes.Cell("OV", "AR", false)] };
        var (scene, filter) = Setup(config, PipeAndWall());

        BroadPhase.Find(scene, config, filter, 2).ShouldBeEmpty();
    }

    [Test]
    public void MissingCell_NoCandidates()
    {
        var config = Config() with { Matrix = [] };
        var (scene, filter) = Setup(config, PipeAndWall());

        BroadPhase.Find(scene, config, filter, 2).ShouldBeEmpty();
    }

    [Test]
    public void ExcludedSetPair_NoCandidates()
    {
        var config = Config(new SetPairExclusion(WallSet.Id, PipeSet.Id));
        var (scene, filter) = Setup(config, PipeAndWall());

        BroadPhase.Find(scene, config, filter, 2).ShouldBeEmpty();
    }

    [Test]
    public void PartiallyExcludedSets_StillCandidate()
    {
        var pipeSet2 = TestScenes.CategorySet("OV", "OST_PipeCurves");
        var config = Config(new SetPairExclusion(PipeSet.Id, WallSet.Id)) with { Sets = [PipeSet, pipeSet2, WallSet] };
        var (scene, filter) = Setup(config, PipeAndWall());

        var pairs = BroadPhase.Find(scene, config, filter, 2);

        pairs.Count.ShouldBe(1);
        pairs[0].ToleranceMm.ShouldBe(15f);
    }

    [Test]
    public void ConnectedMep_Skipped()
    {
        var config = Config() with { Matrix = [TestScenes.Cell("OV", "OV", true)] };
        var model = TestScenes.Model("ov", "OV", Zero, [
            new("a", "OST_PipeCurves", Cube(), ["b"]),
            new("b", "OST_PipeCurves", Cube())]);
        var (scene, filter) = Setup(config, model);

        BroadPhase.Find(scene, config, filter, 2).ShouldBeEmpty();
        scene.AreConnected(0, 1).ShouldBeTrue();
        scene.AreConnected(1, 0).ShouldBeTrue();

        var notIgnoring = config with { IgnoreConnectedMep = false };
        BroadPhase.Find(scene, notIgnoring, new PairFilter(scene, notIgnoring), 2).Count.ShouldBe(1);
    }

    [Test]
    public void NestedComponents_OfSameRoot_Skipped()
    {
        var config = Config() with { Matrix = [TestScenes.Cell("OV", "OV", true)] };
        var model = TestScenes.Model("ov", "OV", Zero, [
            new("parent", "OST_PipeCurves", Cube()),
            new("child", "OST_PipeCurves", Cube(), Root: "parent"),
            new("grandchild", "OST_PipeCurves", Cube(), Root: "parent")]);
        var (scene, filter) = Setup(config, model);

        BroadPhase.Find(scene, config, filter, 2).ShouldBeEmpty();
    }

    [Test]
    public void NestedComponents_DifferentRootsOrModels_Checked()
    {
        var config = Config() with { Matrix = [TestScenes.Cell("OV", "OV", true)] };
        var (scene, filter) = Setup(config,
            TestScenes.Model("ov", "OV", Zero, [
                new("parent", "OST_PipeCurves", Cube()),
                new("other", "OST_PipeCurves", Cube(), Root: "another")]),
            TestScenes.Model("ov2", "OV", Zero, [new("child", "OST_PipeCurves", Cube(), Root: "parent")]));

        // Три пары: родитель и чужой корень в одной модели, а одинаковый UniqueId корня в другой модели - другое изделие.
        BroadPhase.Find(scene, config, filter, 2).Count.ShouldBe(3);
    }

    [Test]
    public void ConnectedIds_AcrossModels_AreNotConnected()
    {
        var config = Config();
        var (scene, filter) = Setup(config,
            TestScenes.Model("ov", "OV", Zero, [new("p", "OST_PipeCurves", Cube(), ["w"])]),
            TestScenes.Model("ar", "AR", Zero, [new("w", "OST_Walls", Cube())]));

        scene.AreConnected(0, 1).ShouldBeFalse();
        BroadPhase.Find(scene, config, filter, 2).Count.ShouldBe(1);
    }

    [Test]
    public void SameElement_NeverPaired()
    {
        var config = Config();
        var (_, filter) = Setup(config, TestScenes.Model("ov", "OV", Zero, [new("p", "OST_PipeCurves", Cube())]));

        filter.Passes(0, 0, 0, 0, out _).ShouldBeFalse();
    }
}
