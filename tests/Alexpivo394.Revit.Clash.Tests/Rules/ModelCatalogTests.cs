using System.Numerics;
using System.Text.Json;
using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Tests.Rules;

public sealed class ModelCatalogTests
{
    private static readonly Guid AdskMark = Guid.Parse("2204049c-d557-4dfc-8d70-13f19715e46d");

    private static ModelIndexEntry Entry(string uid, string category, string? family = null, string? type = null, string? level = null,
        string? workset = null) =>
        new(new ElementInfo(uid, 1, category, family, type, level, workset, []), new Aabb(Vector3.Zero, Vector3.One), 0, 1);

    private static ModelIndex Index(params ModelIndexEntry[] entries) =>
        new(new ModelHeader("k", "p", 2022, DateTimeOffset.UnixEpoch, true, "v", 0, 0, 0), entries, entries.Select(e => e.Info.UniqueId).ToArray());

    private static ModelParameters Parameters(params (string Uid, ParameterValue[] Values)[] rows) =>
        new(rows.ToDictionary(r => r.Uid, r => (IReadOnlyList<ParameterValue>)r.Values));

    [Test]
    public void Build_CountsCategoriesAndParameters()
    {
        var index = Index(Entry("w1", "OST_Walls"), Entry("w2", "OST_Walls"), Entry("p1", "OST_PipeCurves"));
        var parameters = Parameters(
            ("w1", [new ParameterValue("Марка", null, "С1", null)]),
            ("w2", [new ParameterValue("Марка", null, "С2", null)]),
            ("p1", [new ParameterValue("Диаметр", null, "50 мм", 50), new ParameterValue("ADSK_Марка", AdskMark, "Т1", null)]));

        var catalog = ModelCatalog.Build(index, parameters);

        catalog.Categories.ShouldBe([new CatalogCategory("OST_PipeCurves", 1), new CatalogCategory("OST_Walls", 2)]);
        var diameter = catalog.Parameters.Single(p => p.Name == "Диаметр");
        diameter.IsNumeric.ShouldBeTrue();
        diameter.Count.ShouldBe(1);
        diameter.Categories.ShouldBe(["OST_PipeCurves"]);
        var mark = catalog.Parameters.Single(p => p.Name == "Марка");
        mark.IsNumeric.ShouldBeFalse();
        mark.Count.ShouldBe(2);
        mark.SharedGuid.ShouldBeNull();
        catalog.Parameters.Single(p => p.Name == "ADSK_Марка").SharedGuid.ShouldBe(AdskMark);
    }

    [Test]
    public void Build_SharedGuidWinsOverName()
    {
        var index = Index(Entry("a", "OST_Walls"), Entry("b", "OST_Doors"));
        var parameters = Parameters(
            ("a", [new ParameterValue("ADSK_Марка", AdskMark, "1", null)]),
            ("b", [new ParameterValue("adsk_марка", AdskMark, "2", null)]));

        var catalog = ModelCatalog.Build(index, parameters);

        var single = catalog.Parameters.ShouldHaveSingleItem();
        single.Name.ShouldBe("ADSK_Марка");
        single.Count.ShouldBe(2);
        single.Categories.ShouldBe(["OST_Doors", "OST_Walls"]);
    }

    [Test]
    public void Build_SameNameWithoutGuid_IgnoresCase()
    {
        var index = Index(Entry("a", "OST_Walls"), Entry("b", "OST_Walls"));
        var parameters = Parameters(
            ("a", [new ParameterValue("Комментарии", null, "x", null)]),
            ("b", [new ParameterValue("комментарии", null, "y", null)]));

        ModelCatalog.Build(index, parameters).Parameters.ShouldHaveSingleItem().Count.ShouldBe(2);
    }

    [Test]
    public void Build_IgnoresParametersOfElementsNotInIndex()
    {
        var index = Index(Entry("a", "OST_Walls"));
        var parameters = Parameters(
            ("a", [new ParameterValue("Марка", null, "1", null)]),
            ("hidden", [new ParameterValue("Скрытый", null, "1", null)]));

        ModelCatalog.Build(index, parameters).Parameters.Select(p => p.Name).ShouldBe(["Марка"]);
    }

    [Test]
    public void Build_ListsDistinctFamiliesTypesLevelsWorksets()
    {
        var index = Index(
            Entry("a", "OST_Walls", "Стена", "200", "Этаж 1", "АР"),
            Entry("b", "OST_Walls", "стена", "300", "этаж 1", null),
            Entry("c", "OST_Walls", null, "", "Этаж 2", "АР"));

        var catalog = ModelCatalog.Build(index, Parameters());

        catalog.Families.ShouldBe(["Стена"]);
        catalog.Types.ShouldBe(["200", "300"]);
        catalog.Levels.ShouldBe(["Этаж 1", "Этаж 2"]);
        catalog.Worksets.ShouldBe(["АР"]);
    }

    [Test]
    public void Merge_SumsCountsAndUnionsLists()
    {
        var first = ModelCatalog.Build(Index(Entry("a", "OST_Walls", level: "Этаж 1")), Parameters(("a", [new ParameterValue("Марка", null, "1", null)])));
        var second = ModelCatalog.Build(Index(Entry("b", "OST_Walls", level: "ЭТАЖ 1"), Entry("c", "OST_Floors", level: "Этаж 2")),
            Parameters(("b", [new ParameterValue("марка", null, "2", 2)])));

        var merged = ModelCatalog.Merge([first, second]);

        merged.Categories.ShouldBe([new CatalogCategory("OST_Floors", 1), new CatalogCategory("OST_Walls", 2)]);
        merged.Levels.ShouldBe(["Этаж 1", "Этаж 2"]);
        var mark = merged.Parameters.ShouldHaveSingleItem();
        mark.Count.ShouldBe(2);
        mark.IsNumeric.ShouldBeTrue();
    }

    [Test]
    public void Catalog_RoundTripsThroughSystemTextJson()
    {
        var catalog = ModelCatalog.Build(Index(Entry("a", "OST_Walls", "Стена", "200", "Этаж 1", "АР")),
            Parameters(("a", [new ParameterValue("ADSK_Марка", AdskMark, "1", null)])));

        var restored = JsonSerializer.Deserialize<ModelCatalog>(JsonSerializer.Serialize(catalog))!;

        restored.Categories.ShouldBe(catalog.Categories);
        restored.Families.ShouldBe(catalog.Families);
        var parameter = restored.Parameters.ShouldHaveSingleItem();
        parameter.SharedGuid.ShouldBe(AdskMark);
        parameter.Categories.ShouldBe(["OST_Walls"]);
    }
}
