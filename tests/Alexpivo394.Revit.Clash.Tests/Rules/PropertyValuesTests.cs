using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class PropertyValuesTests
{
    private static ElementInfo Info(string uid, string category, string? family = null) => new(uid, 1, category, family, null, null, null, []);

    private static readonly Guid ArGroup = ConfigurationDefaults.DisciplineGroupId("АР");

    private static ElementsInput Ar() => new("АР", "ar",
        [Info("w1", "OST_Walls"), Info("w2", "OST_Walls"), Info("d1", "OST_Doors"), Info("g1", "OST_GenericModel", "Проезд")],
        new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>
        {
            ["w1"] = [new ParameterValue("Марка", null, "С1", null)],
            ["w2"] = [new ParameterValue("марка", null, "С2", null)],
            ["d1"] = [new ParameterValue("Марка", null, "С1", null)]
        }));

    private static ElementsInput Ov() => new("ОВ", "ov", [Info("x", "OST_DuctCurves")], new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>()));

    private static Condition OneOf(params string[] values) => new(new PropertyRef(PropertyKind.Category), ConditionOperator.OneOf, null, values);

    [Test]
    public void OneOf_MatchesAnyValueIgnoringCase()
    {
        var condition = OneOf("ost_walls", "OST_Doors");
        var parameters = Array.Empty<ParameterValue>();

        condition.Matches(Info("a", "OST_Walls"), parameters).ShouldBeTrue();
        condition.Matches(Info("b", "OST_Doors"), parameters).ShouldBeTrue();
        condition.Matches(Info("c", "OST_Floors"), parameters).ShouldBeFalse();
    }

    [Test]
    public void OneOf_MissingProperty_False()
    {
        var condition = new Condition(new PropertyRef(PropertyKind.Family), ConditionOperator.OneOf, null, ["Проезд"]);

        condition.Matches(Info("a", "OST_Walls"), []).ShouldBeFalse();
    }

    [Test]
    public void OneOf_JsonRoundTrip()
    {
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР")],
            [new SelectionSet(Guid.NewGuid(), ArGroup, "Стены и двери", [new ConditionGroup([OneOf("OST_Walls", "OST_Doors")])])], [], []);

        var back = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(config));

        var condition = back.Sets[0].AnyOf[0].All[0];
        condition.Operator.ShouldBe(ConditionOperator.OneOf);
        condition.Values.ShouldBe(["OST_Walls", "OST_Doors"]);
    }

    [Test]
    public void Problems_OneOfWithoutValues()
    {
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР")],
            [new SelectionSet(Guid.NewGuid(), ArGroup, "Пусто", [new ConditionGroup([OneOf()])])], [], []);

        ConfigurationValidator.Problems(config, ["АР"], []).ShouldHaveSingleItem().ShouldContain("не выбрано ни одного значения");
    }

    [Test]
    public void Values_CategoriesOfGroupSourceByCount()
    {
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР")], [], [], []);

        var values = PropertyValues.Count(config, ArGroup, new PropertyRef(PropertyKind.Category), [Ar(), Ov()]);

        values.ShouldBe([new ValueCount("OST_Walls", 2), new ValueCount("OST_Doors", 1), new ValueCount("OST_GenericModel", 1)]);
    }

    [Test]
    public void Values_ParameterByName()
    {
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР")], [], [], []);

        var values = PropertyValues.Count(config, ArGroup, new PropertyRef(PropertyKind.Parameter, "МАРКА"), [Ar()], limit: 1);

        values.ShouldBe([new ValueCount("С1", 2)]);
    }

    [Test]
    public void Unassigned_ByCategory()
    {
        var walls = new SelectionSet(Guid.NewGuid(), ArGroup, "Стены",
            [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, "OST_Walls")])]);
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР")], [walls], [], []);

        var result = SetPreview.Unassigned(config, ArGroup, [Ar(), Ov()]);

        result.Count.ShouldBe(2);
        result.ByCategory.ShouldBe([new ValueCount("OST_Doors", 1), new ValueCount("OST_GenericModel", 1)]);
    }
}
