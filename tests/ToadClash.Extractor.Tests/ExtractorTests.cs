using System.Reflection;
using Alexpivo394.Revit.Clash.Revit;
using Shouldly;

namespace ToadClash.Extractor.Tests;

public sealed class ExtractorTests
{
    [Test]
    [Arguments(UnitType.UT_Area, DisplayUnitType.DUT_SQUARE_METERS, 10, 0.9290304)]
    [Arguments(UnitType.UT_Volume, DisplayUnitType.DUT_CUBIC_METERS, 10, 0.28316846592)]
    [Arguments(UnitType.UT_Angle, DisplayUnitType.DUT_DEGREES, Math.PI, 180)]
    [Arguments(UnitType.UT_Temperature, DisplayUnitType.DUT_CELSIUS, 300, 26.85)]
    [Arguments(UnitType.UT_Length, DisplayUnitType.DUT_GENERAL, 1, 304.8)]
    [Arguments(UnitType.UT_PipeSize, DisplayUnitType.DUT_CENTIMETERS, 1, 304.8)]
    [Arguments(UnitType.UT_Number, DisplayUnitType.DUT_GENERAL, 2.5, 2.5)]
    [Arguments(UnitType.UT_Undefined, DisplayUnitType.DUT_GENERAL, 3.5, 3.5)]
    public void NumbersUseProjectUnitsExceptLength(UnitType spec, DisplayUnitType unit, double value, double expected)
    {
        var doc = new Document();
        doc.Units.Formats[spec] = unit;
        var element = new Element { Document = doc };
        element.Parameters.Add(new Parameter { Element = element, Definition = new Definition { UnitType = spec }, Value = value });

        new ParameterReader().Read(element).Single().Number!.Value.ShouldBe(expected, 1e-10);
    }

    [Test]
    public void TypeParametersAlsoUseProjectUnits()
    {
        var doc = new Document();
        doc.Units.Formats[UnitType.UT_Area] = DisplayUnitType.DUT_SQUARE_METERS;
        var type = new ElementType { Id = new(2), Document = doc };
        type.Parameters.Add(new Parameter { Element = type, Definition = new Definition { UnitType = UnitType.UT_Area }, Value = 10 });
        doc.Elements.Add(type);
        var reader = new ParameterReader();
        reader.Read(new Element { Document = doc, TypeId = type.Id }).Single().Number!.Value.ShouldBe(0.9290304, 1e-10);
        reader.Read(new Element { Document = doc, TypeId = type.Id }).Single().Number!.Value.ShouldBe(0.9290304, 1e-10);
    }

    [Test]
    public void NestedOutsideViewCollectorIsNotExported()
    {
        var doc = new Document();
        var parent = new FamilyInstance { Id = new(1), Document = doc };
        var filteredChild = new FamilyInstance { Id = new(2), Document = doc };
        parent.Children.Add(filteredChild.Id);
        doc.Elements.AddRange([parent, filteredChild]);
        doc.Visible.Add(parent);

        Collect(doc).Select(element => element.Id.IntegerValue).ShouldBe([1]);
    }

    [Test]
    public void VisibleNestedAppearsOnceEvenWhenAlsoReachedThroughParent()
    {
        var doc = new Document();
        var parent = new FamilyInstance { Id = new(1), Document = doc };
        var child = new FamilyInstance { Id = new(2), Document = doc };
        var grandchild = new FamilyInstance { Id = new(3), Document = doc };
        parent.Children.Add(child.Id);
        child.Children.Add(grandchild.Id);
        doc.Elements.AddRange([parent, child, grandchild]);
        doc.Visible.AddRange([parent, child, grandchild]);

        Collect(doc).Select(element => element.Id.IntegerValue).ShouldBe([1, 2, 3]);
    }

    [Test]
    public void RootComponent_IsTopLevelInstanceOfNestingChain()
    {
        var doc = new Document();
        var parent = new FamilyInstance { Id = new(1), Document = doc };
        var child = new FamilyInstance { Id = new(2), Document = doc, SuperComponent = parent };
        var grandchild = new FamilyInstance { Id = new(3), Document = doc, SuperComponent = child };

        Root(grandchild).ShouldBe("1");
        Root(child).ShouldBe("1");
        Root(parent).ShouldBeNull();
        Root(new Element { Id = new(4), Document = doc }).ShouldBeNull();
    }

    [Test]
    public void RootComponent_OfInsulation_IsProductOfHost()
    {
        var doc = new Document();
        var fitting = new FamilyInstance { Id = new(1), Document = doc };
        var nestedFitting = new FamilyInstance { Id = new(2), Document = doc, SuperComponent = fitting };
        var pipe = new Element { Id = new(3), Document = doc };
        doc.Elements.AddRange([fitting, nestedFitting, pipe]);

        Root(new InsulationLiningBase { Id = new(4), Document = doc, HostElementId = pipe.Id }).ShouldBe("3");
        Root(new InsulationLiningBase { Id = new(5), Document = doc, HostElementId = fitting.Id }).ShouldBe("1");
        Root(new InsulationLiningBase { Id = new(6), Document = doc, HostElementId = nestedFitting.Id }).ShouldBe("1");
        Root(new InsulationLiningBase { Id = new(7), Document = doc, HostElementId = new(99) }).ShouldBeNull();
        Root(new InsulationLiningBase { Id = new(8), Document = doc }).ShouldBeNull();
    }

    [Test]
    public void Origin_IsInternalOrigin_RegardlessOfElements()
    {
        // Начало не зависит от того, какой элемент коллектор вернул первым (A7-04).
        Origin(new XYZ(100.4, 7, 3)).ShouldBe((0d, 0d, 0d));
        Origin(new XYZ(-5000, 12, 1)).ShouldBe((0d, 0d, 0d));
    }

    [Test]
    public void Extract_FailingElement_IsSkippedAndReported()
    {
        var doc = new Document();
        doc.Elements.Add(new View3D { Id = new(100) });
        doc.Visible.Add(new Element { Id = new(1), Document = doc, GeometryError = new Autodesk.Revit.Exceptions.ApplicationException() });
        doc.Visible.Add(new Element { Id = new(2), Document = doc });

        var result = GeometryExtractor.Extract(doc, new ExtractOptions("m", "Элемент"), CancellationToken.None);

        result.Skipped.Select(s => (s.UniqueId, s.Stage)).ShouldBe([("1", "геометрия")]);
    }

    private static (double, double, double) Origin(XYZ firstElementAt)
    {
        var doc = new Document();
        doc.Elements.Add(new View3D { Id = new(100) });
        doc.Visible.Add(new Element { Id = new(1), Document = doc, Box = new BoundingBox { Min = firstElementAt, Max = firstElementAt } });
        var header = GeometryExtractor.Extract(doc, new ExtractOptions("m", "Элемент"), CancellationToken.None).Geometry.Header;
        return (header.OriginX, header.OriginY, header.OriginZ);
    }

    private static string? Root(Element element) => (string?)typeof(GeometryExtractor)
        .GetMethod("RootComponentUniqueId", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [element]);

    private static List<Element> Collect(Document doc) => (List<Element>)typeof(GeometryExtractor)
        .GetMethod("CollectVisible", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, [doc, new View3D(), new List<BuiltInCategory> { BuiltInCategory.OST_GenericModel }])!;
}
