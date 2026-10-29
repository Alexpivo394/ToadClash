using Shouldly;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class ConfigurationJsonTests
{
    [Test]
    public void Json_RoundTrip()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var config = ConfigurationDefaults.FromDisciplines(
            [
                new DisciplineSet(a, "OV", "Pipes", [
                    new ConditionGroup([
                        new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, "OST_PipeCurves"),
                        new Condition(new PropertyRef(PropertyKind.Parameter, "Diameter", Guid.NewGuid()), ConditionOperator.Greater, "40,5")])]),
                new DisciplineSet(b, "AR", "Walls", [new ConditionGroup([])])
            ],
            [new DisciplineCell("OV", "AR", true, 25)],
            [new SetPairExclusion(a, b)],
            ignoreConnectedMep: false);

        var json = ConfigurationJson.Serialize(config);
        var restored = ConfigurationJson.Deserialize(json);

        json.ShouldContain("\"equals\"");
        json.ShouldContain("\"ignoreConnectedMep\"");
        restored.IgnoreConnectedMep.ShouldBeFalse();
        restored.Sets.Count.ShouldBe(2);
        restored.Sets[0].Id.ShouldBe(a);
        restored.Sets[0].AnyOf[0].All[0].Operator.ShouldBe(ConditionOperator.Equals);
        restored.Sets[0].AnyOf[0].All[1].ShouldBe(config.Sets[0].AnyOf[0].All[1]);
        restored.Matrix[0].ShouldBe(config.Matrix[0]);
        restored.Exclusions[0].ShouldBe(config.Exclusions[0]);
    }
}
