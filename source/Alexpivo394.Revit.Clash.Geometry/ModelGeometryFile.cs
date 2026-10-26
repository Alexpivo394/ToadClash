using System.IO.Compression;
using System.Numerics;

namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>
///     Двоичный файл геометрии модели (порядок байт little-endian). Раскладка: заголовок, таблица элементов
///     (описание, габариты, отпечаток, смещение и длина сжатой сетки), длина блока сеток, блок сеток, опись.
///     Каждая сетка сжимается Deflate отдельно, поэтому заголовок и таблица читаются без распаковки геометрии.
/// </summary>
public static class ModelGeometryFile
{
    /// <summary>Магическое число файла, "TCG1".</summary>
    public const uint Magic = 0x31474354;

    private const int FormatVersion = 1;
    private const int MaxElements = 1 << 26;
    private const int MaxMeshValues = 1 << 26;

    /// <summary>Записывает модель в поток. Выходной поток может не поддерживать позиционирование.</summary>
    public static void Write(Stream output, ModelGeometry model)
    {
        if (output is null) throw new ArgumentNullException(nameof(output));
        if (model is null) throw new ArgumentNullException(nameof(model));

        var elements = model.Elements;
        var meshes = new byte[elements.Count][];
        var offsets = new long[elements.Count];
        long blockLength = 0;
        for (var i = 0; i < elements.Count; i++)
        {
            meshes[i] = CompressMesh(elements[i].Mesh);
            offsets[i] = blockLength;
            blockLength += meshes[i].Length;
        }

        using var writer = BinaryIo.CreateWriter(output);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        WriteHeader(writer, model.Header);

        writer.Write(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            WriteInfo(writer, element.Info);
            writer.Write(element.Bounds.Min.X);
            writer.Write(element.Bounds.Min.Y);
            writer.Write(element.Bounds.Min.Z);
            writer.Write(element.Bounds.Max.X);
            writer.Write(element.Bounds.Max.Y);
            writer.Write(element.Bounds.Max.Z);
            writer.Write(element.Fingerprint);
            writer.Write(offsets[i]);
            writer.Write(meshes[i].Length);
        }

        writer.Write(blockLength);
        foreach (var mesh in meshes) writer.Write(mesh);

        writer.WriteStrings(model.Inventory);
        writer.Flush();
    }

    /// <summary>Читает модель целиком.</summary>
    /// <exception cref="InvalidDataException">Неверное магическое число, версия или повреждённые данные.</exception>
    public static ModelGeometry Read(Stream input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        try
        {
            using var reader = BinaryIo.CreateReader(input);
            var header = ReadPrefix(reader);

            var count = reader.ReadCount(MaxElements);
            var infos = new ElementInfo[count];
            var bounds = new Aabb[count];
            var fingerprints = new ulong[count];
            var offsets = new long[count];
            var lengths = new int[count];
            for (var i = 0; i < count; i++)
            {
                infos[i] = ReadInfo(reader);
                var min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                bounds[i] = new Aabb(min, max);
                fingerprints[i] = reader.ReadUInt64();
                offsets[i] = reader.ReadInt64();
                lengths[i] = reader.ReadInt32();
            }

            var blockLength = reader.ReadInt64();
            if (blockLength < 0 || blockLength > int.MaxValue)
                throw new InvalidDataException("Недопустимая длина блока сеток.");
            var block = reader.ReadExactly((int)blockLength);

            var elements = new ElementGeometry[count];
            for (var i = 0; i < count; i++)
            {
                if (offsets[i] < 0 || lengths[i] < 0 || offsets[i] + lengths[i] > block.Length)
                    throw new InvalidDataException("Сетка элемента выходит за границы блока сеток.");
                var mesh = DecompressMesh(block, (int)offsets[i], lengths[i]);
                elements[i] = new ElementGeometry(infos[i], mesh, bounds[i], fingerprints[i]);
            }

            var inventory = reader.ReadStrings(MaxElements);
            return new ModelGeometry(header, elements, inventory);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidDataException("Файл геометрии повреждён.", e);
        }
    }

