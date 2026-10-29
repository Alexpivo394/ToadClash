using System.Diagnostics;
using System.Globalization;
using System.IO;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Revit;
using Alexpivo394.Revit.Clash.Rules;
using RevitApplication = Autodesk.Revit.ApplicationServices.Application;

namespace ToadClash.DevAddin;

/// <summary>Создание новых синтетических документов Revit 2022 в контексте API.</summary>
public static class SyntheticGenerationRun
{
    /// <summary>
    /// Создаёт, сохраняет и закрывает только собственные документы; возвращает новый каталог запуска.
    /// При ошибке остаётся журнал и уже завершённые модели, маркер COMPLETE не создаётся.
    /// </summary>
    /// <param name="application">Приложение Revit 2022; вызов допустим только на потоке API.</param>
    /// <param name="options">Разделы, этажи и ячейки.</param>
    /// <param name="extract">Выгрузка отдельного слепка каждой модели после сохранения.</param>
    /// <param name="cancellationToken">Отмена между ячейками и перед сохранением.</param>
    /// <returns>Уникальная подпапка в ToadClashData/synthetic/revit.</returns>
    public static string Run(RevitApplication application, SyntheticOptions options, bool extract = false,
        CancellationToken cancellationToken = default)
    {
        if (application is null) throw new ArgumentNullException(nameof(application));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (application.VersionNumber != "2022") throw new NotSupportedException("Генератор рассчитан на Revit 2022.");
        cancellationToken.ThrowIfCancellationRequested();
        var templates = SyntheticTemplates.Find(options);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ToadClashData", "synthetic", "revit");
        var directory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Каталог запуска уже существует.");
        Directory.CreateDirectory(directory);
        using var journal = new StreamWriter(new FileStream(Path.Combine(directory, "generation.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        journal.WriteLine($"START {DateTime.UtcNow:O}; section={options.Section}; floors={options.Floors}; cells={options.CellsPerFloor}; minimum={options.MinimumElementCount}; extract={extract}");
        journal.WriteLine($"Templates: {templates}");
        try
        {
            var total = 0;
            foreach (var section in options.Sections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += Generate(application, options, templates, directory, section, extract, journal, cancellationToken);
            }
            if (total < options.MinimumElementCount) throw new InvalidOperationException("Число созданных основных элементов меньше расчётного.");
            var sets = options.Sections.Select(section => new DisciplineSet(Guid.NewGuid(), section, section,
                new[] { new ConditionGroup(Array.Empty<Condition>()) })).ToArray();
            var matrix = options.Sections.SelectMany((section, index) => options.Sections.Skip(index)
                .Select(other => new DisciplineCell(section, other, true))).ToArray();
            File.WriteAllText(Path.Combine(directory, "config.json"),
                ConfigurationJson.Serialize(ConfigurationDefaults.FromDisciplines(sets, matrix, Array.Empty<SetPairExclusion>())));
            using var complete = new StreamWriter(new FileStream(Path.Combine(directory, "COMPLETE.txt"), FileMode.CreateNew));
            complete.WriteLine($"primaryElements={total}; requestedMinimum={options.MinimumElementCount}; extracted={extract}");
            journal.WriteLine($"COMPLETE primaryElements={total}");
            return directory;
        }
        catch (Exception exception)
        {
            journal.WriteLine($"FAILED {exception}");
            throw new InvalidOperationException($"Генерация не завершена. Журнал: {directory}. {exception.Message}", exception);
        }
    }

    private static int Generate(RevitApplication application, SyntheticOptions options, SyntheticTemplates templates,
        string directory, string section, bool extract, TextWriter journal, CancellationToken cancellationToken)
    {
        var modelKey = section + "-synthetic";
        var clock = Stopwatch.StartNew();
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var document = application.NewProjectDocument(section is "АР" or "КР" ? templates.Building : templates.Systems);
        try
        {
            var familyDirectory = Path.Combine(directory, modelKey + "-families");
            SyntheticBuilding? building = null;
            SyntheticMep? mep = null;
            if (section is "АР" or "КР")
            {
                Directory.CreateDirectory(familyDirectory);
                building = new SyntheticBuilding(document, templates, familyDirectory);
            }
            else mep = new SyntheticMep(document, section);

            var levels = new List<Level>();
            View3D view;
            using (var transaction = new Transaction(document, "Подготовка синтетической модели"))
            {
                transaction.Start();
                for (var floor = 0; floor < options.Floors; floor++)
                {
                    var level = Level.Create(document, floor * 4000 / 304.8);
                    level.Name = $"TC-{floor + 1:000}";
                    levels.Add(level);
                }
                var viewType = document.CollectElements().OfClass<ViewFamilyType>().Cast<ViewFamilyType>().First(type => type.ViewFamily == ViewFamily.ThreeDimensional);
                view = View3D.CreateIsometric(document, viewType.Id);
                view.Name = "ToadClash Synthetic";
                view.DetailLevel = ViewDetailLevel.Fine;
                view.Discipline = ViewDiscipline.Coordination;
                view.IsSectionBoxActive = false;
                building?.Activate();
                mep?.Prepare(levels[0]);
                Commit(transaction);
            }

            foreach (var floorCells in SyntheticLayout.Cells(options).GroupBy(cell => cell.Floor))
            {
                using var transaction = new Transaction(document, $"Синтетика: этаж {floorCells.Key + 1}");
                transaction.Start();
                foreach (var cell in floorCells)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    building?.Cell(cell, levels[cell.Floor], section == "КР");
                    mep?.Cell(cell, levels[cell.Floor]);
                }
                building?.Details(levels[floorCells.Key]);
                Commit(transaction);
                journal.WriteLine($"{modelKey}: floor={floorCells.Key + 1}; elapsed={clock.Elapsed.TotalSeconds:F1}s");
            }

            var categories = section switch
            {
                "АР" or "КР" => new[] { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralColumns },
                "ВК" => new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting },
                "ОВ" => new[] { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting },
                _ => new[] { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting }
            };
            var count = document.CollectElements().OfCategories(categories).WhereElementIsNotElementType().GetElementCount();
            if (count < options.Floors * options.CellsPerFloor * 3) throw new InvalidOperationException($"{modelKey}: создано недостаточно элементов ({count}).");
            cancellationToken.ThrowIfCancellationRequested();
            using (var save = new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = 1 })
                document.SaveAs(Path.Combine(directory, modelKey + ".rvt"), save);
            journal.WriteLine($"{modelKey}: saved; primary={count}; warnings={document.GetWarnings().Count}; wall={clock.Elapsed.TotalSeconds:F1}s; cpu={(Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds:F1}s");
            if (extract)
            {
                var extractionClock = Stopwatch.StartNew();
                var (geometry, parameters) = GeometryExtractor.Extract(document, new ExtractOptions(modelKey, view.Name), cancellationToken);
                var snapshot = Path.Combine(directory, modelKey + ".toadclash-" + Guid.NewGuid().ToString("N"));
                ModelSnapshotFiles.Write(snapshot, geometry, parameters);
                using var mapping = new StreamWriter(new FileStream(Path.Combine(snapshot, "model-disciplines.json"), FileMode.CreateNew));
                mapping.Write("{\"" + modelKey + "\":\"" + section + "\"}");
                journal.WriteLine($"{modelKey}: snapshot={snapshot}; geometry={geometry.Elements.Count}; extractAndWrite={extractionClock.Elapsed.TotalSeconds:F1}s; tcgBytes={new FileInfo(Path.Combine(snapshot, "model.tcg")).Length}");
            }
            return count;
        }
        finally
        {
            if (!document.Close(false)) throw new InvalidOperationException("Revit не закрыл созданный генератором документ.");
        }
    }

    internal static void Commit(Transaction transaction)
    {
        if (transaction.Commit() != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit не подтвердил транзакцию генератора.");
    }
}
