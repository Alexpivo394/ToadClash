namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>Модель, которая не совмещена с остальными моделями части.</summary>
/// <param name="ModelKey">Ключ модели.</param>
/// <param name="DistanceMm">Расстояние от основной части модели до моделей, совмещённых между собой, мм.</param>
public sealed record MisalignedModel(string ModelKey, double DistanceMm);

/// <summary>
///     Модели части лежат в разных местах общей системы координат: у какой-то модели не заданы или заданы другие общие
///     координаты. Совмещать такие модели движок не берётся — это ошибка данных проекта, а не повод искать коллизии.
/// </summary>
public sealed class ModelsNotAlignedException(IReadOnlyList<MisalignedModel> models)
    : Exception($"Модели не совмещены с остальными: {string.Join(", ", models.Select(m => m.ModelKey))}. Проверьте общие координаты моделей.")
{
    /// <summary>Несовмещённые модели.</summary>
    public IReadOnlyList<MisalignedModel> Models { get; } = models;
}
