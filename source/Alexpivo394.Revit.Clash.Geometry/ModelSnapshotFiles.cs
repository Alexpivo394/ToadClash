namespace Alexpivo394.Revit.Clash.Geometry;

/// <summary>Публикация геометрии и параметров одним новым каталогом слепка.</summary>
public static class ModelSnapshotFiles
{
    /// <summary>
    /// Записывает model.tcg и model.tcp во временный соседний каталог и переименовывает его после закрытия обоих файлов.
    /// Целевой каталог не должен существовать. Каталоги с суффиксом .pending не являются готовыми слепками.
    /// </summary>
    public static void Write(string directory, ModelGeometry geometry, ModelParameters parameters)
    {
        if (geometry is null) throw new ArgumentNullException(nameof(geometry));
        if (parameters is null) throw new ArgumentNullException(nameof(parameters));
        if (!geometry.Header.Complete) throw new ArgumentException("Выгрузка модели не завершена.", nameof(geometry));

        Publish(directory, staging =>
        {
            using (var stream = new FileStream(Path.Combine(staging, "model.tcg"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ModelGeometryFile.Write(stream, geometry);
                stream.Flush(true);
            }
            using (var stream = new FileStream(Path.Combine(staging, "model.tcp"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ModelParametersFile.Write(stream, parameters);
                stream.Flush(true);
            }
        });
    }

    internal static void Publish(string directory, Action<string> writeFiles)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Не задан каталог слепка.", nameof(directory));
        var target = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target.EndsWith(".pending", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Суффикс .pending зарезервирован для незавершённых слепков.", nameof(directory));
        var parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent)) throw new ArgumentException("Корень диска не может быть каталогом слепка.", nameof(directory));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"Каталог слепка уже существует: {target}");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(target) + "-" + Guid.NewGuid().ToString("N") + ".pending");
        Directory.CreateDirectory(staging);
        try
        {
            writeFiles(staging);
            Directory.Move(staging, target);
        }
        finally
        {
            // Каталог создан только этим вызовом; финальные слепки не затрагиваются.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
