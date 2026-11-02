namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>
///     Изоляция сбоев отдельных элементов при выгрузке: ошибка Revit на одном элементе не обрывает выгрузку модели, а
///     попадает в отчёт. Массовые сбои означают повреждённую модель, и тогда выгрузка прерывается.
/// </summary>
internal static class ElementGuard
{
    private const double MaxSkippedShare = 0.01;
    private const int MaxSkippedCount = 20;
    private const int ListedInError = 10;

    /// <summary>
    ///     Выполняет <paramref name="action" />. Ошибку Revit (<see cref="Autodesk.Revit.Exceptions.ApplicationException" />,
    ///     <see cref="InvalidOperationException" />, <see cref="ArgumentException" />) записывает в
    ///     <paramref name="skipped" />; остальные исключения, включая отмену, пробрасывает.
    /// </summary>
    /// <returns>false, если действие завершилось ошибкой Revit.</returns>
    public static bool TryRun(string uniqueId, long elementId, string stage, Action action, List<SkippedElement> skipped)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception) when (exception is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException
                                              && exception is not OperationCanceledException)
        {
            skipped.Add(new SkippedElement(uniqueId, elementId, stage, exception.Message));
            return false;
        }
    }

    /// <summary>Прерывает выгрузку, если пропущено больше 1 % видимых элементов и больше 20.</summary>
    /// <exception cref="InvalidOperationException">Пропущено слишком много элементов.</exception>
    public static void ThrowIfTooMany(IReadOnlyList<SkippedElement> skipped, int visible)
    {
        var elements = skipped.Select(s => s.UniqueId).Distinct(StringComparer.Ordinal).ToList();
        if (elements.Count <= MaxSkippedCount || elements.Count <= visible * MaxSkippedShare) return;

        throw new InvalidOperationException(
            $"Пропущено {elements.Count} элементов из {visible}: модель повреждена. Первые: {string.Join(", ", elements.Take(ListedInError))}");
    }
}
