using System.Numerics;
using System.Xml.Linq;
using Alexpivo394.Revit.Clash.Rules;
using Alexpivo394.Revit.Clash.Tests.Engine;
using Shouldly;
using ToadClash.Bench;

namespace Alexpivo394.Revit.Clash.Tests.Bench;

public sealed class NavisCompareTests
{
    [Test]
    public void Compare_FindsCommonPair_IgnoresResolved_ReportsMissingElement()
    {
        var mesh = TestScenes.Box(Vector3.Zero, new Vector3(1000));
        var left = TestScenes.Model("left", "OV", (0, 0, 0), [new("a", "OST_PipeCurves", mesh)]);
        var right = TestScenes.Model("right", "AR", (0, 0, 0), [new("b", "OST_Walls", mesh)]);
        var configuration = TestScenes.Config([TestScenes.CategorySet("OV", "OST_PipeCurves"), TestScenes.CategorySet("AR", "OST_Walls")],
            [TestScenes.Cell("OV", "AR", true)], []);
        var leftId = left.Geometry.Elements[0].Info.ElementId;
        var rightId = right.Geometry.Elements[0].Info.ElementId;
        XElement Result(string status, long other) => new("clashresult", new XAttribute("name", status + other), new XAttribute("status", status),
            new XElement("clashobjects", Object(leftId), Object(other)));
        XElement Object(long id) => new("clashobject", new XElement("objectattribute", new XElement("name", "ID объекта"), new XElement("value", id)));
        var document = new XDocument(new XElement("exchange", new XElement("clashtest", new XAttribute("name", "test"),
            new XElement("clashresults", Result("active", rightId), Result("resolved", rightId), Result("new", 999)))));

        var report = NavisCompare.Compare(document, left, right, configuration);

        report.Common.Count.ShouldBe(1);
        report.OnlyNavis.ShouldBeEmpty();
        report.OnlyOurs.ShouldBeEmpty();
        report.Unmapped.Count.ShouldBe(1);
        report.IgnoredResolved.ShouldBe(1);

        document.Descendants("clashresult").Remove();
        NavisCompare.Compare(document, left, right, configuration).OnlyOurs.Count.ShouldBe(1);

        document.Descendants("clashresults").Single().Add(Result("active", rightId));
        var moved = TestScenes.Model("right", "AR", (10000, 0, 0), [new("b", "OST_Walls", mesh)]);
        NavisCompare.Compare(document, left, moved, configuration).OnlyNavis.Count.ShouldBe(1);

        var incomplete = new ModelInput(right.Discipline,
            new(right.Geometry.Header with { Complete = false }, right.Geometry.Elements, right.Geometry.Inventory), right.Parameters);
        Should.Throw<InvalidDataException>(() => NavisCompare.Compare(document, left, incomplete, configuration));

        var duplicateKey = new ModelInput(right.Discipline,
            new(right.Geometry.Header with { ModelKey = left.Geometry.Header.ModelKey }, right.Geometry.Elements, right.Geometry.Inventory), right.Parameters);
        Should.Throw<InvalidDataException>(() => NavisCompare.Compare(document, left, duplicateKey, configuration));
    }

    [Test]
    public void Compare_PrefersIdIntegerValueSmarttag_OverObjectAttribute()
    {
        var (left, right, configuration) = OverlappingModels();
        var document = Report(Result(null, Object(999, 10), Object(998, 20)));

        NavisCompare.Compare(document, left, right, configuration).Common.Count.ShouldBe(1);
    }

    [Test]
    public void Compare_EmptySmarttag_FallsBackToObjectAttribute_MissingIdIsUnmapped()
    {
        var (left, right, configuration) = OverlappingModels();
        var emptyTag = Object(10, 0);
        emptyTag.Descendants("value").Last().Value = "";
        var noId = Object(null, 0);
        noId.Descendants("value").Last().Value = "";
        var document = Report(Result(null, emptyTag, Object(null, 20)), Result(null, noId, Object(null, 20)));

        var report = NavisCompare.Compare(document, left, right, configuration);

        report.Common.Count.ShouldBe(1);
        report.Unmapped.Count.ShouldBe(1);
    }

