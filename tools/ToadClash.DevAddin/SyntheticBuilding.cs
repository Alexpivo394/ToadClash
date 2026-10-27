using Autodesk.Revit.DB.Structure;

namespace ToadClash.DevAddin;

internal sealed class SyntheticBuilding
{
    private readonly Document document;
    private readonly WallType wall;
    private readonly WallType curtain;
    private readonly FloorType slab;
    private readonly FamilySymbol column;
    private readonly FamilySymbol nested;

    internal SyntheticBuilding(Document document, SyntheticTemplates templates, string directory)
    {
        this.document = document;
        wall = document.CollectElements().OfClass<WallType>().Cast<WallType>().First(type => type.Kind == WallKind.Basic);
        curtain = document.CollectElements().OfClass<WallType>().Cast<WallType>().First(type => type.Kind == WallKind.Curtain);
        slab = document.CollectElements().OfClass<FloorType>().Cast<FloorType>().First(type => !type.IsFoundationSlab);
        (column, nested) = SyntheticFamilies.Create(document, templates, directory);
    }

    internal void Activate()
    {
        column.Activate();
        nested.Activate();
        document.Regenerate();
    }

    internal void Cell(SyntheticCell cell, Level level, bool structural)
    {
        var start = Point(cell.X, cell.Y + 4000, cell.Z);
        var end = Point(cell.X + 6000, cell.Y + 4000, cell.Z);
        var createdWall = Wall.Create(document, Line.CreateBound(start, end), wall.Id, level.Id, 3600 / 304.8, 0, false, structural);
        WallUtils.DisallowWallJoinAtEnd(createdWall, 0);
        WallUtils.DisallowWallJoinAtEnd(createdWall, 1);
        Floor.Create(document, new[] { Rectangle(cell.X, cell.Y, cell.Z, 6000, 6000) }, slab.Id, level.Id);
        document.Create.NewFamilyInstance(Point(cell.X + 500, cell.Y + 500, cell.Z), column, level, StructuralType.Column);
    }

    internal void Details(Level level)
    {
        var elevation = level.Elevation * 304.8;
        var glass = Wall.Create(document,
            Line.CreateBound(Point(-8000, 0, elevation), Point(-2000, 0, elevation)),
            curtain.Id, level.Id, 3600 / 304.8, 0, false, false);
        document.Regenerate();
        glass.CurtainGrid.AddGridLine(false, Point(-5000, 0, elevation + 1800), false);
        glass.CurtainGrid.AddGridLine(true, Point(-5000, 0, elevation + 1800), false);
        var first = document.Create.NewFamilyInstance(Point(-8000, 3000, elevation), nested, level, StructuralType.NonStructural);
        var second = document.Create.NewFamilyInstance(Point(-6000, 3000, elevation), nested, level, StructuralType.NonStructural);
        document.Regenerate();
        document.Create.NewGroup(new[] { first.Id, second.Id });
    }

    internal static XYZ Point(double x, double y, double z) => new(x / 304.8, y / 304.8, z / 304.8);

    internal static CurveLoop Rectangle(double x, double y, double z, double width, double depth)
    {
        var points = new[] { Point(x, y, z), Point(x + width, y, z), Point(x + width, y + depth, z), Point(x, y + depth, z) };
        var loop = new CurveLoop();
        for (var index = 0; index < points.Length; index++) loop.Append(Line.CreateBound(points[index], points[(index + 1) % points.Length]));
        return loop;
    }
}
