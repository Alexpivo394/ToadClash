using Alexpivo394.Revit.Clash.Rules;
using Shouldly;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class ConfigurationMergeTests
{
    private static readonly SetGroup Ar = ConfigurationDefaults.DisciplineGroup("АР");
    private static readonly SetGroup Ov = ConfigurationDefaults.DisciplineGroup("ОВ");

    private static SelectionSet Set(string name, string category, SetGroup? group = null, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), (group ?? Ar).Id, name,
            [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.Equals, category)])]);

    private static readonly SelectionSet Walls = Set("Стены", "OST_Walls");
    private static readonly SelectionSet Doors = Set("Двери", "OST_Doors");
    private static readonly SelectionSet Ducts = Set("Воздуховоды", "OST_DuctCurves", Ov);

    private static ClashConfiguration Base() =>
        new([Ar, Ov], [Walls, Doors, Ducts], [new MatrixCell(Ar.Id, Ov.Id, true)], [new SetPairExclusion(Doors.Id, Ducts.Id)]);

    private static ClashConfiguration WithSet(ClashConfiguration c, SelectionSet set) =>
        c with { Sets = c.Sets.Select(s => s.Id == set.Id ? set : s).ToList() };

    [Test]
    public void Merge_DifferentSets_BothKept()
    {
        var theirs = WithSet(Base(), Walls with { Name = "Стены наружные" });
        var mine = WithSet(Base(), Doors with { Name = "Двери и люки" });

        var result = ConfigurationMerge.Merge(Base(), theirs, mine);

        result.Conflicts.ShouldBeEmpty();
        result.Merged.Sets.Select(s => s.Name).ShouldBe(["Стены наружные", "Двери и люки", "Воздуховоды"]);
    }

    [Test]
    public void Merge_SameSet_Conflict()
    {
        var theirs = WithSet(Base(), Walls with { Name = "Стены А" });
        var mine = WithSet(Base(), Walls with { Name = "Стены Б" });

        var result = ConfigurationMerge.Merge(Base(), theirs, mine);

        var conflict = result.Conflicts.ShouldHaveSingleItem();
        conflict.Key.ShouldBe($"set:{Walls.Id:N}");
        conflict.Kind.ShouldBe("set");
        conflict.Label.ShouldContain("«Стены А»");
        result.Merged.Sets[0].Name.ShouldBe("Стены А");
    }

    [Test]
    public void Merge_SameChange_NoConflict()
    {
        var changed = WithSet(Base(), Walls with { AnyOf = [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.OneOf, null, ["OST_Walls", "OST_CurtainWallPanels"])])] });
        var same = WithSet(Base(), Walls with { AnyOf = [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.OneOf, null, ["OST_Walls", "OST_CurtainWallPanels"])])] });

        var result = ConfigurationMerge.Merge(Base(), changed, same);

        result.Conflicts.ShouldBeEmpty();
        result.Merged.Sets[0].AnyOf[0].All[0].Values.ShouldBe(["OST_Walls", "OST_CurtainWallPanels"]);
    }

    [Test]
    public void Merge_KeepMine_TakesMine()
    {
        var theirs = WithSet(Base(), Walls with { Name = "Стены А" });
        var mine = WithSet(Base(), Walls with { Name = "Стены Б" });

        var result = ConfigurationMerge.Merge(Base(), theirs, mine, [$"set:{Walls.Id:N}"]);

        result.Conflicts.ShouldBeEmpty();
        result.Merged.Sets[0].Name.ShouldBe("Стены Б");
    }

    [Test]
    public void Merge_DeletedInMineChangedInTheirs_Conflict()
    {
        var theirs = WithSet(Base(), Doors with { Name = "Двери все" });
        var mine = Base() with { Sets = [Walls, Ducts], Exclusions = [] };

        var result = ConfigurationMerge.Merge(Base(), theirs, mine);

        result.Conflicts.ShouldHaveSingleItem().Key.ShouldBe($"set:{Doors.Id:N}");
        result.Merged.Sets.Select(s => s.Id).ShouldContain(Doors.Id);
    }

    [Test]
    public void Merge_DeletedSetDropsItsExclusions()
    {
        var mine = Base() with { Sets = [Walls, Ducts] };

        var result = ConfigurationMerge.Merge(Base(), Base(), mine);

        result.Conflicts.ShouldBeEmpty();
        result.Merged.Sets.Select(s => s.Name).ShouldBe(["Стены", "Воздуховоды"]);
        result.Merged.Exclusions.ShouldBeEmpty();
    }

    [Test]
    public void Merge_CellsAndFlag()
    {
        var theirs = Base() with { Matrix = [new MatrixCell(Ar.Id, Ov.Id, true, 50)] };
        var mine = Base() with { Matrix = [new MatrixCell(Ov.Id, Ar.Id, true), new MatrixCell(Ov.Id, Ov.Id, true)], IgnoreConnectedMep = false };

        var result = ConfigurationMerge.Merge(Base(), theirs, mine);

        result.Conflicts.ShouldBeEmpty();
        result.Merged.IgnoreConnectedMep.ShouldBeFalse();
        result.Merged.CellFor(Ar.Id, Ov.Id)!.ToleranceMm.ShouldBe(50);
        result.Merged.CellFor(Ov.Id, Ov.Id).ShouldNotBeNull();

        var clash = ConfigurationMerge.Merge(Base(), theirs, Base() with { Matrix = [new MatrixCell(Ar.Id, Ov.Id, false)] });
        clash.Conflicts.ShouldHaveSingleItem().Kind.ShouldBe("cell");
        clash.Conflicts[0].Label.ShouldContain("«АР» × «ОВ»");
    }

    [Test]
    public void Merge_NewSetsAppendedInOrder()
    {
        var a = Set("Новый A", "OST_Floors");
        var b = Set("Новый B", "OST_Roofs");
        var c = Set("Их", "OST_Columns");
        var ramps = new SetGroup(Guid.NewGuid(), "Проезды", new GroupSource(["АР"], []));
        var theirs = Base() with { Sets = [.. Base().Sets, c] };
        var mine = Base() with { Groups = [Ar, Ov, ramps], Sets = [b, .. Base().Sets, a] };

        var result = ConfigurationMerge.Merge(Base(), theirs, mine);

        result.Conflicts.ShouldBeEmpty();
        result.Merged.Sets.Select(s => s.Name).ShouldBe(["Стены", "Двери", "Воздуховоды", "Их", "Новый B", "Новый A"]);
        result.Merged.Groups.Select(g => g.Name).ShouldBe(["АР", "ОВ", "Проезды"]);
    }
}
