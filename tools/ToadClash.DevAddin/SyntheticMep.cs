using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace ToadClash.DevAddin;

internal sealed class SyntheticMep(Document document, string section)
{
    private ElementId curveType = ElementId.InvalidElementId;
    private ElementId systemType = ElementId.InvalidElementId;

    internal void Prepare(Level level)
    {
        if (section == "ВК")
        {
            systemType = document.CollectElements().OfClass<PipingSystemType>().Cast<PipingSystemType>()
                .FirstOrDefault(type => type.SystemClassification == MEPSystemClassification.DomesticColdWater)?.Id
                ?? PipingSystemType.Create(document, MEPSystemClassification.DomesticColdWater, "Synthetic Cold Water").Id;
        }
        else if (section == "ОВ")
        {
            systemType = document.CollectElements().OfClass<MechanicalSystemType>().Cast<MechanicalSystemType>()
                .FirstOrDefault(type => type.SystemClassification == MEPSystemClassification.SupplyAir)?.Id
                ?? MechanicalSystemType.Create(document, MEPSystemClassification.SupplyAir, "Synthetic Supply Air").Id;
        }

        var typeClass = section switch { "ВК" => typeof(PipeType), "ОВ" => typeof(DuctType), _ => typeof(CableTrayType) };
        var candidates = document.CollectElements().OfClass(typeClass).ToElementIds().OrderBy(id => id.IntegerValue).ToArray();
        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            using var trial = new SubTransaction(document);
            trial.Start();
            try
            {
                curveType = candidate;
                Cell(new SyntheticCell(0, 0, 0, level.Elevation * 304.8), level);
                trial.RollBack();
                return;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException exception) { errors.Add(exception.Message); }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException exception) { errors.Add(exception.Message); }
            if (trial.GetStatus() == TransactionStatus.Started) trial.RollBack();
        }
        throw new InvalidOperationException($"{section}: стандартный шаблон не содержит рабочего типа с отводом. " +
            "Проверьте семейства фитингов и routing preferences. " + string.Join("; ", errors));
    }

    internal void Cell(SyntheticCell cell, Level level)
    {
        var elevation = cell.Z + (section == "ВК" ? 2600 : section == "ОВ" ? 2800 : 3000);
        var start = SyntheticBuilding.Point(cell.X + 3000, cell.Y + 1000, elevation);
        var corner = SyntheticBuilding.Point(cell.X + 3000, cell.Y + 5000, elevation);
        var end = SyntheticBuilding.Point(cell.X + 6000, cell.Y + 5000, elevation);
        var first = Segment(start, corner, level);
        var second = Segment(corner, end, level);
        document.Regenerate();
        var firstConnector = Nearest(first, corner);
        var secondConnector = Nearest(second, corner);
        var elbow = document.Create.NewElbowFitting(firstConnector, secondConnector);
        document.Regenerate();
        if (elbow is null || !firstConnector.IsConnected || !secondConnector.IsConnected)
            throw new InvalidOperationException($"{section}: отвод не соединил сегменты.");
    }

    private MEPCurve Segment(XYZ start, XYZ end, Level level)
    {
        if (section == "ВК")
        {
            var pipe = Pipe.Create(document, systemType, curveType, level.Id, start, end);
            SetSize(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, 100);
            return pipe;
        }
        if (section == "ОВ")
        {
            var duct = Duct.Create(document, systemType, curveType, level.Id, start, end);
            var diameter = duct.FindParameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
            if (diameter is not null && !diameter.IsReadOnly) diameter.Set(300 / 304.8);
            else
            {
                SetSize(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, 400);
                SetSize(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, 250);
            }
            return duct;
        }
        var tray = CableTray.Create(document, curveType, start, end, level.Id);
        SetSize(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, 300);
        SetSize(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, 100);
        return tray;
    }

    private static void SetSize(Element element, BuiltInParameter id, double millimeters)
    {
        var parameter = element.FindParameter(id) ?? throw new InvalidOperationException($"У элемента нет размера {id}.");
        if (parameter.IsReadOnly || !parameter.Set(millimeters / 304.8))
            throw new InvalidOperationException($"Не удалось установить размер {id}.");
    }

    private static Connector Nearest(MEPCurve curve, XYZ point) => curve.ConnectorManager.Connectors.Cast<Connector>()
        .Where(connector => connector.ConnectorType == ConnectorType.End)
        .OrderBy(connector => connector.Origin.DistanceTo(point)).First();
}
