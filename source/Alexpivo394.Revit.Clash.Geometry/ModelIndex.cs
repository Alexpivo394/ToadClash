namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Запись оглавления: элемент без сетки.</summary>
/// <param name="TriangleCount">Число треугольников сетки; -1 — файл до версии 3, где оно не записано.</param>
public sealed record ModelIndexEntry(ElementInfo Info, Aabb Bounds, ulong Fingerprint, int TriangleCount);

/// <summary>Оглавление файла геометрии: заголовок, элементы без сеток и опись.</summary>
public sealed record ModelIndex(ModelHeader Header, IReadOnlyList<ModelIndexEntry> Elements, IReadOnlyList<string> Inventory)
{
    /// <summary>Сумма треугольников по элементам; элементы с неизвестным числом не учитываются.</summary>
    public long TriangleCount => Elements.Sum(e => e.TriangleCount > 0 ? (long)e.TriangleCount : 0);
}
