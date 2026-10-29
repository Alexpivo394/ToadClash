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
