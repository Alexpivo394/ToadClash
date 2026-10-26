using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Rules;

/// <summary>Модель раздела на входе проверки.</summary>
public sealed record ModelInput(string Discipline, ModelGeometry Geometry, ModelParameters Parameters);
