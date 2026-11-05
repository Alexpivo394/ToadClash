namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Элемент конфигурации, который изменили оба пользователя по-разному.</summary>
/// <param name="Key">Ключ элемента: <c>group:…</c>, <c>set:…</c>, <c>cell:…|…</c>, <c>exclusion:…|…</c>, <c>groupExclusion:набор|группа</c>, <c>flag:ignoreConnectedMep</c>.</param>
/// <param name="Label">Что это, для пользователя: «набор «Стены» (АР)».</param>
/// <param name="Kind"><c>group</c>, <c>set</c>, <c>cell</c>, <c>exclusion</c> или <c>flag</c>.</param>
public sealed record MergeConflict(string Key, string Label, string Kind);

/// <param name="Merged">Результат; в конфликтующих элементах — вариант другого пользователя, кроме названных в <c>keepMine</c>.</param>
public sealed record MergeResult(ClashConfiguration Merged, IReadOnlyList<MergeConflict> Conflicts);

/// <summary>
///     Трёхстороннее слияние черновика: <c>base</c> — версия, с которой пользователь начал, <c>theirs</c> — то, что сохранено
///     сейчас, <c>mine</c> — его правки. Изменения считаются по отдельным группам, наборам, клеткам, исключениям и флагу
///     соединённых MEP, поэтому правки разных элементов у двух людей не затирают друг друга.
/// </summary>
public static class ConfigurationMerge
{
    public const string FlagKey = "flag:ignoreConnectedMep";

    public static MergeResult Merge(ClashConfiguration @base, ClashConfiguration theirs, ClashConfiguration mine, IReadOnlyCollection<string>? keepMine = null)
    {
        if (@base is null) throw new ArgumentNullException(nameof(@base));
        if (theirs is null) throw new ArgumentNullException(nameof(theirs));
        if (mine is null) throw new ArgumentNullException(nameof(mine));

        var keep = new HashSet<string>(keepMine ?? [], StringComparer.Ordinal);
        var conflicts = new List<MergeConflict>();
        var names = theirs.Groups.Concat(mine.Groups).Concat(@base.Groups).GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First().Name);
        string GroupName(Guid id) => names.TryGetValue(id, out var name) ? name : "?";
        var setNames = theirs.Sets.Concat(mine.Sets).Concat(@base.Sets).GroupBy(s => s.Id).ToDictionary(s => s.Key, s => s.First());
        string SetLabel(Guid id) => setNames.TryGetValue(id, out var set) ? $"«{set.Name}» ({GroupName(set.GroupId)})" : "?";

        var groups = Items(@base.Groups, theirs.Groups, mine.Groups, g => $"group:{g.Id:N}", g => $"группа «{g.Name}»", "group", keep, conflicts);
        var sets = Items(@base.Sets, theirs.Sets, mine.Sets, s => $"set:{s.Id:N}", s => $"набор «{s.Name}» ({GroupName(s.GroupId)})", "set", keep, conflicts);
        // Клетка симметрична: «ОВ × АР» и «АР × ОВ» — одно и то же, поэтому группы в клетке упорядочиваются до сравнения.
        var cells = Items(Ordered(@base.Matrix), Ordered(theirs.Matrix), Ordered(mine.Matrix), c => PairKey("cell", c.GroupA, c.GroupB),
            c => CellLabel(c, theirs.Groups, GroupName), "cell", keep, conflicts);
        var exclusions = Items(@base.Exclusions, theirs.Exclusions, mine.Exclusions, e => PairKey("exclusion", e.SetA, e.SetB),
            e => $"исключение {SetLabel(e.SetA)} × {SetLabel(e.SetB)}", "exclusion", keep, conflicts);
        var groupExclusions = Items(@base.GroupExclusions, theirs.GroupExclusions, mine.GroupExclusions,
            e => $"groupExclusion:{e.SetId:N}|{e.GroupId:N}", e => $"исключение {SetLabel(e.SetId)} × вся группа «{GroupName(e.GroupId)}»", "exclusion",
            keep, conflicts);

