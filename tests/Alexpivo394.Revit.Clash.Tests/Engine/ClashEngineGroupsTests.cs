using System.Numerics;
using Alexpivo394.Revit.Clash.Engine;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Engine;

public sealed class ClashEngineGroupsTests
{
    private static readonly (double, double, double) Zero = (0, 0, 0);
    private static readonly SetGroup Ar = ConfigurationDefaults.DisciplineGroup("AR");
    private static readonly SetGroup Vk = ConfigurationDefaults.DisciplineGroup("VK");
    private static readonly SetGroup Ramps = new(Guid.NewGuid(), "Проезды", new GroupSource(["AR"], []));

    private static readonly SelectionSet ArAll = new(Guid.NewGuid(), Ar.Id, "Всё", [ConfigurationDefaults.Everything]);
    private static readonly SelectionSet ArWalls = TestScenes.CategorySet("AR", "OST_Walls");
    private static readonly SelectionSet VkAll = new(Guid.NewGuid(), Vk.Id, "Всё", [ConfigurationDefaults.Everything]);
    private static readonly SelectionSet RampSet = new(Guid.NewGuid(), Ramps.Id, "Проезды",
        [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, "OST_GenericModel")])]);

    private static ElementMesh Cube(float x, float size = 1000) => TestScenes.Box(new Vector3(x, 0, 0), new Vector3(x + size, size, size));

    // Проезд — куб у начала; труба ВК заходит в него на 100 мм; стена АР касается проезда с другой стороны и тоже пересекает его на 100 мм.
    private static ModelInput[] Models() =>
    [
        TestScenes.Model("ar", "AR", Zero, [new("ramp", "OST_GenericModel", Cube(0)), new("wall", "OST_Walls", Cube(-900))]),
        TestScenes.Model("vk", "VK", Zero, [new("pipe", "OST_PipeCurves", Cube(900))])
    ];

    private static ClashRunResult Run(ClashConfiguration config) => new ClashEngine(new EngineOptions(2)).Run(Models(), config, CancellationToken.None);

    [Test]
    public void Engine_ElementInTwoGroups_OneHitPerPairWithChecks()
    {
        var strict = new ClashConfiguration([Ar, Ramps, Vk], [ArAll, RampSet, VkAll],
            [new MatrixCell(Ar.Id, Vk.Id, true, 20), new MatrixCell(Ramps.Id, Vk.Id, true, 500)], []);
        var both = strict with { Matrix = [new MatrixCell(Ar.Id, Vk.Id, true, 20), new MatrixCell(Vk.Id, Ramps.Id, true, 50)] };

        var hit = Run(strict).Hits.ShouldHaveSingleItem();
        var twice = Run(both).Hits.ShouldHaveSingleItem();

        (hit.A.UniqueId, hit.B.UniqueId).ShouldBe(("ramp", "pipe"));
        hit.DepthMm.ShouldBe(100f, 1f);
        hit.Checks.ShouldBe([CheckRef.Of(Ar.Id, Vk.Id)]);
        twice.Checks.OrderBy(c => c.Key).ShouldBe(new[] { CheckRef.Of(Ar.Id, Vk.Id), CheckRef.Of(Ramps.Id, Vk.Id) }.OrderBy(c => c.Key));
    }

    [Test]
    public void Engine_CustomGroupAgainstDiscipline()
    {
        var config = new ClashConfiguration([Ar, Ramps, Vk], [ArWalls, RampSet, VkAll],
            [new MatrixCell(Ramps.Id, Vk.Id, true), new MatrixCell(Ar.Id, Vk.Id, false), new MatrixCell(Ramps.Id, Ar.Id, false)], []);

        var hit = Run(config).Hits.ShouldHaveSingleItem();

        (hit.A.UniqueId, hit.B.UniqueId).ShouldBe(("ramp", "pipe"));
        hit.Checks.ShouldBe([CheckRef.Of(Ramps.Id, Vk.Id)]);
    }

    [Test]
    public void Engine_GroupWithItselfAndAcrossGroups()
    {
        var config = new ClashConfiguration([Ar, Ramps, Vk], [ArAll, RampSet, VkAll],
            [new MatrixCell(Ar.Id, Ar.Id, true), new MatrixCell(Ramps.Id, Ar.Id, true)], []);

        var hits = Run(config).Hits;

        // Стена × проезд: оба в АР (клетка АР × АР) и проезд в «Проездах» против стены АР — одна коллизия, две проверки.
        var hit = hits.ShouldHaveSingleItem();
        hit.Checks.OrderBy(c => c.Key).ShouldBe(new[] { CheckRef.Of(Ar.Id, Ar.Id), CheckRef.Of(Ramps.Id, Ar.Id) }.OrderBy(c => c.Key));
        CheckRef.Of(Vk.Id, Ar.Id).ShouldBe(CheckRef.Of(Ar.Id, Vk.Id));
        CheckRef.Of(Ar.Id, Vk.Id).Key.ShouldBe(string.CompareOrdinal(Ar.Id.ToString("N"), Vk.Id.ToString("N")) < 0
            ? $"{Ar.Id:N}|{Vk.Id:N}"
            : $"{Vk.Id:N}|{Ar.Id:N}");
    }
}
