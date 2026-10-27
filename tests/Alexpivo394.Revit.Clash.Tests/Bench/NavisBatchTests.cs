using System.Numerics;
using System.Xml.Linq;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;
using Alexpivo394.Revit.Clash.Tests.Engine;
using Shouldly;
using ToadClash.Bench;

namespace Alexpivo394.Revit.Clash.Tests.Bench;

public sealed class NavisBatchTests
{
    [Test]
    public void Compare_MatchesReportPair_AcrossModels()
    {
        var (models, selection) = Scene();
        var report = Report(Result(10, 20));

        var result = NavisBatch.Compare(Test("PT - KR", "ПТ", "КР"), selection, new NavisMembership(models), report);

        result.Common.Single().ShouldBe(new ComparisonPair(new("DON_KR", "wall"), new("DON_PT", "pipe")));
        result.OnlyNavis.ShouldBeEmpty();
        result.OnlyOurs.ShouldBeEmpty();
    }

    [Test]
    public void Compare_SameSetsOnBothSides_DoesNotClashElementWithItself()
    {
        var (models, selection) = Scene();

        var result = NavisBatch.Compare(Test("PT - PT", "ПТ", "ПТ"), selection, new NavisMembership(models), null);

        result.OnlyOurs.ShouldBeEmpty();
    }

    [Test]
    public void Compare_IgnoreRule_RemovesPairOfRuleSets()
    {
        var (models, selection) = Scene();
        var test = Test("PT - KR", "ПТ", "КР");
        test.Element("rules")!.Add(new XElement("rule", new XAttribute("enabled", "1"),
            new XAttribute("creator", "LcOdpClashIgnoreSpecSelectionSetsPlugin"),
            new XElement("ruleparams", RuleParam("КР\nКР_Стены"), RuleParam("ПТ\nПТ_Трубы"))));

        var result = NavisBatch.Compare(test, selection, new NavisMembership(models), null);

        result.OnlyOurs.ShouldBeEmpty();
        result.RuleIgnored.ShouldBe(1);
    }

    private static (IReadOnlyList<ModelInput> Models, NavisSelection Selection) Scene()
    {
        var pipe = Model("DON_PT", "pipe", 10, "Трубы", Vector3.Zero);
        var wall = Model("DON_KR", "wall", 20, "Стены", new Vector3(500, 0, 0));
        var selection = NavisSelection.Parse(new XElement("selectionsets",
            Folder("ПТ", "ПТ_Трубы", "PT", "Трубы"), Folder("КР", "КР_Стены", "KR", "Стены")));
        return ([pipe, wall], selection);
    }

    private static ModelInput Model(string key, string uniqueId, long id, string category, Vector3 offset)
    {
        var mesh = TestScenes.Box(offset, offset + new Vector3(1000));
        var header = new ModelHeader(key, "", 2022, DateTimeOffset.UnixEpoch, true, "Navisworks", 0, 0, 0);
        var element = new ElementGeometry(new ElementInfo(uniqueId, id, "OST_Any", null, null, null, null, []), mesh, Aabb.FromPositions(mesh.Positions), 0);
        var parameters = new Dictionary<string, IReadOnlyList<ParameterValue>> { [uniqueId] = [new("Категория", null, category, null)] };
        return new ModelInput(key, new ModelGeometry(header, [element], []), new ModelParameters(parameters));
    }

    private static XElement Folder(string folder, string set, string file, string category) =>
        new("viewfolder", new XAttribute("name", folder), new XElement("selectionset", new XAttribute("name", set),
            new XElement("findspec", new XElement("conditions",
                Condition("LcOaNodeSourceFile", file), Condition("LcRevitPropertyElementCategory", category)))));

    private static XElement Condition(string property, string value) =>
        new("condition", new XAttribute("test", property == "LcOaNodeSourceFile" ? "contains" : "equals"), new XAttribute("flags", 10),
            new XElement("property", new XElement("name", new XAttribute("internal", property))),
            new XElement("value", new XElement("data", new XAttribute("type", "wstring"), value)));

    private static XElement Test(string name, string left, string right) =>
        new("clashtest", new XAttribute("name", name), new XAttribute("tolerance", "0.0656167979"),
            new XElement("left", new XElement("clashselection", new XAttribute("selfintersect", "0"), new XElement("locator", "lcop_selection_set_tree/" + left))),
            new XElement("right", new XElement("clashselection", new XAttribute("selfintersect", "0"), new XElement("locator", "lcop_selection_set_tree/" + right))),
            new XElement("rules"));

    private static XElement RuleParam(string internalName) =>
        new("ruleparam", new XElement("value", new XElement("data", new XAttribute("type", "name"), new XElement("name", new XAttribute("internal", internalName)))));

    private static XDocument Report(params XElement[] results) =>
        new(new XElement("exchange", new XAttribute("units", "m"), new XElement("clashtest", new XAttribute("name", "PT - KR"),
            new XElement("clashresults", results))));

    private static XElement Result(long first, long second) =>
        new("clashresult", new XAttribute("name", "r"), new XAttribute("status", "active"),
            new XElement("clashpoint", new XElement("pos3f", new XAttribute("x", 0.75), new XAttribute("y", 0.5), new XAttribute("z", 0.5))),
            new XElement("clashobjects", Object(first), Object(second)));

    private static XElement Object(long id) =>
        new("clashobject", new XElement("smarttags", new XElement("smarttag", new XElement("name", "Id IntegerValue"), new XElement("value", id))));
}
