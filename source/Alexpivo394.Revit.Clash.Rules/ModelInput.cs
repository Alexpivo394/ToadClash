using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Модель раздела на входе проверки.</summary>
public sealed record ModelInput(string Discipline, ModelGeometry Geometry, ModelParameters Parameters)
{
    /// <summary>Те же элементы без сеток — для принадлежности наборам и предпросмотра.</summary>
    public ElementsInput ToElements() =>
        new(Discipline, Geometry.Header.ModelKey, Geometry.Elements.Select(e => e.Info).ToArray(), Parameters);
}

/// <summary>Элементы модели раздела без геометрии: описания и параметры, например из оглавления выгрузки.</summary>
public sealed record ElementsInput(string Discipline, string ModelKey, IReadOnlyList<ElementInfo> Elements, ModelParameters Parameters);
