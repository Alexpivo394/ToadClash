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
        restored.GroupExclusions.ShouldBeEmpty();
    }

    [Test]
    public void Json_GroupExclusions_RoundTripAndOptional()
    {
        var group = ConfigurationDefaults.DisciplineGroupId("AR");
        var set = Guid.NewGuid();
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("AR")], [], [], []) { GroupExclusions = [new SetGroupExclusion(set, group)] };

        var json = ConfigurationJson.Serialize(config);

        json.ShouldContain("\"groupExclusions\"");
        ConfigurationJson.Deserialize(json).GroupExclusions.ShouldBe([new SetGroupExclusion(set, group)]);
        // Конфигурация, сохранённая до появления исключений с группой, читается с пустым списком.
        ConfigurationJson.Deserialize("""{"groups":[],"sets":[],"matrix":[],"exclusions":[]}""").GroupExclusions.ShouldBeEmpty();
        Should.Throw<System.Text.Json.JsonException>(() =>
            ConfigurationJson.Deserialize("""{"groups":[],"sets":[],"matrix":[],"exclusions":[],"groupExclusions":null}"""));
    }

    [Test]
    [Arguments("""{"groups":null,"sets":[],"matrix":[],"exclusions":[]}""", "groups")]
    [Arguments("""{"groups":[null],"sets":[],"matrix":[],"exclusions":[]}""", "groups")]
    [Arguments("""{"groups":[],"sets":null,"matrix":[],"exclusions":[]}""", "sets")]
    [Arguments("""{"groups":[],"sets":[],"matrix":null,"exclusions":[]}""", "matrix")]
    [Arguments("""{"groups":[],"sets":[],"matrix":[],"exclusions":null}""", "exclusions")]
    [Arguments("""{"groups":[],"sets":[{"id":"00000000-0000-0000-0000-000000000001","groupId":"00000000-0000-0000-0000-000000000002","name":"s","anyOf":null}],"matrix":[],"exclusions":[]}""", "anyOf")]
    [Arguments("""{"groups":[],"sets":[{"id":"00000000-0000-0000-0000-000000000001","groupId":"00000000-0000-0000-0000-000000000002","name":"s","anyOf":[{"all":null}]}],"matrix":[],"exclusions":[]}""", "all")]
    [Arguments("""{"groups":[],"sets":[{"id":"00000000-0000-0000-0000-000000000001","groupId":"00000000-0000-0000-0000-000000000002","name":"s","anyOf":[{"all":[{"property":null,"operator":"isEmpty"}]}]}],"matrix":[],"exclusions":[]}""", "property")]
    [Arguments("""{"sets":null,"matrix":[],"exclusions":[]}""", "sets")]
    [Arguments("""{"sets":[{"id":"00000000-0000-0000-0000-000000000001","discipline":"AR","name":"s","anyOf":null}],"matrix":[],"exclusions":[]}""", "anyOf")]
    public void NullLists_JsonException(string json, string list) =>
        Should.Throw<System.Text.Json.JsonException>(() => ConfigurationJson.Deserialize(json)).Message.ShouldContain($"«{list}»");
}
