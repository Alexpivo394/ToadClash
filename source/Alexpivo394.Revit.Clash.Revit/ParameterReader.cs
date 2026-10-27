using Alexpivo394.Revit.Clash.Geometry;
using ParameterValue = Alexpivo394.Revit.Clash.Geometry.ParameterValue;

namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>
///     Читает параметры экземпляра и его типа. Параметры типа читаются один раз на тип.
///     Длины и другие величины, измеряемые в мм (размеры труб, воздуховодов, лотков), - в мм, прочие размерные
///     вещественные - в единицах проекта, целые - как число; для ссылки на элемент текст - имя элемента, числа нет.
/// </summary>
internal sealed class ParameterReader
{
    private readonly Dictionary<ElementId, IReadOnlyList<ParameterValue>> _typeValues = new();
#if REVIT2021_OR_GREATER
    private readonly Dictionary<ForgeTypeId, bool> _lengthSpecs = new();
#else
    private readonly Dictionary<UnitType, bool> _lengthSpecs = new();
#endif

    /// <summary>Параметры элемента, затем параметры его типа. Параметры без значения пропускаются.</summary>
    public IReadOnlyList<ParameterValue> Read(Element element)
    {
        var values = ReadOwn(element);
        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId) return values;

        if (!_typeValues.TryGetValue(typeId, out var typeValues))
        {
            typeValues = typeId.ToElement(element.Document) is { } type ? ReadOwn(type) : [];
            _typeValues.Add(typeId, typeValues);
        }

        values.AddRange(typeValues);
        return values;
    }

    private List<ParameterValue> ReadOwn(Element element)
    {
        var values = new List<ParameterValue>();
        foreach (Parameter parameter in element.Parameters)
        {
            if (!parameter.HasValue) continue;
            var definition = parameter.Definition;
            values.Add(new ParameterValue(
                definition.Name,
                parameter.IsShared ? parameter.GUID : null,
                Text(parameter),
                Number(parameter, definition)));
        }

        return values;
    }

    private static string? Text(Parameter parameter)
    {
        if (parameter.StorageType == StorageType.ElementId && parameter.AsElement() is { } referenced) return referenced.Name;
        return parameter.AsValueString() ?? parameter.AsString();
    }

    private double? Number(Parameter parameter, Definition definition) => parameter.StorageType switch
    {
        StorageType.Double when IsLength(definition) => parameter.AsDouble().ToMillimeters(),
        StorageType.Double => ProjectNumber(parameter, definition),
        StorageType.Integer => parameter.AsInteger(),
        _ => null
    };

    private static double ProjectNumber(Parameter parameter, Definition definition)
    {
        var value = parameter.AsDouble();
#if REVIT2022_OR_GREATER
        var spec = definition.GetDataType();
        if (!UnitUtils.IsMeasurableSpec(spec)) return value;
#elif REVIT2021_OR_GREATER
        var spec = definition.GetSpecTypeId();
        if (!UnitUtils.IsSpec(spec)) return value;
#else
        var spec = definition.UnitType;
        if (!UnitUtils.IsValidUnitType(spec)) return value;
#endif
        using var units = parameter.Element.Document.GetUnits();
        using var format = units.GetFormatOptions(spec);
#if REVIT2021_OR_GREATER
        return UnitUtils.ConvertFromInternalUnits(value, format.GetUnitTypeId());
#else
        return UnitUtils.ConvertFromInternalUnits(value, format.DisplayUnits);
#endif
    }

    /// <summary>Величина допускает мм: длина или размер сечения MEP. Ответ кешируется по типу величины.</summary>
    private bool IsLength(Definition definition)
    {
#if REVIT2022_OR_GREATER
        var spec = definition.GetDataType();
        if (_lengthSpecs.TryGetValue(spec, out var isLength)) return isLength;
        isLength = UnitUtils.IsMeasurableSpec(spec) && UnitUtils.GetValidUnits(spec).Contains(UnitTypeId.Millimeters);
#elif REVIT2021_OR_GREATER
        var spec = definition.GetSpecTypeId();
        if (_lengthSpecs.TryGetValue(spec, out var isLength)) return isLength;
        isLength = UnitUtils.IsSpec(spec) && UnitUtils.GetValidUnits(spec).Contains(UnitTypeId.Millimeters);
#else
        var spec = definition.UnitType;
        if (_lengthSpecs.TryGetValue(spec, out var isLength)) return isLength;
        isLength = UnitUtils.IsValidUnitType(spec) && UnitUtils.GetValidDisplayUnits(spec).Contains(DisplayUnitType.DUT_MILLIMETERS);
#endif
        _lengthSpecs.Add(spec, isLength);
        return isLength;
    }
}
