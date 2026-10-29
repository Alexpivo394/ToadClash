namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>
///     Откуда группа берёт элементы: модели названных разделов части и (или) конкретные модели по ключу. Разделы сравниваются
///     без учёта регистра, ключи моделей — точно.
/// </summary>
public sealed record GroupSource(IReadOnlyList<string> Disciplines, IReadOnlyList<string> ModelKeys)
{
    /// <summary>Источник группы раздела — модели этого раздела.</summary>
    public static GroupSource OfDiscipline(string discipline) => new([discipline], []);

    /// <summary>Входят ли элементы модели в источник.</summary>
    public bool Includes(string discipline, string modelKey)
    {
        foreach (var d in Disciplines)
            if (string.Equals(d, discipline, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var key in ModelKeys)
            if (string.Equals(key, modelKey, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Источник пуст: группа не отберёт ни одного элемента.</summary>
    public bool IsEmpty => Disciplines.Count == 0 && ModelKeys.Count == 0;
}

/// <summary>
///     Группа проверки — папка наборов и строка/столбец матрицы. По умолчанию одна на раздел моделей части; своя группа
///     (например, «Проезды» из моделей АР) проверяется отдельно от группы раздела.
/// </summary>
public sealed record SetGroup(Guid Id, string Name, GroupSource Source);
