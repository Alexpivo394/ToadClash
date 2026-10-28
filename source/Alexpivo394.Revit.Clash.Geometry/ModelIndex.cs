namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Запись оглавления: элемент без сетки.</summary>
/// <param name="TriangleCount">Число треугольников сетки; -1 — файл до версии 3, где оно не записано.</param>
/// <param name="MeshOffset">Смещение сжатой сетки от начала файла: по нему <see cref="ModelGeometryFile.ReadMesh" /> читает одну сетку.</param>
/// <param name="MeshLength">Длина сжатой сетки в байтах.</param>
public sealed record ModelIndexEntry(ElementInfo Info, Aabb Bounds, ulong Fingerprint, int TriangleCount, long MeshOffset, int MeshLength);

/// <summary>Оглавление файла геометрии: заголовок, элементы без сеток и опись.</summary>
public sealed record ModelIndex(ModelHeader Header, IReadOnlyList<ModelIndexEntry> Elements, IReadOnlyList<string> Inventory)
{
    /// <summary>Сумма треугольников по элементам; элементы с неизвестным числом не учитываются.</summary>
    public long TriangleCount => Elements.Sum(e => e.TriangleCount > 0 ? (long)e.TriangleCount : 0);
}
