using System.IO;
using Autodesk.Revit.DB.Structure;

namespace ToadClash.DevAddin;

internal static class SyntheticFamilies
{
    internal static (FamilySymbol Column, FamilySymbol Nested) Create(Document target, SyntheticTemplates templates, string directory)
    {
        var application = target.Application;
        var columnDocument = application.NewFamilyDocument(templates.Column);
        Family column;
        try
        {
            BuildSolid(columnDocument, 400, 400, 3600, false);
            SaveFamily(columnDocument, directory, "SyntheticColumn");
            column = columnDocument.LoadFamily(target);
        }
        finally { columnDocument.Close(false); }

        var parent = application.NewFamilyDocument(templates.Generic);
        try
        {
            var child = application.NewFamilyDocument(templates.Generic);
            Family nested;
            try
            {
                BuildSolid(child, 300, 300, 600, true);
                SaveFamily(child, directory, "SyntheticSharedChild");
                nested = child.LoadFamily(parent);
            }
            finally { child.Close(false); }

            using (var transaction = new Transaction(parent, "Вложенное общее семейство"))
            {
                transaction.Start();
                var symbol = nested.GetFamilySymbolIds().First().ToElement<FamilySymbol>(parent)!;
                symbol.Activate();
                parent.Regenerate();
                parent.FamilyCreate.NewFamilyInstance(XYZ.Zero, symbol, StructuralType.NonStructural);
                AddSolid(parent, 600, 600, 200);
                if (parent.FamilyManager.CurrentType is null) parent.FamilyManager.NewType("Synthetic");
                SyntheticGenerationRun.Commit(transaction);
            }
            SaveFamily(parent, directory, "SyntheticNestedParent");
            var family = parent.LoadFamily(target);
            return (column.GetFamilySymbolIds().First().ToElement<FamilySymbol>(target)!,
                family.GetFamilySymbolIds().First().ToElement<FamilySymbol>(target)!);
        }
        finally { parent.Close(false); }
    }

    private static void BuildSolid(Document document, double width, double depth, double height, bool shared)
    {
        using var transaction = new Transaction(document, "Геометрия синтетического семейства");
        transaction.Start();
        if (document.FamilyManager.CurrentType is null) document.FamilyManager.NewType("Synthetic");
        if (shared)
        {
            var parameter = document.OwnerFamily.FindParameter(BuiltInParameter.FAMILY_SHARED)
                ?? throw new InvalidOperationException("Шаблон семейства не поддерживает общий экземпляр.");
            if (!parameter.Set(1)) throw new InvalidOperationException("Не удалось сделать вложенное семейство общим.");
        }
        AddSolid(document, width, depth, height);
        SyntheticGenerationRun.Commit(transaction);
    }

    private static void AddSolid(Document document, double width, double depth, double height)
    {
        using var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
        using var sketch = SketchPlane.Create(document, plane);
        var curves = new CurveArray();
        foreach (var curve in SyntheticBuilding.Rectangle(0, 0, 0, width, depth)) curves.Append(curve);
        var profile = new CurveArrArray();
        profile.Append(curves);
        document.FamilyCreate.NewExtrusion(true, profile, sketch, height / 304.8);
    }

    private static void SaveFamily(Document document, string directory, string name)
    {
        using var options = new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 };
        document.SaveAs(Path.Combine(directory, name + ".rfa"), options);
    }
}
