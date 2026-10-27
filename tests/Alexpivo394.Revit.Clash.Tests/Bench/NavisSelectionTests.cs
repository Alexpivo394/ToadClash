using System.Numerics;
using System.Xml.Linq;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Tests.Engine;
using Shouldly;
using ToadClash.Bench;

namespace Alexpivo394.Revit.Clash.Tests.Bench;

public sealed class NavisSelectionTests
{
    private static readonly ElementMesh Mesh = TestScenes.Box(Vector3.Zero, new Vector3(1000));

    [Test]
    public void Parse_FolderLocator_SelectsAllChildSets()
    {
        var sets = NavisSelection.Parse(Root(
            Folder("ПТ", Set("ПТ_Трубы", Condition("equals", 10, "LcRevitPropertyElementCategory", "Трубы")),
                Set("ПТ_Арматура", Condition("equals", 10, "LcRevitPropertyElementCategory", "Арматура")))));

        sets.Resolve("lcop_selection_set_tree/ПТ").Select(set => set.Path).ShouldBe(["ПТ/ПТ_Трубы", "ПТ/ПТ_Арматура"]);
        sets.Resolve("lcop_selection_set_tree/ПТ/ПТ_Трубы").Single().Path.ShouldBe("ПТ/ПТ_Трубы");
    }

    [Test]
    public void Matches_AndWithinGroup_OrBetweenGroups_NegationAndSourceFile()
    {
        var set = NavisSelection.Parse(Root(Set("s",
            Condition("contains", 10, "LcOaNodeSourceFile", "PT"),
            Condition("equals", 10, "LcRevitPropertyElementCategory", "Трубы"),
            Condition("contains", 58, "LcRevitPropertyElementFamily", "муфта"),
            Condition("contains", 90, "LcRevitPropertyElementFamily", "Хомут")))).Resolve("lcop_selection_set_tree/s").Single();

        set.Matches(Item("DON_PT_R22", "Трубы", family: "Труба")).ShouldBeTrue();
        set.Matches(Item("DON_PT_R22", "Трубы", family: "Муфта стальная")).ShouldBeFalse();
        set.Matches(Item("DON_VK_R22", "Трубы", family: "Труба")).ShouldBeFalse();
        // Вторая OR-группа не ограничена файлом источника.
        set.Matches(Item("DON_VK_R22", "Арматура", family: "хомут")).ShouldBeTrue();
    }

    [Test]
    public void Matches_LinearValueInFeet_ComparedWithMillimeters_MissingPropertyIsFalse()
    {
        var set = NavisSelection.Parse(Root(Set("s", Condition("greater_than", 10, "lcldrevit_parameter_-1140225", "0.131234", "Диаметр", "linear"))))
            .Resolve("lcop_selection_set_tree/s").Single();

        set.Matches(Item("m", "Трубы", diameter: 50)).ShouldBeTrue();
        set.Matches(Item("m", "Трубы", diameter: 40)).ShouldBeFalse();
        set.Matches(Item("m", "Трубы")).ShouldBeFalse();
    }

    [Test]
    public void Matches_NotEqualsOnMissingProperty_IsFalse_NegatedIsTrue()
    {
        var sets = NavisSelection.Parse(Root(
            Set("plain", Condition("not_equals", 10, "LcRevitPropertyElementFamily", "x")),
            Set("negated", Condition("equals", 42, "LcRevitPropertyElementFamily", "x"))));

        sets.Resolve("lcop_selection_set_tree/plain").Single().Matches(Item("m", "Трубы")).ShouldBeFalse();
        sets.Resolve("lcop_selection_set_tree/negated").Single().Matches(Item("m", "Трубы")).ShouldBeTrue();
    }

    private static NavisItem Item(string model, string category, string? family = null, double? diameter = null)
    {
        var info = new ElementInfo("u", 1, "OST_Any", family, "Тип", null, null, []);
        var parameters = new List<ParameterValue> { new("Категория", null, category, null) };
        if (diameter is { } value) parameters.Add(new("Диаметр", null, value.ToString(), value));
        return new NavisItem(model, info, parameters);
    }

    private static XElement Root(params XElement[] children) => new("selectionsets", children);

    private static XElement Folder(string name, params XElement[] children) => new("viewfolder", new XAttribute("name", name), children);

    private static XElement Set(string name, params XElement[] conditions) =>
        new("selectionset", new XAttribute("name", name), new XElement("findspec", new XElement("conditions", conditions)));

    private static XElement Condition(string test, int flags, string property, string value, string display = "", string type = "wstring") =>
        new("condition", new XAttribute("test", test), new XAttribute("flags", flags),
            new XElement("property", new XElement("name", new XAttribute("internal", property), display)),
            new XElement("value", new XElement("data", new XAttribute("type", type), value)));
}
