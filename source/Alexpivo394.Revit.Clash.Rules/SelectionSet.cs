namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Группа условий, объединённых по И.</summary>
public sealed record ConditionGroup(IReadOnlyList<Condition> All);

/// <summary>
///     Набор группы: из источника группы отбираются элементы, удовлетворяющие хотя бы одной группе условий (ИЛИ).
/// </summary>
public sealed record SelectionSet(Guid Id, Guid GroupId, string Name, IReadOnlyList<ConditionGroup> AnyOf);
