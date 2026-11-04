using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class SetMembershipTests
{
    private static ElementGeometry Element(string uid, string category) =>
        new(new ElementInfo(uid, 1, category, null, null, null, null, []),
            new ElementMesh([], []), new Aabb(), 0);

    private static ModelInput Model(string key, string discipline, params ElementGeometry[] elements)
    {
        var header = new ModelHeader(key, "", 2024, DateTimeOffset.UnixEpoch, true, "", 0, 0, 0);
        return new ModelInput(discipline, new ModelGeometry(header, elements, []),
            new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>()));
    }

    private static Condition Cat(string category) =>
        new(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, category);

    private static ClashConfiguration Config(params DisciplineSet[] sets) => ConfigurationDefaults.FromDisciplines(sets, [], []);

    [Test]
    public void ModelCondition_SelectsElementsOfOneFile()
    {
        var model = new Condition(new PropertyRef(PropertyKind.Model), ConditionOperator.Equals, "m2");
        var set = new DisciplineSet(Guid.NewGuid(), "AR", "Second file walls", [new ConditionGroup([Cat("OST_Walls"), model])]);
        var others = new DisciplineSet(Guid.NewGuid(), "AR", "Not second file", [
            new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Model), ConditionOperator.NotEquals, "M2")])]);

        var membership = SetMembership.Evaluate(Config(set, others),
            [Model("m1", "AR", Element("a", "OST_Walls")), Model("m2", "AR", Element("b", "OST_Walls"))]);

        membership.SetsOf("m1", "a").ShouldBe([others.Id]);
        membership.SetsOf("m2", "b").ShouldBe([set.Id]); // ключ модели без учёта регистра, как любые строки условий
    }

    [Test]
    public void ModelCondition_CountsAndValues()
    {
        var model = new Condition(new PropertyRef(PropertyKind.Model), ConditionOperator.OneOf, null, ["m2"]);
        var set = new DisciplineSet(Guid.NewGuid(), "AR", "S", [new ConditionGroup([model])]);
        var config = Config(set);
        var inputs = new[] { Model("m1", "AR", Element("a", "OST_Walls")), Model("m2", "AR", Element("b", "OST_Walls"), Element("c", "OST_Doors")) }
            .Select(m => m.ToElements()).ToArray();

        SetPreview.Count(config, set.Id, inputs).Matched.ShouldBe(2);
        PropertyValues.Count(config, config.Sets[0].GroupId, new PropertyRef(PropertyKind.Model), inputs).ShouldBe([new ValueCount("m2", 2), new ValueCount("m1", 1)]);
        new Condition(new PropertyRef(PropertyKind.Model), ConditionOperator.Equals, "m2")
            .Matches(new ElementInfo("x", 1, "OST_Walls", null, null, null, null, []), []).ShouldBeFalse(); // без ключа модели — свойства нет
    }

    [Test]
    public void Set_AnyOfGroups_Or()
    {
        var set = new DisciplineSet(Guid.NewGuid(), "OV", "Pipes or ducts", [
            new ConditionGroup([Cat("OST_PipeCurves")]),
            new ConditionGroup([Cat("OST_DuctCurves")])]);
        var model = Model("m1", "OV", Element("a", "OST_PipeCurves"), Element("b", "OST_DuctCurves"), Element("c", "OST_Walls"));

        var membership = SetMembership.Evaluate(Config(set), [model]);

        membership.SetsOf("m1", "a").ShouldBe([set.Id]);
        membership.SetsOf("m1", "b").ShouldBe([set.Id]);
        membership.SetsOf("m1", "c").ShouldBeEmpty();
    }

    [Test]
    public void Group_AllConditions_And()
    {
        var set = new DisciplineSet(Guid.NewGuid(), "OV", "S", [
            new ConditionGroup([Cat("OST_PipeCurves"),
                new Condition(new PropertyRef(PropertyKind.Level), ConditionOperator.Equals, "L1")])]);
        var model = Model("m1", "OV", Element("a", "OST_PipeCurves"));

        SetMembership.Evaluate(Config(set), [model]).SetsOf("m1", "a").ShouldBeEmpty();
    }

    [Test]
    public void Element_InNoSet_NotInMembership()
    {
        var model = Model("m1", "OV", Element("a", "OST_Walls"));

        var membership = SetMembership.Evaluate(Config(), [model]);

        membership.SetsOf("m1", "a").ShouldBeEmpty();
        membership.SetsOf("m1", "unknown").ShouldBeEmpty();
        membership.SetsOf("nomodel", "a").ShouldBeEmpty();
    }

    [Test]
    public void Membership_OnlySetsOfOwnDiscipline()
    {
        var ar = new DisciplineSet(Guid.NewGuid(), "AR", "Walls", [new ConditionGroup([Cat("OST_Walls")])]);
        var ov = new DisciplineSet(Guid.NewGuid(), "ov", "Walls too", [new ConditionGroup([Cat("OST_Walls")])]);
        var model = Model("m1", "OV", Element("a", "OST_Walls"));

        SetMembership.Evaluate(Config(ar, ov), [model]).SetsOf("m1", "a").ShouldBe([ov.Id]);
    }

    [Test]
    public void CellFor_IsSymmetric()
    {
        var cell = new DisciplineCell("AR", "OV", true, 15);
        var config = ConfigurationDefaults.FromDisciplines([], [cell], []);

        config.CellFor(ConfigurationDefaults.DisciplineGroupId("OV"), ConfigurationDefaults.DisciplineGroupId("AR")).ShouldBe(config.Matrix[0]);
        config.CellFor(ConfigurationDefaults.DisciplineGroupId("ar"), ConfigurationDefaults.DisciplineGroupId("ov")).ShouldBe(config.Matrix[0]);
        config.CellFor(ConfigurationDefaults.DisciplineGroupId("AR"), ConfigurationDefaults.DisciplineGroupId("EOM")).ShouldBeNull();
    }
}
