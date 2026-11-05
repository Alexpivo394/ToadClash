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

    [Test]
    public void Read_EqualValuesOfDifferentElements_ShareOneInstance()
    {
        // Реальные модели: 1,8 млн значений, уникальных из них ~70 тыс. Без общих экземпляров файл 10 МБ занимает
        // ~0,5 ГБ памяти, и сервер с лимитом памяти отклонял слепок как «повреждённый».
        var source = Enumerable.Range(0, 3).ToDictionary(i => $"uid-{i}", i => (IReadOnlyList<ParameterValue>)
            [new ParameterValue("Система", null, "П1", null), new ParameterValue("Марка", null, $"М-{i}", null), new ParameterValue("Марка", null, "М-0", null)]);

        using var ms = new MemoryStream();
        ModelParametersFile.Write(ms, new ModelParameters(source));
        ms.Position = 0;
        var loaded = ModelParametersFile.Read(ms);

        loaded.For("uid-1")[0].ShouldBeSameAs(loaded.For("uid-0")[0]);
        loaded.For("uid-2")[2].ShouldBeSameAs(loaded.For("uid-0")[1]);
        loaded.For("uid-2")[1].Name.ShouldBeSameAs(loaded.For("uid-0")[1].Name);
        loaded.For("uid-1")[1].ShouldBe(new ParameterValue("Марка", null, "М-1", null));
    }
}
