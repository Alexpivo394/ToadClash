using Alexpivo394.Revit.Clash.Geometry;
using ParameterValue = Alexpivo394.Revit.Clash.Geometry.ParameterValue;

namespace Alexpivo394.Revit.Clash.Revit;

/// <summary>Выгрузка геометрии и параметров модели Revit для проверки на коллизии.</summary>
public static class GeometryExtractor
{
    /// <summary>Наибольшее отклонение триангуляции тела целиком от тела, мм: заметно меньше допуска 20 мм.</summary>
    private const double ShellAccuracyMm = 3;

    private const double FeetToMillimeters = 304.8;

    /// <summary>Дальше этого расстояния от начала выгрузки, мм, шаг float больше 0,06 мм.</summary>
    private const float FarFromOriginMm = 1_000_000;

    private const string GeometryStage = "геометрия";
    private const string ParametersStage = "параметры";

    private const int ProgressEvery = 500;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Видимые на виде элементы с геометрией + опись всех элементов модели (без видов, типов, элементов без категории).
    ///     Учитываются только элементы модельных встроенных категорий; группы, сборки и связанные модели не выгружаются.
    ///     Координаты - мм в общей системе документа относительно начала выгрузки: внутреннего начала документа в общих
    ///     координатах, округлённого до метра. Начало не зависит от элементов модели.
    /// </summary>
    /// <param name="doc">Документ; не изменяется.</param>
    /// <param name="options">Ключ модели, имя 3D-вида и детализация триангуляции.</param>
    /// <param name="ct">Отмена между элементами.</param>
    /// <exception cref="ArgumentException">В документе нет 3D-вида с именем <see cref="ExtractOptions.ViewName"/>.</exception>
    /// <exception cref="OperationCanceledException">Выгрузка отменена.</exception>
    public static ExtractResult Extract(Document doc, ExtractOptions options, CancellationToken ct) =>
        Extract(doc, options, null, ct);

    /// <summary>То же, что <see cref="Extract(Document, ExtractOptions, CancellationToken)" />, с сообщениями о ходе выгрузки.</summary>
    /// <param name="progress">
    ///     Ход по этапам: о первом и последнем элементе этапа всегда, о промежуточных — каждые 500 элементов, но не чаще
    ///     раза в 2 с. Вызывается в потоке выгрузки. null — без сообщений.
    /// </param>
    public static ExtractResult Extract(Document doc, ExtractOptions options, IProgress<ExtractProgress>? progress,
        CancellationToken ct)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (options is null) throw new ArgumentNullException(nameof(options));

        progress?.Report(new ExtractProgress(ExtractStage.Collecting, 0, 0));
        var view = FindView(doc, options.ViewName);
        var categoryNames = ModelCategoryNames(doc);
        var categories = categoryNames.Keys.ToList();
        var coordinates = CoordinateTransform.FromDocument(doc);
        var visible = CollectVisible(doc, view, categories);

        var origin = coordinates.RoundedOrigin(XYZ.Zero);
        var header = new ModelHeader(
            options.ModelKey,
            doc.PathName,
            int.Parse(doc.Application.VersionNumber),
            DateTimeOffset.UtcNow,
            false,
            view.Name,
            origin.X,
            origin.Y,
            origin.Z);

        var geometryOptions = new Options { View = view, ComputeReferences = false };
        var parameterReader = new ParameterReader();
        var elements = new List<ElementGeometry>(visible.Count);
        var parameters = new Dictionary<string, IReadOnlyList<ParameterValue>>(visible.Count, StringComparer.Ordinal);
        var throttle = new ProgressThrottle(ProgressEvery, ProgressInterval);
        var far = 0;
        var skipped = new List<SkippedElement>();
        var done = 0;
        foreach (var element in visible)
        {
            ct.ThrowIfCancellationRequested();
            Report(ExtractStage.Elements, ++done, visible.Count);

            // Сбой геометрии пропускает элемент целиком (в описи он остаётся), сбой параметров — только параметры.
            ElementMesh mesh = null!;
            ElementInfo info = null!;
            var id = IdValue(element.Id);
            if (!ElementGuard.TryRun(element.UniqueId, id, GeometryStage, () =>
                {
                    mesh = Tessellate(element, geometryOptions, coordinates, origin, options.TriangulationLevelOfDetail);
                    if (mesh.TriangleCount > 0) info = ReadInfo(element, categoryNames);
                }, skipped) || mesh.TriangleCount == 0)
                continue;

            var bounds = Aabb.FromPositions(mesh.Positions);
            if (IsFar(bounds)) far++;
            elements.Add(new ElementGeometry(info, mesh, bounds, Fingerprint.Compute(mesh, header)));
            IReadOnlyList<ParameterValue> values = [];
            ElementGuard.TryRun(element.UniqueId, id, ParametersStage, () => values = parameterReader.Read(element), skipped);
            parameters[info.UniqueId] = values;
        }

        ElementGuard.ThrowIfTooMany(skipped, visible.Count);

