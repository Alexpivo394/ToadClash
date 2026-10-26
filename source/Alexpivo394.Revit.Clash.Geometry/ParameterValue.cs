namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>
///     Значение параметра элемента. <paramref name="Number"/> - для длин в мм, для прочих чисел в единицах Revit;
///     <paramref name="Text"/> - отображаемое значение.
/// </summary>
public sealed record ParameterValue(string Name, Guid? SharedGuid, string? Text, double? Number);
