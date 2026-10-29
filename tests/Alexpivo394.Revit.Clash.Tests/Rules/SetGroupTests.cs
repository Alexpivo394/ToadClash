using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class SetGroupTests
{
    private static Condition Cat(string category) => new(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, category);

    private static Condition FamilyContains(string text) => new(new PropertyRef(PropertyKind.Family), ConditionOperator.Contains, text);

    private static ElementInfo Info(string uid, string category, string? family = null) => new(uid, 1, category, family, null, null, null, []);

    private static ElementsInput Model(string discipline, string key, params ElementInfo[] elements) =>
        new(discipline, key, elements, new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>()));

    private static readonly ElementsInput Ar = Model("АР", "ar",
        Info("wall", "OST_Walls"), Info("ramp", "OST_GenericModel", "Проезд_паркинг"), Info("box", "OST_GenericModel", "Ящик"));

    private static readonly ElementsInput Ov = Model("ОВ", "ov", Info("duct", "OST_DuctCurves"), Info("ramp-ov", "OST_GenericModel", "Проезд_паркинг"));

    private static readonly Guid ArGroup = ConfigurationDefaults.DisciplineGroupId("АР");
    private static readonly Guid OvGroup = ConfigurationDefaults.DisciplineGroupId("ОВ");
    private static readonly SetGroup Ramps = new(Guid.NewGuid(), "Проезды", new GroupSource(["АР"], []));

    private const string LegacyJson = """
        {
          "sets": [
            { "id": "6a1c1b8e-0000-4000-8000-000000000001", "discipline": "АР", "name": "Стены",
              "anyOf": [ { "all": [ { "property": { "kind": "category" }, "operator": "equals", "value": "OST_Walls" } ] } ] },
            { "id": "6a1c1b8e-0000-4000-8000-000000000002", "discipline": "ов", "name": "Воздуховоды",
              "anyOf": [ { "all": [ { "property": { "kind": "category" }, "operator": "equals", "value": "OST_DuctCurves" } ] } ] }
          ],
          "matrix": [ { "disciplineA": "АР", "disciplineB": "ОВ", "enabled": true, "toleranceMm": 25 } ],
          "exclusions": [],
          "ignoreConnectedMep": false
        }
        """;

    [Test]
    public void Json_LegacyDisciplines_GroupsWithStableIds()
    {
        var first = ConfigurationJson.Deserialize(LegacyJson);
        var second = ConfigurationJson.Deserialize(LegacyJson);

        first.Groups.Select(g => (g.Id, g.Name)).ShouldBe([(ArGroup, "АР"), (OvGroup, "ов")]);
        second.Groups.Select(g => g.Id).ShouldBe(first.Groups.Select(g => g.Id));
        first.Groups[0].Source.Disciplines.ShouldBe(["АР"]);
        first.Sets.Select(s => s.GroupId).ShouldBe([ArGroup, OvGroup]);
        first.Matrix.ShouldHaveSingleItem().ShouldBe(new MatrixCell(ArGroup, OvGroup, true, 25));
        first.IgnoreConnectedMep.ShouldBeFalse();
        ConfigurationDefaults.DisciplineGroupId("ар").ShouldBe(ArGroup);
        ConfigurationDefaults.DisciplineGroupId("ОВ").ShouldNotBe(ArGroup);
    }

    [Test]
    public void Json_RoundTrip_Groups()
    {
        var set = new SelectionSet(Guid.NewGuid(), Ramps.Id, "Проезды", [new ConditionGroup([FamilyContains("Проезд")])]);
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР"), Ramps, new SetGroup(Guid.NewGuid(), "Модель", new GroupSource([], ["ar"]))],
            [set], [new MatrixCell(Ramps.Id, OvGroup, true, 50)], []);

        var back = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(config));

        back.Groups.Select(g => (g.Id, g.Name)).ShouldBe(config.Groups.Select(g => (g.Id, g.Name)));
        back.Groups[2].Source.ModelKeys.ShouldBe(["ar"]);
        back.Sets.ShouldHaveSingleItem().GroupId.ShouldBe(Ramps.Id);
        back.Matrix.ShouldBe(config.Matrix);
        ConfigurationJson.Serialize(back).ShouldNotContain("discipline\"");
    }

    [Test]
    public void Membership_CustomGroupFromDisciplineModels()
    {
        var set = new SelectionSet(Guid.NewGuid(), Ramps.Id, "Проезды", [new ConditionGroup([FamilyContains("Проезд")])]);
        var config = new ClashConfiguration([Ramps], [set], [], []);

        var membership = SetMembership.Evaluate(config, [Ar, Ov]);

        membership.SetsOf("ar", "ramp").ShouldBe([set.Id]);
        membership.SetsOf("ar", "box").ShouldBeEmpty();
        membership.SetsOf("ov", "ramp-ov").ShouldBeEmpty();
    }

    [Test]
    public void Membership_GroupByModelKey()
    {
        var group = new SetGroup(Guid.NewGuid(), "Только ОВ", new GroupSource([], ["ov"]));
        var set = new SelectionSet(Guid.NewGuid(), group.Id, "Всё", [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null)])]);

        var membership = SetMembership.Evaluate(new ClashConfiguration([group], [set], [], []), [Ar, Ov]);

        membership.SetsOf("ov", "duct").ShouldBe([set.Id]);
        membership.SetsOf("ar", "wall").ShouldBeEmpty();
    }

    [Test]
    public void Membership_ElementInTwoGroups_GroupsOfReturnsBoth()
    {
        var all = new SelectionSet(Guid.NewGuid(), ArGroup, "Всё", [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null)])]);
        var ramps = new SelectionSet(Guid.NewGuid(), Ramps.Id, "Проезды", [new ConditionGroup([FamilyContains("Проезд")])]);
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР"), Ramps], [all, ramps], [], []);

        var membership = SetMembership.Evaluate(config, [Ar]);

        membership.SetsOf("ar", "ramp").ShouldBe([all.Id, ramps.Id]);
        membership.GroupsOf("ar", "ramp").ShouldBe([ArGroup, Ramps.Id]);
        membership.GroupsOf("ar", "wall").ShouldBe([ArGroup]);
        membership.GroupsOf("ar", "nothing").ShouldBeEmpty();
    }

    [Test]
    public void Preview_CountsPerAnyOfGroup()
    {
        var set = new SelectionSet(Guid.NewGuid(), ArGroup, "Стены или проезды",
            [new ConditionGroup([Cat("OST_Walls")]), new ConditionGroup([FamilyContains("Проезд")]), new ConditionGroup([Cat("OST_Walls"), FamilyContains("x")])]);
        var config = new ClashConfiguration([ConfigurationDefaults.DisciplineGroup("АР")], [set], [], []);

        var preview = SetPreview.Count(config, set.Id, [Ar, Ov]);

        preview.Matched.ShouldBe(2);
        preview.Total.ShouldBe(3);
        preview.ByAnyOf.ShouldBe([1, 1, 0]);
        preview.Sample.Select(e => e.UniqueId).ShouldBe(["wall", "ramp"]);
    }

    [Test]
    public void Defaults_ByDisciplines_UpperTriangleAt20()
    {
        var config = ConfigurationDefaults.ByDisciplines(["АР", "ОВ", "ВК"]);

        config.Groups.Select(g => g.Name).ShouldBe(["АР", "ОВ", "ВК"]);
        config.Sets.Select(s => (s.GroupId, s.Name)).ShouldBe(config.Groups.Select(g => (g.Id, "Всё")));
        config.Matrix.Count.ShouldBe(6);
        config.Matrix.ShouldAllBe(c => c.Enabled && c.ToleranceMm == 20);
        config.CellFor(config.Groups[2].Id, config.Groups[0].Id).ShouldNotBeNull();
        config.IgnoreConnectedMep.ShouldBeTrue();
        ConfigurationValidator.Problems(config, ["АР", "ОВ", "ВК"], []).ShouldBeEmpty();
    }

    [Test]
    public void Problems_GroupSourceModelNotInPart()
    {
        var group = new SetGroup(Guid.NewGuid(), "Проезды", new GroupSource([], ["gone"]));
        var set = new SelectionSet(Guid.NewGuid(), group.Id, "Проезды", [new ConditionGroup([FamilyContains("Проезд")])]);

        var problems = ConfigurationValidator.Problems(new ClashConfiguration([group], [set], [], []), ["АР"], ["ar"]);

        problems.ShouldHaveSingleItem().ShouldContain("«Проезды»");
        problems[0].ShouldContain("модель");
    }

    [Test]
    public void Problems_SetOfUnknownGroup()
    {
        var set = new SelectionSet(Guid.NewGuid(), Guid.NewGuid(), "Стены", [new ConditionGroup([Cat("OST_Walls")])]);

        ConfigurationValidator.Problems(new ClashConfiguration([], [set], [], []), ["АР"], [])
            .ShouldHaveSingleItem().ShouldContain("группа удалена");
    }

    [Test]
    public void Problems_DuplicateGroupName()
    {
        var a = new SetGroup(Guid.NewGuid(), "Проезды", new GroupSource(["АР"], []));
        var b = new SetGroup(Guid.NewGuid(), "проезды ", new GroupSource(["АР"], []));
        var empty = new SetGroup(Guid.NewGuid(), "Пустая", new GroupSource([], []));
        var unknown = new SetGroup(Guid.NewGuid(), "ЭОМ", new GroupSource(["ЭОМ"], []));

        var problems = ConfigurationValidator.Problems(new ClashConfiguration([a, b, empty, unknown], [], [], []), ["АР"], []);

        problems.Count.ShouldBe(3);
        problems.ShouldContain(p => p.Contains("Две группы"));
        problems.ShouldContain(p => p.Contains("«Пустая»") && p.Contains("источник"));
        problems.ShouldContain(p => p.Contains("«ЭОМ»") && p.Contains("раздел"));
    }
}
