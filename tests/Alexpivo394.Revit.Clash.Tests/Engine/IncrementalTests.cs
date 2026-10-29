using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class IncrementalTests
{
    private static readonly (double, double, double) Zero = (0, 0, 0);

    // Стена АР на x 0…1000; трубы ВК заходят в неё на 100 мм (p1) и на 50 мм (p2); p3 стену не задевает.
    private static ModelInput[] Models(float p1 = 900, string marked = "yes") =>
    [
        WithFingerprints(TestScenes.Model("ar", "AR", Zero, [new("wall", "OST_Walls", Cube(0))]), new Dictionary<string, string> { ["wall"] = marked }),
        WithFingerprints(TestScenes.Model("vk", "VK", Zero,
        [
            new("p1", "OST_PipeCurves", Cube(p1)),
            new("p2", "OST_PipeCurves", Cube(950)),
            new("p3", "OST_PipeCurves", Cube(5000))
        ]), new Dictionary<string, string>())
    ];

    private static readonly SelectionSet ArAll = TestScenes.CategorySet("AR", "OST_Walls");
    private static readonly SelectionSet VkAll = TestScenes.CategorySet("VK", "OST_PipeCurves");

    private static readonly SelectionSet ArMarked = new(Guid.NewGuid(), ConfigurationDefaults.DisciplineGroupId("AR"), "Отмеченные",
    [
        new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Parameter, "Отметка"), ConditionOperator.Equals, "yes")])
    ]);

    private static ClashConfiguration Config(SelectionSet ar, double tolerance = 20) =>
        TestScenes.Config([ar, VkAll], [TestScenes.Cell("AR", "VK", true, tolerance)], []);

    private static ElementMesh Cube(float x) => TestScenes.Box(new Vector3(x, 0, 0), new Vector3(x + 1000, 1000, 1000));

    private static ModelInput WithFingerprints(ModelInput model, IReadOnlyDictionary<string, string> marks)
    {
        var header = model.Geometry.Header;
        var elements = model.Geometry.Elements.Select(e => e with { Fingerprint = Fingerprint.Compute(e.Mesh, header) }).ToList();
        var parameters = marks.ToDictionary(p => p.Key, p => (IReadOnlyList<ParameterValue>) [new ParameterValue("Отметка", null, p.Value, null)]);
        return model with { Geometry = new ModelGeometry(header, elements, model.Geometry.Inventory), Parameters = new ModelParameters(parameters) };
    }

    private static IncrementalBase Base(IReadOnlyList<ModelInput> models, ClashConfiguration config, ClashRunResult result) =>
        IncrementalBase.From(
            models.Select(m => m.ToElements()).ToList(),
            models.SelectMany(m => m.Geometry.Elements.Select(e => (Ref: new ElementRef(m.Geometry.Header.ModelKey, e.Info.UniqueId), e.Fingerprint)))
                .ToDictionary(x => x.Ref, x => x.Fingerprint),
            config,
            result.Hits);

    private static ClashRunResult Run(IReadOnlyList<ModelInput> models, ClashConfiguration config, IncrementalBase? previous = null) =>
        new ClashEngine(new EngineOptions(2)).Run(models, config, CancellationToken.None, previous);

    [Test]
    public void Engine_Incremental_UnchangedPairsReused()
    {
        var config = Config(ArAll);
        var models = Models();
        var first = Run(models, config);

        var second = Run(models, config, Base(models, config, first));

        second.Hits.ShouldBe(first.Hits, ignoreOrder: false, comparer: HitComparer.Instance);
        second.Stats.ReusedPairs.ShouldBe(second.Stats.CandidatePairs);
        second.Stats.ReusedPairs.ShouldBe(2);
        first.Stats.ReusedPairs.ShouldBe(0);
    }

    [Test]
    public void Engine_Incremental_MovedElementRechecked()
    {
        var config = Config(ArAll);
        var before = Models();
        var first = Run(before, config);
        first.Hits.Select(h => h.B.UniqueId).ShouldBe(["p1", "p2"]);

        var after = Models(p1: 990);
        var second = Run(after, config, Base(before, config, first));

        second.Hits.Select(h => h.B.UniqueId).ShouldBe(["p2"]);
        second.Stats.ReusedPairs.ShouldBe(1);
    }

    [Test]
    public void Engine_Incremental_MembershipChanged_Rechecked()
    {
        var config = Config(ArMarked);
        var before = Models(marked: "no");
        var first = Run(before, config);
        first.Hits.ShouldBeEmpty();

        var after = Models(marked: "yes");
        var second = Run(after, config, Base(before, config, first));

        second.Hits.Select(h => h.B.UniqueId).ShouldBe(["p1", "p2"]);
        second.Stats.ReusedPairs.ShouldBe(0);
    }

    [Test]
    public void Engine_Incremental_ChecksFollowCurrentTolerance()
    {
        var config = Config(ArAll, tolerance: 60);
        var models = Models();
        var full = Run(models, config);

        var reused = Run(models, config, Base(models, config, full));

        full.Hits.ShouldHaveSingleItem().B.UniqueId.ShouldBe("p1");
        reused.Hits.Select(h => h.Checks).ShouldBe(full.Hits.Select(h => h.Checks));
    }

    [Test]
    public void Recheck_PreviousConfigFindsPair()
    {
        var models = Models();
        var pair = (new ElementRef("ar", "wall"), new ElementRef("vk", "p2"));
        var engine = new ClashEngine(new EngineOptions(2));

        var strict = engine.Recheck(models, Config(ArAll, tolerance: 20), [pair], CancellationToken.None);
        var loose = engine.Recheck(models, Config(ArAll, tolerance: 100), [pair], CancellationToken.None);

        var hit = strict.ShouldHaveSingleItem();
        (hit.A, hit.B).ShouldBe(pair);
        hit.DepthMm.ShouldBe(50f, 1f);
        loose.ShouldBeEmpty();
    }

    [Test]
    public void Recheck_ExcludedSetPair_NoHit()
    {
        var config = Config(ArAll) with { Exclusions = [new SetPairExclusion(ArAll.Id, VkAll.Id)] };

        new ClashEngine(new EngineOptions(2))
            .Recheck(Models(), config, [(new ElementRef("ar", "wall"), new ElementRef("vk", "p1"))], CancellationToken.None)
            .ShouldBeEmpty();
    }

    [Test]
    public void Recheck_MissingElement_NoHit() =>
        new ClashEngine(new EngineOptions(2))
            .Recheck(Models(), Config(ArAll), [(new ElementRef("ar", "wall"), new ElementRef("vk", "gone"))], CancellationToken.None)
            .ShouldBeEmpty();

    private sealed class HitComparer : IEqualityComparer<ClashHit>
    {
        public static readonly HitComparer Instance = new();

        public bool Equals(ClashHit? x, ClashHit? y) =>
            x is not null && y is not null && x.A == y.A && x.B == y.B && x.PointX == y.PointX && x.PointY == y.PointY && x.PointZ == y.PointZ &&
            x.DepthMm == y.DepthMm && x.Checks.SequenceEqual(y.Checks);

        public int GetHashCode(ClashHit obj) => HashCode.Combine(obj.A, obj.B);
    }
}