        var all = doc.CollectElements().WhereElementIsNotElementType().OfCategories(categories).ToList();
        var inventory = new List<string>(all.Count);
        throttle = new ProgressThrottle(ProgressEvery, ProgressInterval);
        done = 0;
        foreach (var element in all)
        {
            ct.ThrowIfCancellationRequested();
            Report(ExtractStage.Inventory, ++done, all.Count);
            if (!IsContainer(element)) inventory.Add(element.UniqueId);
        }

        var geometry = new ModelGeometry(header with { Complete = true }, elements, inventory);
        return new ExtractResult(geometry, new ModelParameters(parameters), skipped, far);

        void Report(ExtractStage stage, int current, int total)
        {
            if (progress is not null && throttle.ShouldReport(current, total, DateTimeOffset.UtcNow))
                progress.Report(new ExtractProgress(stage, current, total));
        }
    }

    private static View3D FindView(Document doc, string viewName)
    {
        foreach (var element in doc.CollectElements().OfClass(typeof(View3D)))
        {
            if (element is View3D { IsTemplate: false } view && view.Name == viewName) return view;
        }

        throw new ArgumentException($"В документе нет 3D-вида \"{viewName}\".", nameof(viewName));
    }

    /// <summary>
    ///     Встроенные модельные категории документа и их имена BuiltInCategory. Подкатегории включаются: категорией
    ///     элемента бывает и подкатегория (OST_Cornices у профилей стен, OST_EdgeSlab у кромок перекрытий).
    /// </summary>
    private static Dictionary<BuiltInCategory, string> ModelCategoryNames(Document doc)
    {
        var names = new Dictionary<BuiltInCategory, string>();
        foreach (Category category in doc.Settings.Categories)
        {
            if (category.CategoryType != CategoryType.Model) continue;
            AddBuiltIn(category);
            foreach (Category subCategory in category.SubCategories) AddBuiltIn(subCategory);
        }

        return names;

        void AddBuiltIn(Category category)
        {
            var id = IdValue(category.Id);
            if (id >= 0) return;
            // Значение — само перечисление: с Revit 2024 его базовый тип long, до того — int, а IsDefined требует точный тип.
            var builtIn = (BuiltInCategory)id;
            if (!Enum.IsDefined(typeof(BuiltInCategory), builtIn)) return;
            names[builtIn] = builtIn.ToString();
        }
    }

    /// <summary>Видимые на виде экземпляры; вложенные общие семейства добавляются отдельными элементами.</summary>
    private static List<Element> CollectVisible(Document doc, View3D view, ICollection<BuiltInCategory> categories)
    {
        var candidates = doc.CollectElements(view).WhereElementIsNotElementType().OfCategories(categories).ToList();
        var visibleIds = new HashSet<ElementId>(candidates.Select(element => element.Id));
        var seen = new HashSet<ElementId>();
        var result = new List<Element>();
        foreach (var element in candidates)
            Add(element);
        return result;

        void Add(Element element)
        {
            if (!seen.Add(element.Id) || IsContainer(element)) return;
            result.Add(element);
            if (element is not FamilyInstance instance) return;

            foreach (var id in instance.GetSubComponentIds())
            {
                if (visibleIds.Contains(id) && id.ToElement(doc) is Element nested && !nested.IsHidden(view))
                    Add(nested);
            }
        }
    }

    /// <summary>Элементы-контейнеры: их геометрия повторяет геометрию входящих элементов или другой модели.</summary>
    private static bool IsContainer(Element element) => element is View or Group or AssemblyInstance or RevitLinkInstance;

    private static bool IsFar(Aabb box) =>
        Math.Max(Math.Max(Math.Abs(box.Min.X), Math.Abs(box.Max.X)), Math.Max(Math.Max(Math.Abs(box.Min.Y), Math.Abs(box.Max.Y)),
            Math.Max(Math.Abs(box.Min.Z), Math.Abs(box.Max.Z)))) > FarFromOriginMm;

    private static ElementMesh Tessellate(Element element, Options options, CoordinateTransform coordinates, Vector3d origin, double levelOfDetail)
    {
        var builder = new MeshBuilder(origin);
        if (element.get_Geometry(options) is { } geometry)
            AppendGeometry(builder, geometry, Transform.Identity, coordinates, levelOfDetail);
        return builder.Build();
    }

    private static void AppendGeometry(MeshBuilder builder, GeometryElement geometry, Transform local, CoordinateTransform coordinates, double levelOfDetail)
    {
        foreach (var geometryObject in geometry)
        {
            switch (geometryObject)
            {
                case GeometryInstance instance:
                    AppendGeometry(builder, instance.GetSymbolGeometry(), local.Multiply(instance.Transform), coordinates, levelOfDetail);
                    break;
                case Solid solid when solid.Faces.Size > 0:
                    AppendSolid(builder, solid, local, coordinates, levelOfDetail);
                    break;
                case Mesh mesh:
                    AppendMesh(builder, mesh, local, coordinates);
                    break;
            }
        }
    }

    /// <summary>
    ///     Тело триангулируется по граням. Грани триангулируются независимо, и на криволинейных гранях узлы общих рёбер
    ///     могут не совпасть - сетка тела выходит незамкнутой. Тогда тело триангулируется целиком
    ///     (<see cref="SolidUtils.TessellateSolidOrShell"/>, отклонение до <see cref="ShellAccuracyMm"/>); если и это
    ///     не удалось, остаётся триангуляция по граням.
    /// </summary>
    private static void AppendSolid(MeshBuilder builder, Solid solid, Transform local, CoordinateTransform coordinates, double levelOfDetail)
    {
        var mark = builder.Mark();
        foreach (Face face in solid.Faces) AppendMesh(builder, face.Triangulate(levelOfDetail), local, coordinates);
        if (builder.IsClosedSince(mark)) return;

        TriangulatedSolidOrShell shell;
        try
        {
            shell = SolidUtils.TessellateSolidOrShell(solid, new SolidOrShellTessellationControls { Accuracy = ShellAccuracyMm / FeetToMillimeters });
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return;
        }

        var components = Enumerable.Range(0, shell.ShellComponentCount).Select(shell.GetShellComponent).Where(c => c.TriangleCount > 0).ToList();
        if (components.Count == 0) return;

        builder.Rollback(mark);
        foreach (var component in components) AppendShell(builder, component, local, coordinates);
    }

    private static void AppendShell(MeshBuilder builder, TriangulatedShellComponent component, Transform local, CoordinateTransform coordinates)
    {
        var points = new List<XYZ>(component.VertexCount);
        for (var i = 0; i < component.VertexCount; i++) points.Add(component.GetVertex(i));
        var vertices = coordinates.ToSharedMillimeters(points, local);
        var mirrored = local.Determinant < 0;
        for (var i = 0; i < component.TriangleCount; i++)
        {
            var triangle = component.GetTriangle(i);
            var a = vertices[triangle.VertexIndex0];
            var b = vertices[triangle.VertexIndex1];
            var c = vertices[triangle.VertexIndex2];
            if (mirrored) builder.AddTriangle(a, c, b);
            else builder.AddTriangle(a, b, c);
        }
    }

    /// <summary>Отражающая трансформация (определитель меньше нуля) выворачивает обход, поэтому порядок вершин меняется.</summary>
    private static void AppendMesh(MeshBuilder builder, Mesh? mesh, Transform local, CoordinateTransform coordinates)
    {
        if (mesh is null || mesh.NumTriangles == 0) return;

        var vertices = coordinates.ToSharedMillimeters(mesh.Vertices, local);
        var mirrored = local.Determinant < 0;
        for (var i = 0; i < mesh.NumTriangles; i++)
        {
            var triangle = mesh.get_Triangle(i);
            var a = vertices[triangle.get_Index(0)];
            var b = vertices[triangle.get_Index(1)];
            var c = vertices[triangle.get_Index(2)];
            if (mirrored) builder.AddTriangle(a, c, b);
            else builder.AddTriangle(a, b, c);
        }
    }

    private static ElementInfo ReadInfo(Element element, Dictionary<BuiltInCategory, string> categoryNames)
    {
        var doc = element.Document;
        var type = element.GetTypeId().ToElement<ElementType>(doc);
        var workset = doc.IsWorkshared ? doc.GetWorksetTable().GetWorkset(element.WorksetId)?.Name : null;
        return new ElementInfo(
            element.UniqueId,
            IdValue(element.Id),
            categoryNames[(BuiltInCategory)IdValue(element.Category.Id)],
            type?.FamilyName,
            type?.Name,
            LevelOf(element)?.Name,
            workset,
            ConnectivityReader.ReadConnectedUniqueIds(element),
            RootComponentUniqueId(element));
    }

    /// <summary>
    ///     Экземпляр верхнего уровня цепочки вложенных общих семейств; null, если элемент не вложен. Изоляция и облицовка
    ///     относятся к изделию своей основы: против неё они не проверяются.
    /// </summary>
    private static string? RootComponentUniqueId(Element element)
    {
        if (element is InsulationLiningBase insulation)
            return insulation.HostElementId.ToElement(element.Document) is { } host ? RootComponentUniqueId(host) ?? host.UniqueId : null;
        if (element is not FamilyInstance { SuperComponent: { } super }) return null;
        var root = super;
        // Глубина вложенности семейств Revit невелика; ограничение защищает от циклической ссылки.
        for (var depth = 0; depth < 64 && root is FamilyInstance { SuperComponent: { } next }; depth++) root = next;
        return root.UniqueId;
    }

    private static Level? LevelOf(Element element) =>
        element.LevelId.ToElement<Level>(element.Document) ??
        element.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM)?.AsElement<Level>() ??
        element.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)?.AsElement<Level>();

    private static long IdValue(ElementId id) =>
#if REVIT2024_OR_GREATER
        id.Value;
#else
        id.IntegerValue;
#endif
}