    /// <summary>Читает только заголовок, не обращаясь к таблице элементов и сеткам.</summary>
    /// <exception cref="InvalidDataException">Неверное магическое число, версия или повреждённые данные.</exception>
    public static ModelHeader ReadHeader(Stream input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));

        try
        {
            using var reader = BinaryIo.CreateReader(input);
            return ReadPrefix(reader);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidDataException("Файл геометрии повреждён.", e);
        }
    }

    /// <summary>Смещение начала блока сеток от начала файла: всё до него читается без распаковки.</summary>
    internal static long ReadMeshBlockOffset(Stream input)
    {
        using var reader = BinaryIo.CreateReader(input);
        ReadPrefix(reader);
        var count = reader.ReadCount(MaxElements);
        for (var i = 0; i < count; i++)
        {
            ReadInfo(reader);
            reader.ReadExactly(6 * sizeof(float) + sizeof(ulong) + sizeof(long) + sizeof(int));
        }

        reader.ReadInt64();
        return input.Position;
    }

    private static ModelHeader ReadPrefix(BinaryReader reader)
    {
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Это не файл геометрии ToadClash.");
        var version = reader.ReadInt32();
        if (version != FormatVersion)
            throw new InvalidDataException($"Неподдерживаемая версия файла геометрии: {version}.");

        var modelKey = reader.ReadString();
        var sourcePath = reader.ReadString();
        var revitYear = reader.ReadInt32();
        var utcTicks = reader.ReadInt64();
        var offsetMinutes = reader.ReadInt32();
        var complete = reader.ReadBoolean();
        var viewName = reader.ReadString();
        var originX = reader.ReadDouble();
        var originY = reader.ReadDouble();
        var originZ = reader.ReadDouble();
        var extractedAt = new DateTimeOffset(new DateTime(utcTicks, DateTimeKind.Utc))
            .ToOffset(TimeSpan.FromMinutes(offsetMinutes));
        return new ModelHeader(modelKey, sourcePath, revitYear, extractedAt, complete, viewName, originX, originY, originZ);
    }

    private static void WriteHeader(BinaryWriter writer, ModelHeader header)
    {
        writer.Write(header.ModelKey);
        writer.Write(header.SourcePath);
        writer.Write(header.RevitYear);
        writer.Write(header.ExtractedAt.UtcTicks);
        writer.Write((int)header.ExtractedAt.Offset.TotalMinutes);
        writer.Write(header.Complete);
        writer.Write(header.ViewName);
        writer.Write(header.OriginX);
        writer.Write(header.OriginY);
        writer.Write(header.OriginZ);
    }

    private static void WriteInfo(BinaryWriter writer, ElementInfo info)
    {
        writer.Write(info.UniqueId);
        writer.Write(info.ElementId);
        writer.Write(info.Category);
        writer.WriteNullable(info.Family);
        writer.WriteNullable(info.Type);
        writer.WriteNullable(info.Level);
        writer.WriteNullable(info.Workset);
        writer.WriteStrings(info.ConnectedUniqueIds);
    }

    private static ElementInfo ReadInfo(BinaryReader reader) => new(
        reader.ReadString(),
        reader.ReadInt64(),
        reader.ReadString(),
        reader.ReadNullableString(),
        reader.ReadNullableString(),
        reader.ReadNullableString(),
        reader.ReadNullableString(),
        reader.ReadStrings(MaxElements));

    private static byte[] CompressMesh(ElementMesh mesh)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Fastest, true))
        using (var writer = BinaryIo.CreateWriter(deflate))
        {
            writer.Write(mesh.Positions.Length / 3);
            WriteBlock(writer, mesh.Positions, sizeof(float));
            writer.Write(mesh.Indices.Length);
            WriteBlock(writer, mesh.Indices, sizeof(int));
        }

        return buffer.ToArray();
    }

    private static ElementMesh DecompressMesh(byte[] block, int offset, int length)
    {
        using var source = new MemoryStream(block, offset, length, false);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var reader = BinaryIo.CreateReader(deflate);

        var vertexCount = reader.ReadCount(MaxMeshValues / 3);
        var positions = new float[vertexCount * 3];
        ReadBlock(reader, positions, sizeof(float));
        var indexCount = reader.ReadCount(MaxMeshValues);
        var indices = new int[indexCount];
        ReadBlock(reader, indices, sizeof(int));
        return new ElementMesh(positions, indices);
    }

    private static void WriteBlock(BinaryWriter writer, Array values, int elementSize)
    {
        var bytes = new byte[values.Length * elementSize];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        writer.Write(bytes);
    }

    private static void ReadBlock(BinaryReader reader, Array values, int elementSize)
    {
        var bytes = reader.ReadExactly(values.Length * elementSize);
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
    }
}
