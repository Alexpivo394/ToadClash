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
        var configuration = new ClashConfiguration([TestScenes.CategorySet("OV", "OST_PipeCurves"), TestScenes.CategorySet("AR", "OST_Walls")],
            [new MatrixCell("OV", "AR", true)], []);
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
}
