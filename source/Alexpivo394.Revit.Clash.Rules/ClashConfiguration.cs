namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Клетка матрицы проверок для пары групп; порядок групп не важен.</summary>
public sealed record MatrixCell(Guid GroupA, Guid GroupB, bool Enabled, double ToleranceMm = 20);

/// <summary>Пара наборов, исключённая из проверки.</summary>
public sealed record SetPairExclusion(Guid SetA, Guid SetB);

/// <summary>Конфигурация проверки: группы, их наборы, матрица групп, исключения.</summary>
public sealed record ClashConfiguration(
    IReadOnlyList<SetGroup> Groups,
    IReadOnlyList<SelectionSet> Sets,
    IReadOnlyList<MatrixCell> Matrix,
    IReadOnlyList<SetPairExclusion> Exclusions,
    bool IgnoreConnectedMep = true)
{
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
