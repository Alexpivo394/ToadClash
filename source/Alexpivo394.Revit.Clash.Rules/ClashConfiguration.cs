namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Ячейка матрицы проверок для пары разделов; порядок разделов не важен.</summary>
public sealed record MatrixCell(string DisciplineA, string DisciplineB, bool Enabled, double ToleranceMm = 20);

/// <summary>Пара наборов, исключённая из проверки.</summary>
public sealed record SetPairExclusion(Guid SetA, Guid SetB);

/// <summary>Конфигурация проверки: наборы, матрица разделов, исключения.</summary>
public sealed record ClashConfiguration(
    IReadOnlyList<SelectionSet> Sets,
    IReadOnlyList<MatrixCell> Matrix,
    IReadOnlyList<SetPairExclusion> Exclusions,
    bool IgnoreConnectedMep = true)
{
    /// <summary>Ячейка матрицы для пары разделов без учёта порядка и регистра; null, если ячейки нет.</summary>
    public MatrixCell? CellFor(string a, string b)
    {
        foreach (var cell in Matrix)
        {
            if (Same(cell.DisciplineA, a) && Same(cell.DisciplineB, b)) return cell;
            if (Same(cell.DisciplineA, b) && Same(cell.DisciplineB, a)) return cell;
        }

        return null;
    }

    private static bool Same(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
}
