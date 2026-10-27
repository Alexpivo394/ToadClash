namespace Alexpivo394.Revit.Clash.Engine.Identity;

/// <summary>Предложение сопоставить два UniqueId. Статусы и история автоматически не переносятся.</summary>
/// <param name="OldId">UniqueId в старой геометрии.</param>
/// <param name="NewId">UniqueId в новой геометрии.</param>
public sealed record IdentityMatch(string OldId, string NewId);

/// <summary>Сопоставление элементов с геометрией одной модели; Inventory не участвует.</summary>
/// <param name="Kept">Количество элементов с сохранённым UniqueId.</param>
/// <param name="Removed">Количество старых элементов без пары после fallback, не доказательство удаления из Revit.</param>
/// <param name="Added">Количество новых элементов без пары после fallback, не доказательство создания в Revit.</param>
/// <param name="FallbackMatches">Предложения сопоставления в порядке расстояния, затем OldId и NewId ordinal.</param>
public sealed record IdentityReport(int Kept, int Removed, int Added, IReadOnlyList<IdentityMatch> FallbackMatches);
