namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Группа условий, объединённых по И.</summary>
public sealed record ConditionGroup(IReadOnlyList<Condition> All);

/// <summary>Набор выбора раздела: элемент входит в набор, если удовлетворяет хотя бы одной группе (ИЛИ).</summary>
public sealed record SelectionSet(Guid Id, string Discipline, string Name, IReadOnlyList<ConditionGroup> AnyOf);
