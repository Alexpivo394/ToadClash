using System.Security.Cryptography;
using System.Text;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Набор прежнего формата — привязан к разделу, а не к группе.</summary>
public sealed record DisciplineSet(Guid Id, string Discipline, string Name, IReadOnlyList<ConditionGroup> AnyOf);

/// <summary>Клетка матрицы прежнего формата — пара разделов.</summary>
public sealed record DisciplineCell(string DisciplineA, string DisciplineB, bool Enabled, double ToleranceMm = 20);

/// <summary>
///     Группы разделов и стартовые конфигурации. Id группы раздела выводится из имени раздела, поэтому один и тот же раздел
///     даёт одну и ту же группу в черновиках, шаблонах и снимках прогонов, записанных до появления групп.
/// </summary>
public static class ConfigurationDefaults
{
    /// <summary>Id группы раздела: SHA-256 от имени раздела в верхнем регистре, Guid версии 8.</summary>
    public static Guid DisciplineGroupId(string discipline)
    {
        if (discipline is null) throw new ArgumentNullException(nameof(discipline));
        byte[] hash;
        using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes("discipline:" + discipline.Trim().ToUpperInvariant()));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>Группа раздела: имя — раздел, источник — модели раздела.</summary>
    public static SetGroup DisciplineGroup(string discipline) => new(DisciplineGroupId(discipline), discipline, GroupSource.OfDiscipline(discipline));

    /// <summary>Набор «Всё»: элементы с категорией.</summary>
    public static ConditionGroup Everything { get; } =
        new([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null)]);

    /// <summary>Группа на раздел с набором «Всё», все пары групп (и группа сама с собой) включены с допуском 20 мм.</summary>
    public static ClashConfiguration ByDisciplines(IReadOnlyList<string> disciplines)
    {
        if (disciplines is null) throw new ArgumentNullException(nameof(disciplines));
        var groups = disciplines.Distinct(StringComparer.OrdinalIgnoreCase).Select(DisciplineGroup).ToList();
        var sets = groups.Select(g => new SelectionSet(Guid.NewGuid(), g.Id, "Всё", [Everything])).ToList();
        var matrix = new List<MatrixCell>();
        for (var i = 0; i < groups.Count; i++)
        for (var j = i; j < groups.Count; j++)
            matrix.Add(new MatrixCell(groups[i].Id, groups[j].Id, true));
        return new ClashConfiguration(groups, sets, matrix, []);
    }

    /// <summary>
    ///     Конфигурация из наборов и клеток по разделам (прежний формат): группа раздела на каждый встреченный раздел, в
    ///     порядке первого появления; имя группы — как раздел записан первым.
    /// </summary>
    public static ClashConfiguration FromDisciplines(
        IReadOnlyList<DisciplineSet> sets,
        IReadOnlyList<DisciplineCell> matrix,
        IReadOnlyList<SetPairExclusion> exclusions,
        bool ignoreConnectedMep = true)
    {
        if (sets is null) throw new ArgumentNullException(nameof(sets));
        if (matrix is null) throw new ArgumentNullException(nameof(matrix));
        if (exclusions is null) throw new ArgumentNullException(nameof(exclusions));

        var groups = new List<SetGroup>();
        var seen = new HashSet<Guid>();
        Guid Group(string discipline)
        {
            var group = DisciplineGroup(discipline);
            if (seen.Add(group.Id)) groups.Add(group);
            return group.Id;
        }

        var converted = sets.Select(s => new SelectionSet(s.Id, Group(s.Discipline), s.Name, s.AnyOf)).ToList();
        var cells = matrix.Select(c => new MatrixCell(Group(c.DisciplineA), Group(c.DisciplineB), c.Enabled, c.ToleranceMm)).ToList();
        return new ClashConfiguration(groups, converted, cells, exclusions, ignoreConnectedMep);
    }
}
