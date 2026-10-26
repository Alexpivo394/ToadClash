namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Геометрия элемента: описание, сетка, габариты и отпечаток.</summary>
public sealed record ElementGeometry(ElementInfo Info, ElementMesh Mesh, Aabb Bounds, ulong Fingerprint);
