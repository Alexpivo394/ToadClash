using System.Text;

namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Общие примитивы двоичного формата: строки с признаком наличия, массивы, GUID.</summary>
internal static class BinaryIo
{
    internal static readonly Encoding Utf8 = new UTF8Encoding(false);

    internal static BinaryWriter CreateWriter(Stream stream) => new(stream, Utf8, true);

    internal static BinaryReader CreateReader(Stream stream) => new(stream, Utf8, true);

    internal static void WriteNullable(this BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) writer.Write(value);
    }

    internal static string? ReadNullableString(this BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;

    internal static void WriteNullable(this BinaryWriter writer, Guid? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value.ToByteArray());
    }

    internal static Guid? ReadNullableGuid(this BinaryReader reader) =>
        reader.ReadBoolean() ? new Guid(ReadExactly(reader, 16)) : null;

    internal static void WriteNullable(this BinaryWriter writer, double? value)
    {
        writer.Write(value.HasValue);
        if (value.HasValue) writer.Write(value.Value);
    }

    internal static double? ReadNullableDouble(this BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadDouble() : null;

    internal static byte[] ReadExactly(this BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        return bytes;
    }

    /// <summary>Читает неотрицательное число элементов, не превышающее <paramref name="max"/>.</summary>
    internal static int ReadCount(this BinaryReader reader, int max)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > max) throw new InvalidDataException($"Недопустимое число элементов: {count}.");
        return count;
    }

    internal static void WriteStrings(this BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        foreach (var value in values) writer.Write(value);
    }

    internal static string[] ReadStrings(this BinaryReader reader, int max)
    {
        var count = reader.ReadCount(max);
        var values = new string[count];
        for (var i = 0; i < count; i++) values[i] = reader.ReadString();
        return values;
    }
}
