using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class SetMembershipIndexTests
{
    private static readonly ModelParameters NoParameters = new(new Dictionary<string, IReadOnlyList<ParameterValue>>());

    private static ElementInfo Info(string uid, string category, string? family = null) => new(uid, uid.GetHashCode(), category, family, "T", null, null, []);

    private static Condition Cat(string category) => new(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, category);

    private static DisciplineSet Set(string discipline, string category) =>
        new(Guid.NewGuid(), discipline, category, [new ConditionGroup([Cat(category)])]);

    private static SetPreviewResult Preview(DisciplineSet set, IReadOnlyList<ElementsInput> models, int sample = 20) =>
        SetPreview.Count(ConfigurationDefaults.FromDisciplines([set], [], []), set.Id, models, sample);

    [Test]
    public void Evaluate_ElementsInput_SameAsModelInput()
    {
        var walls = Set("АР", "OST_Walls");
        var doors = Set("АР", "OST_Doors");
        var infos = new[] { Info("w1", "OST_Walls"), Info("d1", "OST_Doors"), Info("f1", "OST_Floors") };
        var header = new ModelHeader("m1", "", 2022, DateTimeOffset.UnixEpoch, true, "", 0, 0, 0);
        var geometry = new ModelGeometry(header, infos.Select(i => new ElementGeometry(i, new ElementMesh([], []), new Aabb(), 0)).ToArray(), []);
        var config = ConfigurationDefaults.FromDisciplines([walls, doors], [], []);

        var byModel = SetMembership.Evaluate(config, [new ModelInput("АР", geometry, NoParameters)]);
        var byElements = SetMembership.Evaluate(config, [new ModelInput("АР", geometry, NoParameters).ToElements()]);

        foreach (var info in infos) byElements.SetsOf("m1", info.UniqueId).ShouldBe(byModel.SetsOf("m1", info.UniqueId));
        byElements.SetsOf("m1", "w1").ShouldBe([walls.Id]);
    }

    [Test]
    public void Preview_CountsMatchedAndSamples()
    {
        var infos = Enumerable.Range(0, 50).Select(i => Info($"e{i}", i < 20 ? "OST_Walls" : "OST_Floors")).ToArray();
        var model = new ElementsInput("АР", "m1", infos, NoParameters);

        var result = Preview(Set("АР", "OST_Walls"), [model]);

        result.Matched.ShouldBe(20);
        result.Total.ShouldBe(50);
        result.Sample.Count.ShouldBe(20);
        result.Sample[0].Category.ShouldBe("OST_Walls");
        result.Sample[0].ModelKey.ShouldBe("m1");
    }

    [Test]
    public void Preview_SampleLimited()
    {
        var infos = Enumerable.Range(0, 30).Select(i => Info($"e{i}", "OST_Walls")).ToArray();

        var result = Preview(Set("АР", "OST_Walls"), [new ElementsInput("АР", "m1", infos, NoParameters)], sample: 5);

        result.Matched.ShouldBe(30);
        result.Sample.Count.ShouldBe(5);
    }

    [Test]
    public void Preview_OtherDisciplineModelsNotCounted()
    {
        var ar = new ElementsInput("АР", "m1", [Info("w1", "OST_Walls")], NoParameters);
        var kr = new ElementsInput("КР", "m2", [Info("w2", "OST_Walls"), Info("w3", "OST_Walls")], NoParameters);

        var result = Preview(Set("ар", "OST_Walls"), [ar, kr]);

        result.Matched.ShouldBe(1);
        result.Total.ShouldBe(1);
    }
}