        var flag = theirs.IgnoreConnectedMep;
        if (mine.IgnoreConnectedMep != @base.IgnoreConnectedMep && mine.IgnoreConnectedMep != theirs.IgnoreConnectedMep)
        {
            if (theirs.IgnoreConnectedMep == @base.IgnoreConnectedMep || keep.Contains(FlagKey)) flag = mine.IgnoreConnectedMep;
            else conflicts.Add(new MergeConflict(FlagKey, "«Не проверять соединённые MEP»", "flag"));
        }

        var ids = sets.Select(s => s.Id).ToHashSet();
        exclusions = exclusions.Where(e => ids.Contains(e.SetA) && ids.Contains(e.SetB)).ToList();
        var groupIds = groups.Select(g => g.Id).ToHashSet();
        groupExclusions = groupExclusions.Where(e => ids.Contains(e.SetId) && groupIds.Contains(e.GroupId)).ToList();
        return new MergeResult(new ClashConfiguration(groups, sets, cells, exclusions, flag) { GroupExclusions = groupExclusions }, conflicts);
    }

    // Группы в подписи — в порядке матрицы, как их видит пользователь.
    private static string CellLabel(MatrixCell cell, IReadOnlyList<SetGroup> groups, Func<Guid, string> name)
    {
        int Index(Guid id)
        {
            for (var i = 0; i < groups.Count; i++)
                if (groups[i].Id == id) return i;
            return int.MaxValue;
        }

        var (a, b) = Index(cell.GroupA) <= Index(cell.GroupB) ? (cell.GroupA, cell.GroupB) : (cell.GroupB, cell.GroupA);
        return $"клетка матрицы «{name(a)}» × «{name(b)}»";
    }

    private static IReadOnlyList<MatrixCell> Ordered(IReadOnlyList<MatrixCell> cells) =>
        cells.Select(c => string.CompareOrdinal(c.GroupA.ToString("N"), c.GroupB.ToString("N")) <= 0 ? c : c with { GroupA = c.GroupB, GroupB = c.GroupA })
            .ToList();

    private static string PairKey(string kind, Guid a, Guid b)
    {
        var (x, y) = (a.ToString("N"), b.ToString("N"));
        return string.CompareOrdinal(x, y) <= 0 ? $"{kind}:{x}|{y}" : $"{kind}:{y}|{x}";
    }

    private static List<T> Items<T>(
        IReadOnlyList<T> @base,
        IReadOnlyList<T> theirs,
        IReadOnlyList<T> mine,
        Func<T, string> key,
        Func<T, string> label,
        string kind,
        HashSet<string> keep,
        List<MergeConflict> conflicts) where T : class
    {
        var b = ByKey(@base, key);
        var t = ByKey(theirs, key);
        var m = ByKey(mine, key);

        // Порядок: как у сохранённого черновика, затем новые элементы из правок в их порядке.
        var order = theirs.Select(key).Concat(mine.Select(key)).Distinct().ToList();
        var result = new List<T>();
        foreach (var k in order.Concat(b.Keys.Where(k => !order.Contains(k))))
        {
            b.TryGetValue(k, out var bi);
            t.TryGetValue(k, out var ti);
            m.TryGetValue(k, out var mi);
            T? chosen;
            if (Same(bi, mi)) chosen = ti;
            else if (Same(bi, ti) || Same(ti, mi) || keep.Contains(k)) chosen = mi;
            else
            {
                chosen = ti;
                conflicts.Add(new MergeConflict(k, label((ti ?? mi ?? bi)!), kind));
            }

            if (chosen is not null) result.Add(chosen);
        }

        return result;
    }

    private static Dictionary<string, T> ByKey<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items) result[key(item)] = item;
        return result;
    }

    // Записи со списками сравниваются по содержимому — через их JSON.
    private static bool Same<T>(T? x, T? y) where T : class =>
        x is null || y is null ? x is null && y is null : ConfigurationJson.Canonical(x) == ConfigurationJson.Canonical(y);
}
