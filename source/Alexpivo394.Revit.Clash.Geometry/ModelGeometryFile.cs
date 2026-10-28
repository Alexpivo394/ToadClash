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
    /// <summary>Магическое число файла, "TCG1"; версия формата записана следом.</summary>
    public const uint Magic = 0x31474354;

    /// <summary>
    ///     Версия записи. Прежние версии читаются: в версии 1 нет корневого семейства элемента, в версиях 1 и 2 —
    ///     числа треугольников в таблице.
    /// </summary>
    private const int FormatVersion = 3;
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
            writer.Write(element.Mesh.TriangleCount);
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
            var header = ReadPrefix(reader, out var version);

            var table = ReadTable(reader, version);

            var blockLength = reader.ReadInt64();
            if (blockLength < 0) throw new InvalidDataException("Недопустимая длина блока сеток.");

            var elements = new ElementGeometry[table.Length];
            long position = 0;
            for (var i = 0; i < table.Length; i++)
            {
                var row = table[i];
                if (row.Offset != position || row.Length < 0 || position + row.Length > blockLength)
                    throw new InvalidDataException("Сетка элемента не соответствует блоку сеток.");
                var mesh = DecompressMesh(reader.ReadExactly(row.Length));
                position += row.Length;
                elements[i] = new ElementGeometry(row.Info, mesh, row.Bounds, row.Fingerprint);
            }

            if (position != blockLength) throw new InvalidDataException("Длина блока сеток не совпадает с таблицей.");

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
            return ReadPrefix(reader, out _);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidDataException("Файл геометрии повреждён.", e);
        }
    }

    /// <summary>Читает заголовок, таблицу элементов и опись, пропуская блок сеток без распаковки.</summary>
    /// <param name="input">Поток с позиционированием: блок сеток пропускается через <see cref="Stream.Seek" />.</param>
    /// <exception cref="ArgumentException">Поток не поддерживает позиционирование.</exception>
    /// <exception cref="InvalidDataException">Неверное магическое число, версия или повреждённые данные.</exception>
    public static ModelIndex ReadIndex(Stream input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (!input.CanSeek) throw new ArgumentException("Оглавление читается только из потока с позиционированием.", nameof(input));

        try
        {
            using var reader = BinaryIo.CreateReader(input);
            var header = ReadPrefix(reader, out var version);
            var table = ReadTable(reader, version);

            var blockLength = reader.ReadInt64();
            if (blockLength < 0 || blockLength > input.Length - input.Position)
                throw new InvalidDataException("Недопустимая длина блока сеток.");
            long expected = 0;
            foreach (var row in table)
            {
                if (row.Offset != expected || row.Length < 0) throw new InvalidDataException("Сетка элемента не соответствует блоку сеток.");
                expected += row.Length;
            }

            if (expected != blockLength) throw new InvalidDataException("Длина блока сеток не совпадает с таблицей.");
            var blockStart = input.Position;
            input.Seek(blockLength, SeekOrigin.Current);

            var inventory = reader.ReadStrings(MaxElements);
            var entries = table
                .Select(row => new ModelIndexEntry(row.Info, row.Bounds, row.Fingerprint, row.TriangleCount, blockStart + row.Offset, row.Length))
                .ToArray();
            return new ModelIndex(header, entries, inventory);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidDataException("Файл геометрии повреждён.", e);
        }
    }

    /// <summary>Читает и распаковывает одну сетку по записи оглавления того же файла.</summary>
    /// <param name="input">Поток файла с позиционированием; позиция после чтения не определена.</param>
    /// <exception cref="ArgumentException">Поток не поддерживает позиционирование.</exception>
    /// <exception cref="InvalidDataException">Смещение или длина вне файла, повреждённые данные сетки.</exception>
    public static ElementMesh ReadMesh(Stream input, ModelIndexEntry entry)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (!input.CanSeek) throw new ArgumentException("Сетка читается только из потока с позиционированием.", nameof(input));
        if (entry.MeshOffset < 0 || entry.MeshLength < 0 || entry.MeshOffset + entry.MeshLength > input.Length)
            throw new InvalidDataException("Сетка элемента вне файла геометрии.");

        try
        {
            input.Seek(entry.MeshOffset, SeekOrigin.Begin);
            using var reader = BinaryIo.CreateReader(input);
            return DecompressMesh(reader.ReadExactly(entry.MeshLength));
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or OverflowException or FormatException)
        {
            throw new InvalidDataException("Файл геометрии повреждён.", e);
        }
    }

    private static ModelHeader ReadPrefix(BinaryReader reader, out int version)
    {
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Это не файл геометрии ToadClash.");
        version = reader.ReadInt32();
        if (version is < 1 or > FormatVersion)
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

    private static TableRow[] ReadTable(BinaryReader reader, int version)
    {
        var count = reader.ReadCount(MaxElements);
        var table = new TableRow[count];
        for (var i = 0; i < count; i++)
        {
            var info = ReadInfo(reader, version);
            var min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var fingerprint = reader.ReadUInt64();
            var offset = reader.ReadInt64();
            var length = reader.ReadInt32();
            var triangles = version >= 3 ? reader.ReadInt32() : -1;
            table[i] = new TableRow(info, new Aabb(min, max), fingerprint, offset, length, triangles);
        }

        return table;
    }

    private readonly record struct TableRow(ElementInfo Info, Aabb Bounds, ulong Fingerprint, long Offset, int Length, int TriangleCount);

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
        writer.WriteNullable(info.RootComponentUniqueId);
    }

    private static ElementInfo ReadInfo(BinaryReader reader, int version) => new(
        reader.ReadString(),
        reader.ReadInt64(),
        reader.ReadString(),
        reader.ReadNullableString(),
        reader.ReadNullableString(),
        reader.ReadNullableString(),
        reader.ReadNullableString(),
        reader.ReadStrings(MaxElements),
        version >= 2 ? reader.ReadNullableString() : null);

    private static byte[] CompressMesh(ElementMesh mesh)
    {
        if (mesh.Positions.Length > MaxMeshValues || mesh.Indices.Length > MaxMeshValues)
            throw new ArgumentException("Сетка превышает допустимый размер формата.", nameof(mesh));

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

    private static ElementMesh DecompressMesh(byte[] compressed)
    {
        using var source = new MemoryStream(compressed, false);
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
