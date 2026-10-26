namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>Физические соединения MEP-элемента через коннекторы.</summary>
internal static class ConnectivityReader
{
    private static readonly IReadOnlyList<string> NoConnections = [];

    /// <summary>
    ///     UniqueId элементов, присоединённых к физическим коннекторам элемента, по порядку ordinal.
    ///     Логические связи и системы MEP не учитываются.
    /// </summary>
    public static IReadOnlyList<string> ReadConnectedUniqueIds(Element element)
    {
        var manager = element switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            FabricationPart part => part.ConnectorManager,
            _ => null
        };
        if (manager is null) return NoConnections;

        var connected = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Connector connector in manager.Connectors)
        {
            if (!IsPhysical(connector) || !connector.IsConnected) continue;

            foreach (Connector other in connector.AllRefs)
            {
                if (!IsPhysical(other)) continue;
                var owner = other.Owner;
                if (owner is null || owner is MEPSystem || owner.Id == element.Id) continue;
                connected.Add(owner.UniqueId);
            }
        }

        return connected.Count == 0 ? NoConnections : connected.ToArray();
    }

    private static bool IsPhysical(Connector connector) => (connector.ConnectorType & ConnectorType.Physical) != 0;
}
