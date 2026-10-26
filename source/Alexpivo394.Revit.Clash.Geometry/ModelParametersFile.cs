using System.IO.Compression;

namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Двоичный файл параметров элементов: магическое число, затем Deflate-сжатая полезная нагрузка.</summary>
public static class ModelParametersFile
{
    /// <summary>Магическое число файла, "TCP1".</summary>
    public const uint Magic = 0x31504354;

    private const int FormatVersion = 1;
    private const int MaxCount = 1 << 26;

    /// <summary>Записывает параметры в поток.</summary>
    public static void Write(Stream s, ModelParameters p)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));
        if (p is null) throw new ArgumentNullException(nameof(p));

        using (var head = BinaryIo.CreateWriter(s))
            head.Write(Magic);

        using var deflate = new DeflateStream(s, CompressionLevel.Optimal, true);
        using var writer = BinaryIo.CreateWriter(deflate);
        writer.Write(FormatVersion);
        writer.Write(p.ByUniqueId.Count);
        foreach (var pair in p.ByUniqueId)
        {
            writer.Write(pair.Key);
            writer.Write(pair.Value.Count);
            foreach (var value in pair.Value)
            {
                writer.Write(value.Name);
                writer.WriteNullable(value.SharedGuid);
                writer.WriteNullable(value.Text);
                writer.WriteNullable(value.Number);
            }
        }
    }

    /// <summary>Читает параметры из потока.</summary>
    /// <exception cref="InvalidDataException">Неверное магическое число, версия или повреждённые данные.</exception>
    public static ModelParameters Read(Stream s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        try
        {
            using (var head = BinaryIo.CreateReader(s))
            {
                if (head.ReadUInt32() != Magic) throw new InvalidDataException("Это не файл параметров ToadClash.");
            }

            using var deflate = new DeflateStream(s, CompressionMode.Decompress, true);
            using var reader = BinaryIo.CreateReader(deflate);
            if (reader.ReadInt32() != FormatVersion)
                throw new InvalidDataException("Неподдерживаемая версия файла параметров.");

            var count = reader.ReadCount(MaxCount);
            var result = new Dictionary<string, IReadOnlyList<ParameterValue>>(count);
            for (var i = 0; i < count; i++)
            {
                var uniqueId = reader.ReadString();
                var valueCount = reader.ReadCount(MaxCount);
                var values = new List<ParameterValue>(Math.Min(valueCount, 1024));
                for (var j = 0; j < valueCount; j++)
                {
                    var name = reader.ReadString();
                    values.Add(new ParameterValue(name, reader.ReadNullableGuid(), reader.ReadNullableString(), reader.ReadNullableDouble()));
                }

                result[uniqueId] = values;
            }

            return new ModelParameters(result);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidDataException("Файл параметров повреждён.", e);
        }
    }
}
