namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Клетка матрицы проверок для пары групп; порядок групп не важен.</summary>
public sealed record MatrixCell(Guid GroupA, Guid GroupB, bool Enabled, double ToleranceMm = 20);

/// <summary>Пара наборов, исключённая из проверки.</summary>
public sealed record SetPairExclusion(Guid SetA, Guid SetB);

/// <summary>
///     Набор, исключённый из проверки со всей группой: то же, что исключение набора с каждым набором группы, включая наборы,
///     добавленные в группу позже.
/// </summary>
public sealed record SetGroupExclusion(Guid SetId, Guid GroupId);

/// <summary>Конфигурация проверки: группы, их наборы, матрица групп, исключения.</summary>
public sealed record ClashConfiguration(
    IReadOnlyList<SetGroup> Groups,
    IReadOnlyList<SelectionSet> Sets,
    IReadOnlyList<MatrixCell> Matrix,
    IReadOnlyList<SetPairExclusion> Exclusions,
    bool IgnoreConnectedMep = true)
{
    /// <summary>Исключения «набор × вся группа». В JSON необязательны: конфигурация без них читается с пустым списком.</summary>
    public IReadOnlyList<SetGroupExclusion> GroupExclusions { get; init; } = [];

    /// <summary>
    ///     Все исключённые пары наборов: явные и раскрытые из исключений с группой. Пара снимается с проверки, только если
    ///     исключено каждое сочетание наборов её элементов.
    /// </summary>
    public IEnumerable<SetPairExclusion> ExcludedSetPairs()
    {
        foreach (var exclusion in Exclusions) yield return exclusion;
        foreach (var exclusion in GroupExclusions)
        foreach (var set in Sets)
            if (set.GroupId == exclusion.GroupId) yield return new SetPairExclusion(exclusion.SetId, set.Id);
    }

    /// <summary>Клетка матрицы для пары групп без учёта порядка; null, если клетки нет.</summary>
    public MatrixCell? CellFor(Guid a, Guid b)
    {
        foreach (var cell in Matrix)
        {
            if (cell.GroupA == a && cell.GroupB == b) return cell;
            if (cell.GroupA == b && cell.GroupB == a) return cell;
        }

        return null;
    }

    /// <summary>Группа набора; null, если набора или его группы нет.</summary>
    public SetGroup? GroupOf(Guid setId)
    {
        var set = Sets.FirstOrDefault(s => s.Id == setId);
        return set is null ? null : Groups.FirstOrDefault(g => g.Id == set.GroupId);
    }
}
