using Alexpivo394.Revit.Clash.Geometry;
using ParameterValue = Alexpivo394.Revit.Clash.Geometry.ParameterValue;

namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>
///     Читает параметры экземпляра и его типа. Параметры типа читаются один раз на тип.
///     Длины - в мм, прочие вещественные - во внутренних единицах Revit, целые - как число;
///     для ссылки на элемент текст - имя элемента, числа нет.
/// </summary>
internal sealed class ParameterReader
{
    private readonly Dictionary<ElementId, IReadOnlyList<ParameterValue>> _typeValues = new();

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

    private static List<ParameterValue> ReadOwn(Element element)
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

    private static double? Number(Parameter parameter, Definition definition) => parameter.StorageType switch
    {
        StorageType.Double when IsLength(definition) => parameter.AsDouble().ToMillimeters(),
        StorageType.Double => parameter.AsDouble(),
        StorageType.Integer => parameter.AsInteger(),
        _ => null
    };

    private static bool IsLength(Definition definition) =>
#if REVIT2022_OR_GREATER
        definition.GetDataType() == SpecTypeId.Length;
#elif REVIT2021_OR_GREATER
        definition.GetSpecTypeId() == SpecTypeId.Length;
#else
        definition.ParameterType == ParameterType.Length;
#endif
}
