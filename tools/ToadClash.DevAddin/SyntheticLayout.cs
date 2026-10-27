namespace ToadClash.DevAddin;

/// <summary>Параметры синтетических моделей; размеры раскладки заданы в миллиметрах.</summary>
public sealed record SyntheticOptions
{
    /// <summary>Создаёт параметры одного раздела или полного набора.</summary>
    /// <param name="section">АР, КР, ВК, ОВ, ЭОМ или «Все».</param>
    /// <param name="floors">Число этажей от 1 до 100.</param>
    /// <param name="cellsPerFloor">Число ячеек на этаже от 1 до 500.</param>
    public SyntheticOptions(string section = "Все", int floors = 20, int cellsPerFloor = 500)
    {
        if (section is not ("Все" or "АР" or "КР" or "ВК" or "ОВ" or "ЭОМ"))
            throw new ArgumentException("Неизвестный раздел.", nameof(section));
        if (floors is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(floors));
        if (cellsPerFloor is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(cellsPerFloor));
        Section = section;
        Floors = floors;
        CellsPerFloor = cellsPerFloor;
    }

    /// <summary>Выбранный раздел или «Все».</summary>
    public string Section { get; }
    /// <summary>Число этажей.</summary>
    public int Floors { get; }
    /// <summary>Число ячеек на этаже.</summary>
    public int CellsPerFloor { get; }
    /// <summary>Разделы для последовательного создания отдельных документов.</summary>
    public IReadOnlyList<string> Sections => Section == "Все" ? new[] { "АР", "КР", "ВК", "ОВ", "ЭОМ" } : new[] { Section };
    /// <summary>Нижняя оценка числа основных элементов: три на ячейку, без вложений и витражных панелей.</summary>
    public int MinimumElementCount => Sections.Count * Floors * CellsPerFloor * 3;
}

/// <summary>Положение ячейки в миллиметрах, этаж нумеруется с нуля.</summary>
public sealed record SyntheticCell(int Floor, double X, double Y, double Z);

/// <summary>Детерминированная общая раскладка для всех разделов.</summary>
public static class SyntheticLayout
{
    /// <summary>Перечисляет ячейки по этажам на сетке 8000 мм с высотой этажа 4000 мм.</summary>
    public static IEnumerable<SyntheticCell> Cells(SyntheticOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        for (var floor = 0; floor < options.Floors; floor++)
        for (var index = 0; index < options.CellsPerFloor; index++)
            yield return new SyntheticCell(floor, index % 25 * 8000, index / 25 * 8000, floor * 4000);
    }
}