    [Test]
    public void Compare_BothObjectsInLeftModel_MapsPairInsideLeftModel()
    {
        var (left, right, configuration) = OverlappingModels(extraLeft: true);
        var document = Report(Result(null, Object(null, 10), Object(null, 11)));

        var report = NavisCompare.Compare(document, left, right, configuration);

        report.Unmapped.ShouldBeEmpty();
        report.OnlyNavis.Single().ShouldBe(new ComparisonPair(new("left", "a"), new("left", "c")));
    }

    [Test]
    public void Compare_SameIdInBothModels_ChoosesElementNearestToClashPoint()
    {
        var (left, right, configuration) = OverlappingModels(extraLeft: true, rightId: 10, rightOffset: 5000);
        // Точка в метрах: рядом с правым элементом 10, хотя он записан первым; второй объект есть только слева.
        var document = Report(Result((5.5, 0.5, 0.5), Object(null, 10), Object(null, 11)));

        var report = NavisCompare.Compare(document, left, right, configuration);

        report.Unmapped.ShouldBeEmpty();
        report.OnlyNavis.Single().ShouldBe(new ComparisonPair(new("left", "c"), new("right", "b")));
    }

    private static (ModelInput Left, ModelInput Right, ClashConfiguration Configuration) OverlappingModels(
        bool extraLeft = false, long rightId = 20, float rightOffset = 500)
    {
        TestScenes.Item[] leftItems = extraLeft
            ? [new("a", "OST_PipeCurves", TestScenes.Box(Vector3.Zero, new Vector3(1000))), new("c", "OST_PipeCurves", TestScenes.Box(new Vector3(300, 0, 0), new Vector3(1300, 1000, 1000)))]
            : [new("a", "OST_PipeCurves", TestScenes.Box(Vector3.Zero, new Vector3(1000)))];
        var left = WithIds(TestScenes.Model("left", "OV", (0, 0, 0), leftItems), 10, 11);
        var right = WithIds(TestScenes.Model("right", "AR", (0, 0, 0),
            [new("b", "OST_Walls", TestScenes.Box(new Vector3(rightOffset, 0, 0), new Vector3(rightOffset + 1000, 1000, 1000)))]), rightId);
        var configuration = TestScenes.Config([TestScenes.CategorySet("OV", "OST_PipeCurves"), TestScenes.CategorySet("AR", "OST_Walls")],
            [TestScenes.Cell("OV", "AR", true)], []);
        return (left, right, configuration);
    }

    private static ModelInput WithIds(ModelInput model, params long[] ids) =>
        model with
        {
            Geometry = new(model.Geometry.Header,
                model.Geometry.Elements.Select((element, index) => element with { Info = element.Info with { ElementId = ids[index] } }).ToList(),
                model.Geometry.Inventory)
        };

    private static XDocument Report(params XElement[] results) =>
        new(new XElement("exchange", new XAttribute("units", "m"), new XElement("clashtest", new XAttribute("name", "test"),
            new XElement("clashresults", results))));

    private static XElement Result((double X, double Y, double Z)? point, XElement first, XElement second) =>
        new("clashresult", new XAttribute("name", "r"), new XAttribute("status", "active"),
            point is { } p ? new XElement("clashpoint", new XElement("pos3f", new XAttribute("x", p.X), new XAttribute("y", p.Y), new XAttribute("z", p.Z))) : null,
            new XElement("clashobjects", first, second));

    private static XElement Object(long? objectAttribute, long integerValue) =>
        new("clashobject",
            objectAttribute is { } id ? new XElement("objectattribute", new XElement("name", "ID объекта"), new XElement("value", id)) : null,
            new XElement("smarttags", new XElement("smarttag", new XElement("name", "Id IntegerValue"), new XElement("value", integerValue))));
}
