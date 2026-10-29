// Тестовая граница API. Не моделирует геометрию и видимость Revit:
// коллекция кандидатов и единицы проекта задаются тестом явно.
global using Autodesk.Revit.DB;
global using ToadClash.Extractor.Tests;
using Alexpivo394.Revit.Clash.Revit;

namespace Autodesk.Revit.DB
{
    public enum StorageType { None, Double, Integer, String, ElementId }
    public enum ParameterType { Length, Other }
    public enum UnitType { UT_Undefined, UT_Length, UT_PipeSize, UT_Area, UT_Volume, UT_Angle, UT_Temperature, UT_Number }
    public enum DisplayUnitType { DUT_MILLIMETERS, DUT_CENTIMETERS, DUT_SQUARE_METERS, DUT_CUBIC_METERS, DUT_DEGREES, DUT_CELSIUS, DUT_GENERAL }
    public sealed record ForgeTypeId(UnitType Value);
    public static class SpecTypeId { public static ForgeTypeId Length { get; } = new(UnitType.UT_Length); }
    public static class UnitTypeId { public static DisplayUnitType Millimeters => DisplayUnitType.DUT_MILLIMETERS; }
    public sealed class Definition
    {
        public string Name { get; init; } = "Параметр";
        public UnitType UnitType { get; init; }
        public ParameterType ParameterType => UnitType == UnitType.UT_Length ? ParameterType.Length : ParameterType.Other;
        public ForgeTypeId GetDataType() => new(UnitType);
        public ForgeTypeId GetSpecTypeId() => new(UnitType);
    }
    public sealed class Parameter
    {
        public Definition Definition { get; init; } = new();
        public StorageType StorageType { get; init; } = StorageType.Double;
        public bool HasValue { get; init; } = true;
        public bool IsShared => false;
        public Guid GUID => throw new InvalidOperationException();
        public Element Element { get; set; } = null!;
        public double Value { get; init; }
        public double AsDouble() => Value;
        public int AsInteger() => (int)Value;
        public string? AsValueString() => "отображаемое значение";
        public string? AsString() => null;
        public Element? AsElement() => null;
        public T? AsElement<T>() where T : Element => null;
    }
    public sealed class Units : IDisposable
    {
        public Dictionary<UnitType, DisplayUnitType> Formats { get; } = new();
        public FormatOptions GetFormatOptions(UnitType type) => new(Formats[type]);
        public FormatOptions GetFormatOptions(ForgeTypeId type) => GetFormatOptions(type.Value);
        public void Dispose() { }
    }
    public sealed class FormatOptions(DisplayUnitType unit) : IDisposable
    {
        public DisplayUnitType DisplayUnits => unit;
        public DisplayUnitType GetUnitTypeId() => unit;
        public void Dispose() { }
    }
    public static class UnitUtils
    {
        public static bool IsValidUnitType(UnitType type) => type != UnitType.UT_Undefined;
        public static bool IsSpec(ForgeTypeId type) => IsValidUnitType(type.Value);
        public static bool IsMeasurableSpec(ForgeTypeId type) => IsSpec(type);
        // Размеры труб, как и длины, допускают мм; остальные величины - нет.
        public static IList<DisplayUnitType> GetValidDisplayUnits(UnitType type) =>
            type is UnitType.UT_Length or UnitType.UT_PipeSize ? [DisplayUnitType.DUT_MILLIMETERS, DisplayUnitType.DUT_CENTIMETERS] : [DisplayUnitType.DUT_GENERAL];
        public static IList<DisplayUnitType> GetValidUnits(ForgeTypeId type) => GetValidDisplayUnits(type.Value);
        public static double ConvertFromInternalUnits(double value, DisplayUnitType unit) => unit switch
        {
            DisplayUnitType.DUT_MILLIMETERS => value * 304.8,
            DisplayUnitType.DUT_CENTIMETERS => value * 30.48,
            DisplayUnitType.DUT_SQUARE_METERS => value * 0.09290304,
            DisplayUnitType.DUT_CUBIC_METERS => value * 0.028316846592,
            DisplayUnitType.DUT_DEGREES => value * 180 / Math.PI,
            DisplayUnitType.DUT_CELSIUS => value - 273.15,
            _ => value
        };
    }
    public sealed record ElementId(int IntegerValue)
    {
        public static ElementId InvalidElementId { get; } = new(-1);
    }
    public enum BuiltInCategory { OST_GenericModel = -2000151 }
    public enum BuiltInParameter { INSTANCE_REFERENCE_LEVEL_PARAM, SCHEDULE_LEVEL_PARAM }
    public enum CategoryType { Model }
    public sealed class Category
    {
        public ElementId Id { get; } = new((int)BuiltInCategory.OST_GenericModel);
        public CategoryType CategoryType => CategoryType.Model;
        public List<Category> SubCategories { get; } = [];
    }
    public class Element
    {
        public ElementId Id { get; init; } = new(1);
        public string UniqueId => Id.IntegerValue.ToString();
        public Document Document { get; set; } = null!;
        public string Name { get; init; } = "Элемент";
        public Category Category { get; } = new();
        public List<Parameter> Parameters { get; } = [];
        public ElementId TypeId { get; init; } = ElementId.InvalidElementId;
        public ElementId GetTypeId() => TypeId;
        public ElementId LevelId => ElementId.InvalidElementId;
        public ElementId WorksetId => ElementId.InvalidElementId;
        public bool IsHidden(View view) => false;
        public BoundingBox? get_BoundingBox(View view) => null;
        public GeometryElement? get_Geometry(Options options) => null;
        public Parameter? get_Parameter(BuiltInParameter parameter) => null;
    }
    public class FamilyInstance : Element
    {
        public List<ElementId> Children { get; } = [];
        public ICollection<ElementId> GetSubComponentIds() => Children;
        public Element? SuperComponent { get; init; }
    }
    public class InsulationLiningBase : Element { public ElementId HostElementId { get; init; } = ElementId.InvalidElementId; }
    public class View : Element;
    public sealed class View3D : View { public bool IsTemplate => false; }
    public sealed class Group : Element;
    public sealed class AssemblyInstance : Element;
    public sealed class RevitLinkInstance : Element;
    public sealed class Level : Element;
    public sealed class ElementType : Element { public string FamilyName => "Семейство"; }
    public sealed class Document
    {
        public List<Element> Elements { get; } = [];
        public List<Element> Visible { get; } = [];
        public Units Units { get; } = new();
        public Units GetUnits() => Units;
        public Settings Settings { get; } = new();
        public string PathName => "fixture.rvt";
        public Application Application { get; } = new();
        public bool IsWorkshared => false;
        public WorksetTable GetWorksetTable() => new();
    }
    public sealed class Settings { public List<Category> Categories { get; } = [new()]; }
    public sealed class Application { public string VersionNumber => "2022"; }
    public sealed class WorksetTable { public Element? GetWorkset(ElementId id) => null; }
    public sealed class Options { public View? View { get; init; } public bool ComputeReferences { get; init; } }
    public sealed class XYZ
    {
        public static XYZ Zero { get; } = new();
        public static XYZ operator +(XYZ a, XYZ b) => new();
        public static XYZ operator /(XYZ a, double b) => new();
    }
    public sealed class Transform
    {
        public static Transform Identity { get; } = new();
        public double Determinant => 1;
        public Transform Multiply(Transform other) => this;
        public XYZ OfPoint(XYZ point) => point;
    }
    public sealed class BoundingBox { public Transform Transform => Transform.Identity; public XYZ Min => new(); public XYZ Max => new(); }
    public class GeometryObject;
    public sealed class GeometryElement : List<GeometryObject>;
    public sealed class GeometryInstance : GeometryObject
    {
        public Transform Transform => Transform.Identity;
        public GeometryElement GetSymbolGeometry() => [];
    }
    public sealed class Solid : GeometryObject { public FaceArray Faces { get; } = []; }
    public sealed class FaceArray : List<Face> { public int Size => Count; }
    public sealed class Face { public Mesh Triangulate(double detail) => new(); }
    public sealed class Mesh : GeometryObject
    {
        public int NumTriangles => 0;
        public IList<XYZ> Vertices => [];
        public Triangle get_Triangle(int index) => new();
    }
    public sealed class Triangle { public int get_Index(int index) => index; }
    public sealed class SolidOrShellTessellationControls { public double Accuracy { get; set; } }
    public sealed class TriangulatedSolidOrShell
    {
        public int ShellComponentCount => 0;
        public TriangulatedShellComponent GetShellComponent(int index) => new();
    }
    public sealed class TriangulatedShellComponent
    {
        public int VertexCount => 0;
        public int TriangleCount => 0;
        public XYZ GetVertex(int index) => new();
        public TriangleInShellComponent GetTriangle(int index) => new();
    }
    public sealed class TriangleInShellComponent { public int VertexIndex0 => 0; public int VertexIndex1 => 0; public int VertexIndex2 => 0; }
    public static class SolidUtils { public static TriangulatedSolidOrShell TessellateSolidOrShell(Solid solid, SolidOrShellTessellationControls controls) => new(); }
}

