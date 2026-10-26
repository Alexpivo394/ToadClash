using Shouldly;
using Alexpivo394.Revit.Clash.Geometry;

namespace Alexpivo394.Revit.Clash.Tests.Geometry;

public sealed class ModelParametersFileTests
{
    [Test]
    public void Parameters_RoundTrip_WithNullsAndGuid()
    {
        var guid = Guid.NewGuid();
        var source = new Dictionary<string, IReadOnlyList<ParameterValue>>
        {
            ["uid-1"] = [new ParameterValue("Марка", null, "А-1", null), new ParameterValue("Length", guid, null, 1234.5)],
            ["uid-2"] = [new ParameterValue("Empty", null, null, null)],
            ["uid-3"] = [],
        };

        using var ms = new MemoryStream();
        ModelParametersFile.Write(ms, new ModelParameters(source));
        ms.Position = 0;
        var loaded = ModelParametersFile.Read(ms);

        loaded.ByUniqueId.Count.ShouldBe(3);
        loaded.For("uid-1").ShouldBe(source["uid-1"]);
        loaded.For("uid-1")[1].SharedGuid.ShouldBe(guid);
        loaded.For("uid-2").Single().ShouldBe(new ParameterValue("Empty", null, null, null));
        loaded.For("uid-3").ShouldBeEmpty();
        loaded.For("unknown").ShouldBeEmpty();
    }

    [Test]
    public void Parameters_WrongMagic_Throws()
    {
        using var ms = new MemoryStream();
        ModelParametersFile.Write(ms, new ModelParameters(new Dictionary<string, IReadOnlyList<ParameterValue>>()));
        var bytes = ms.ToArray();
        bytes[0] ^= 0xFF;

        Should.Throw<InvalidDataException>(() => ModelParametersFile.Read(new MemoryStream(bytes)));
    }
}
