using Shouldly;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class ConfigurationValidatorTests
{
    private static readonly string[] Disciplines = ["АР", "ОВ"];

    private static Condition Cond(PropertyKind kind, ConditionOperator op, string? value, string? parameter = null, Guid? guid = null) =>
        new(new PropertyRef(kind, parameter, guid), op, value);

    private static DisciplineSet Set(string discipline, string name, params ConditionGroup[] groups) =>
        new(Guid.NewGuid(), discipline, name, groups.Length > 0 ? groups : [new ConditionGroup([Cond(PropertyKind.Category, ConditionOperator.IsNotEmpty, null)])]);

    private static IReadOnlyList<string> Problems(IReadOnlyList<DisciplineSet> sets, IReadOnlyList<DisciplineCell>? matrix = null,
        IReadOnlyList<SetPairExclusion>? exclusions = null) =>
        ConfigurationValidator.Problems(ConfigurationDefaults.FromDisciplines(sets, matrix ?? [], exclusions ?? []), Disciplines, []);

    [Test]
    public void Problems_ValidConfiguration_Empty()
    {
        var walls = Set("АР", "Стены");
        var ducts = Set("ОВ", "Воздуховоды",
            new ConditionGroup([Cond(PropertyKind.Parameter, ConditionOperator.Greater, "40,5", "Диаметр")]),
            new ConditionGroup([Cond(PropertyKind.Category, ConditionOperator.Equals, "OST_DuctCurves")]));

        Problems([walls, ducts], [new DisciplineCell("АР", "ОВ", true), new DisciplineCell("ОВ", "ОВ", false, 0.05)], [new SetPairExclusion(walls.Id, ducts.Id)])
            .ShouldBeEmpty();
    }

    [Test]
    public void Problems_EmptySetName() => Problems([Set("АР", "  ")]).ShouldHaveSingleItem().ShouldContain("без имени");

    [Test]
    public void Problems_DuplicateSetName() => Problems([Set("АР", "Стены"), Set("ар", "СТЕНЫ")]).ShouldHaveSingleItem().ShouldContain("«СТЕНЫ»");

    [Test]
    public void Problems_SameNameInOtherDiscipline_Allowed() => Problems([Set("АР", "Все"), Set("ОВ", "Все")]).ShouldBeEmpty();

    [Test]
    public void Problems_UnknownDisciplineOfSet() => Problems([Set("ВК", "Трубы")]).ShouldHaveSingleItem().ShouldContain("раздел «ВК»");

    [Test]
    public void Problems_SetWithoutConditions()
    {
        Problems([new DisciplineSet(Guid.NewGuid(), "АР", "Пусто", [])]).ShouldHaveSingleItem().ShouldContain("нет условий");
        Problems([Set("АР", "Пустая группа", new ConditionGroup([]))]).ShouldHaveSingleItem().ShouldContain("нет условий");
    }

    [Test]
    public void Problems_NonNumericGreater() =>
        Problems([Set("ОВ", "Толстые", new ConditionGroup([Cond(PropertyKind.Parameter, ConditionOperator.Less, "сорок", "Диаметр")]))])
            .ShouldHaveSingleItem().ShouldContain("число");

    [Test]
    public void Problems_EqualsWithoutValue() =>
        Problems([Set("АР", "Стены", new ConditionGroup([Cond(PropertyKind.Family, ConditionOperator.Equals, " ")]))])
            .ShouldHaveSingleItem().ShouldContain("значение");

    [Test]
    public void Problems_ParameterWithoutNameOrGuid() =>
        Problems([Set("АР", "Стены", new ConditionGroup([Cond(PropertyKind.Parameter, ConditionOperator.IsNotEmpty, null)]))])
            .ShouldHaveSingleItem().ShouldContain("параметр");

    [Test]
    public void Problems_UnknownDisciplineInMatrix() =>
        Problems([Set("АР", "Стены")], [new DisciplineCell("АР", "ЭОМ", true)]).ShouldHaveSingleItem().ShouldContain("«ЭОМ»");

    [Test]
    public void Problems_ToleranceBelowMinimum()
    {
        Problems([Set("АР", "Стены")], [new DisciplineCell("АР", "ОВ", true, 0.01)]).ShouldHaveSingleItem().ShouldContain("0,05");
        Problems([Set("АР", "Стены")], [new DisciplineCell("АР", "ОВ", true, 5000)]).ShouldHaveSingleItem().ShouldContain("1000");
    }

    [Test]
    public void Problems_DuplicateCell() =>
        Problems([Set("АР", "Стены")], [new DisciplineCell("АР", "ОВ", true), new DisciplineCell("ов", "ар", false)]).ShouldHaveSingleItem().ShouldContain("дважды");

    [Test]
    public void Problems_ExclusionOfMissingSet()
    {
        var walls = Set("АР", "Стены");

        Problems([walls], exclusions: [new SetPairExclusion(walls.Id, Guid.NewGuid())]).ShouldHaveSingleItem().ShouldContain("исключени");
    }

    [Test]
    public void DuplicateGroupIds_Problem()
    {
        var id = Guid.NewGuid();
        var config = new ClashConfiguration([new SetGroup(id, "АР", GroupSource.OfDiscipline("АР")), new SetGroup(id, "ОВ", GroupSource.OfDiscipline("ОВ"))], [], [], []);

        ConfigurationValidator.Problems(config, Disciplines, []).ShouldContain("Две группы с одинаковым Id — пересоздайте одну из них");
    }

    [Test]
    public void DuplicateSetIds_Problem()
    {
        var group = Guid.NewGuid();
        var id = Guid.NewGuid();
        ConditionGroup[] all = [new ConditionGroup([Cond(PropertyKind.Category, ConditionOperator.IsNotEmpty, null)])];
        var config = new ClashConfiguration([new SetGroup(group, "АР", GroupSource.OfDiscipline("АР"))],
            [new SelectionSet(id, group, "Стены", all), new SelectionSet(id, group, "Двери", all)], [], []);

        ConfigurationValidator.Problems(config, Disciplines, []).ShouldContain("Два набора с одинаковым Id: «Стены» и «Двери»");
    }
}