namespace ToadClash.Extractor.Tests
{
    internal static class ApiExtensions
    {
        public static Element? ToElement(this ElementId id, Document doc) => doc.Elements.Find(element => element.Id == id);
        public static T? ToElement<T>(this ElementId id, Document doc) where T : Element => id.ToElement(doc) as T;
        public static ElementId ToElementId(this BuiltInCategory category) => new((int)category);
        public static double ToMillimeters(this double value) => value * 304.8;
        public static IEnumerable<Element> CollectElements(this Document doc) => doc.Elements;
        public static IEnumerable<Element> CollectElements(this Document doc, View view) => doc.Visible;
        public static IEnumerable<Element> WhereElementIsNotElementType(this IEnumerable<Element> elements) => elements.Where(element => element is not ElementType);
        public static IEnumerable<Element> OfCategories(this IEnumerable<Element> elements, ICollection<BuiltInCategory> categories) => elements;
        public static IEnumerable<Element> OfClass(this IEnumerable<Element> elements, Type type) => elements.Where(type.IsInstanceOfType);
    }
}

namespace Alexpivo394.Revit.Clash.Revit
{
    internal sealed class CoordinateTransform
    {
        public static CoordinateTransform FromDocument(Document doc) => new();
        public Vector3d RoundedOrigin(XYZ point) => new(0, 0, 0);
        public Vector3d[] ToSharedMillimeters(IList<XYZ> points, Transform transform) => [];
    }
    internal static class ConnectivityReader
    {
        public static IReadOnlyList<string> ReadConnectedUniqueIds(Element element) => [];
    }
}

namespace Autodesk.Revit.Exceptions
{
    public class ApplicationException : System.Exception;
}
