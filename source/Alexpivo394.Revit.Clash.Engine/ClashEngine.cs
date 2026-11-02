using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace Alexpivo394.Revit.Clash.Engine;

/// <summary>
///     Движок проверки: наборы, сцена, широкая фаза по габаритам, затем параллельная точная фаза -
///     пересечение треугольников, вложение замкнутых сеток и глубина проникновения против допуска ячейки матрицы.
/// </summary>
/// <remarks>
///     Для пары треугольники меньшего элемента (только с габаритом в общей части габаритов пары) проверяются
///     по BVH большего. BVH элемента (с замкнутостью и внешней ориентацией сетки) строится лениво при первой
///     надобности и живёт до конца прогона. Допуск ячейки меньше 0.05 мм поднимается до 0.05 мм.
/// </remarks>
public sealed class ClashEngine(EngineOptions options)
{
    /// <summary>
    ///     Версия алгоритма поиска. Повышать при любой правке точной фазы, фильтра пар, вложения, глубины или допусков.
    ///     Основа прошлого прогона с другой версией не переиспользуется — это проверяет вызывающий.
    /// </summary>
    public const int AlgorithmVersion = 1;

    private const int PairsPerBatch = 256;
    private const long MemorySampleInterval = 10_000;

    /// <summary>Расширение зоны сверх допуска при оценке глубины, мм.</summary>
    private const float ZoneMargin = 1;

    /// <summary>Наибольшее число вершин элемента, проверяемых на вложенность в другой элемент пары.</summary>
    private const int MaxInsideSamples = 256;

    /// <summary>
    ///     Нижняя граница допуска, мм: при допуске 0 касание с погрешностью float не становится коллизией.
    /// </summary>
    private const float MinToleranceMm = 0.05f;

    private readonly EngineOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Ищет коллизии между моделями части по конфигурации.</summary>
    /// <param name="previous">Прошлый прогон той же конфигурации: пары с неизменными элементами берутся из него без точной фазы.</param>
    /// <exception cref="OperationCanceledException">Прогон отменён.</exception>
    public ClashRunResult Run(IReadOnlyList<ModelInput> models, ClashConfiguration config, CancellationToken ct, IncrementalBase? previous = null)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        if (config is null) throw new ArgumentNullException(nameof(config));
        ct.ThrowIfCancellationRequested();

        var threads = _options.MaxDegreeOfParallelism > 0 ? _options.MaxDegreeOfParallelism : Environment.ProcessorCount;
        var peak = new PeakMemory();

        var membership = SetMembership.Evaluate(config, models);
        var scene = ClashScene.Build(models, membership);
        peak.Sample();
        ct.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        var pairs = BroadPhase.Find(scene, config, new PairFilter(scene, config), threads);
        var broadPhase = clock.Elapsed;
        peak.Sample();
        ct.ThrowIfCancellationRequested();

        clock.Restart();
        var hits = new List<ClashHit>();
        var candidates = pairs.Count;
        var reused = 0L;
        if (previous is not null) pairs = Reuse(scene, models, membership, previous, pairs, hits, out reused);
        if (pairs.Count > 0)
        {
            var narrow = new NarrowPhase(scene);
            var parts = new ConcurrentBag<List<ClashHit>>();
            long processed = 0;
            Parallel.ForEach(
                Partitioner.Create(0, pairs.Count, PairsPerBatch),
                new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct },
                () => new Worker(narrow),
                (range, _, worker) =>
                {
                    for (var i = range.Item1; i < range.Item2; i++)
                    {
                        if (worker.Check(pairs[i], out var hit)) worker.Hits.Add(hit);
                    }

                    var count = range.Item2 - range.Item1;
                    var done = Interlocked.Add(ref processed, count);
                    if (done / MemorySampleInterval != (done - count) / MemorySampleInterval) peak.Sample();
                    return worker;
                },
                worker => parts.Add(worker.Hits));

            foreach (var part in parts) hits.AddRange(part);
        }

        ct.ThrowIfCancellationRequested();
        hits.Sort(static (x, y) =>
        {
            var byA = x.A.CompareTo(y.A);
            return byA != 0 ? byA : x.B.CompareTo(y.B);
        });
        var narrowPhase = clock.Elapsed;
        peak.Sample();

        var stats = new ClashRunStats(scene.ElementCount, scene.TriangleCount, candidates, broadPhase, narrowPhase, peak.Value, reused);
        return new ClashRunResult(hits, stats);
    }

    /// <summary>
    ///     Точная проверка заданных пар по правилам конфигурации — наборы, клетки, исключения, соединения MEP, одно изделие,
    ///     допуск — без широкой фазы. Пара, элемента которой нет в моделях или в наборах, коллизией не будет.
    /// </summary>
    /// <returns>Коллизии из заданных пар, элементы в каноническом порядке.</returns>
    /// <exception cref="OperationCanceledException">Проверка отменена.</exception>
    public IReadOnlyList<ClashHit> Recheck(IReadOnlyList<ModelInput> models, ClashConfiguration config, IReadOnlyList<(ElementRef A, ElementRef B)> pairs,
        CancellationToken ct)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (pairs is null) throw new ArgumentNullException(nameof(pairs));

        var scene = ClashScene.Build(models, SetMembership.Evaluate(config, models));
        var filter = new PairFilter(scene, config);
        var index = new Dictionary<ElementRef, int>(scene.ElementCount);
        for (var i = 0; i < scene.Elements.Count; i++) index[scene.Elements[i].Ref] = i;

        var candidates = new List<CandidatePair>();
        var cells = new List<PairCell>(4);
        foreach (var (refA, refB) in pairs.Distinct())
        {
            if (!index.TryGetValue(refA, out var x) || !index.TryGetValue(refB, out var y) || x == y) continue;
            var (a, b) = x < y ? (x, y) : (y, x);
            if (!scene.Elements[a].Bounds.Overlaps(scene.Elements[b].Bounds)) continue;
            cells.Clear();
            var tolerance = float.MaxValue;
            foreach (var ga in scene.Elements[a].Groups)
            foreach (var gb in scene.Elements[b].Groups)
            {
                var (first, second) = ga <= gb ? (ga, gb) : (gb, ga);
                if (cells.Exists(c => c.GroupA == first && c.GroupB == second)) continue;
                if (!filter.Passes(a, b, ga, gb, out var cellTolerance)) continue;
                cells.Add(new PairCell(first, second, cellTolerance));
                tolerance = Math.Min(tolerance, cellTolerance);
            }

            if (cells.Count > 0) candidates.Add(new CandidatePair(a, b, tolerance, [.. cells]));
        }

        var worker = new Worker(new NarrowPhase(scene));
        foreach (var pair in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (worker.Check(pair, out var hit)) worker.Hits.Add(hit);
        }

        return worker.Hits;
    }

    // Пары, у обоих элементов которых состояние не изменилось, получают коллизию прошлого прогона или ничего; остальные идут в точную фазу.
    private static List<CandidatePair> Reuse(ClashScene scene, IReadOnlyList<ModelInput> models, SetMembership membership, IncrementalBase previous,
        List<CandidatePair> pairs, List<ClashHit> hits, out long reused)
    {
        var fingerprints = new Dictionary<ElementRef, (ulong Fingerprint, ElementInfo Info)>(scene.ElementCount);
        foreach (var model in models)
        foreach (var element in model.Geometry.Elements)
            fingerprints[new ElementRef(model.Geometry.Header.ModelKey, element.Info.UniqueId)] = (element.Fingerprint, element.Info);

        var unchanged = new bool[scene.Elements.Count];
        for (var i = 0; i < unchanged.Length; i++)
        {
            var element = scene.Elements[i].Ref;
            var (fingerprint, info) = fingerprints[element];
            unchanged[i] = previous.TryGetState(element, out var state) &&
                           state == IncrementalBase.StateOf(fingerprint, membership.SetsOf(element.ModelKey, element.UniqueId), info);
        }

        reused = 0;
        var rest = new List<CandidatePair>(pairs.Count);
        foreach (var pair in pairs)
        {
            if (!unchanged[pair.A] || !unchanged[pair.B])
            {
                rest.Add(pair);
                continue;
            }

            reused++;
            var a = scene.Elements[pair.A].Ref;
            var b = scene.Elements[pair.B].Ref;
            var (first, second) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            if (previous.HitOf(first, second) is not { } hit) continue;
            var checks = ChecksOf(scene.Groups, pair, hit.DepthMm);
            if (checks.Count > 0) hits.Add(hit with { Checks = checks });
        }

        return rest;
    }

    // Проверки, в которых глубина больше допуска своей клетки; клетка с наименьшим допуском проходит всегда.
    private static IReadOnlyList<CheckRef> ChecksOf(IReadOnlyList<Guid> groups, CandidatePair pair, double depth)
    {
        var checks = new List<CheckRef>(pair.Cells.Length);
        foreach (var cell in pair.Cells)
        {
            if (depth > Math.Max(cell.ToleranceMm, MinToleranceMm)) checks.Add(CheckRef.Of(groups[cell.GroupA], groups[cell.GroupB]));
        }

        return checks;
    }

    /// <summary>Общее для потоков состояние точной фазы: ленивые BVH и кеш замкнутости.</summary>
    private sealed class NarrowPhase
    {
        private readonly Lazy<MeshBvh>[] _bvh;

        public NarrowPhase(ClashScene scene)
        {
            Scene = scene;
            var elements = scene.Elements;
            _bvh = new Lazy<MeshBvh>[elements.Count];
            for (var i = 0; i < _bvh.Length; i++)
            {
                var element = elements[i];
                _bvh[i] = new Lazy<MeshBvh>(() => MeshBvh.Build(element), LazyThreadSafetyMode.ExecutionAndPublication);
            }

            Containment = new Containment(elements, BvhOf);
        }

        public ClashScene Scene { get; }

        public Containment Containment { get; }

        public MeshBvh BvhOf(int element) => _bvh[element].Value;
    }

    /// <summary>Проверка пар одним потоком со своими буферами.</summary>
    private sealed class Worker(NarrowPhase narrow)
    {
        private readonly List<int> _candidates = [];
        private readonly List<int> _smallTriangles = [];
        private readonly PenetrationDepth.Scratch _scratch = new();
        private readonly List<Vector3> _shared = [];
        private readonly List<int> _inCommon = [];

        public List<ClashHit> Hits { get; } = [];

        public bool Check(CandidatePair pair, out ClashHit hit)
        {
            hit = null!;
            var scene = narrow.Scene;
            var a = scene.Elements[pair.A];
            var b = scene.Elements[pair.B];

            var zone = Aabb.Empty;
            var common = Common(a.Bounds, b.Bounds);
            var (small, large) = a.Indices.Length <= b.Indices.Length ? (pair.A, pair.B) : (pair.B, pair.A);
            _shared.Clear();
            if (Intersections(narrow.BvhOf(small), narrow.BvhOf(large), common, ref zone))
            {
                CollectInside(pair.A, pair.B, common);
                CollectInside(pair.B, pair.A, common);
                foreach (var point in _shared) zone = zone.Union(new Aabb(point, point));
            }
            else
            {
                // Вложенный элемент целиком в обоих телах: его вершины - общие точки, иначе внешний элемент представлен
                // лишь треугольниками, чей габарит задел зону, например одной боковой гранью.
                int inner;
                if (narrow.Containment.IsInside(pair.A, pair.B, _candidates)) inner = pair.A;
                else if (narrow.Containment.IsInside(pair.B, pair.A, _candidates)) inner = pair.B;
                else return false;
                zone = scene.Elements[inner].Bounds;
                SampleVertices(scene.Elements[inner].Positions);
            }

            var tolerance = Math.Max(pair.ToleranceMm, MinToleranceMm);
            // Решение — по глубине, которая записывается в коллизию: повтор с основой сравнивает с допуском её же.
            var depth = (float)PenetrationDepth.Estimate(
                a, narrow.BvhOf(pair.A), b, narrow.BvhOf(pair.B), zone.Expand(tolerance + ZoneMargin), _shared, _scratch);
            if (!(depth > tolerance)) return false;

            var (first, second) = a.Ref.CompareTo(b.Ref) <= 0 ? (a.Ref, b.Ref) : (b.Ref, a.Ref);
            hit = new ClashHit(
                first,
                second,
                scene.OriginX + ((double)zone.Min.X + zone.Max.X) / 2,
                scene.OriginY + ((double)zone.Min.Y + zone.Max.Y) / 2,
                scene.OriginZ + ((double)zone.Min.Z + zone.Max.Z) / 2,
                depth,
                ChecksOf(scene.Groups, pair, depth));
            return true;
        }

        // Вершины элемента source в общем габарите, лежащие внутри замкнутого outer; не больше MaxInsideSamples проверок.
        private void CollectInside(int source, int outer, in Aabb common)
        {
            if (!narrow.BvhOf(source).IsClosed || !narrow.BvhOf(outer).IsClosed) return;
            var positions = narrow.Scene.Elements[source].Positions;
            var inCommon = _inCommon;
            inCommon.Clear();
            for (var v = 0; v < positions.Length / 3; v++)
            {
                var p = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
                if (Vector3.Min(p, common.Min) == common.Min && Vector3.Max(p, common.Max) == common.Max) inCommon.Add(v);
            }

            var take = Math.Min(MaxInsideSamples, inCommon.Count);
            for (var i = 0; i < take; i++)
            {
                var v = inCommon[(int)((long)i * inCommon.Count / take)];
                if (narrow.Containment.ContainsPoint(outer, Vec3d.At(positions, v), _candidates))
                    _shared.Add(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
            }
        }

        // Равномерная выборка вершин элемента, не больше MaxInsideSamples, в общие точки.
        private void SampleVertices(float[] positions)
        {
            var count = positions.Length / 3;
            var take = Math.Min(MaxInsideSamples, count);
            for (var i = 0; i < take; i++)
            {
                var v = (int)((long)i * count / take);
                _shared.Add(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
            }
        }

        // Все отрезки пересечения треугольников пары; zone - их общий габарит. Треугольники берутся из BVH,
        // то есть с внешней ориентацией для замкнутых сеток.
        private bool Intersections(MeshBvh small, MeshBvh large, in Aabb common, ref Aabb zone)
        {
            var eps = (float)TriangleIntersection.PlaneEpsilon;
            _smallTriangles.Clear();
            small.Query(common.Expand(eps), _smallTriangles);
            var found = false;
            foreach (var t in _smallTriangles)
            {
                small.Vertices(t, out var a0, out var a1, out var a2);
                _candidates.Clear();
                large.Query(small.BoundsOf(t).Expand(eps), _candidates);
                foreach (var u in _candidates)
                {
                    large.Vertices(u, out var b0, out var b1, out var b2);
                    if (!TriangleIntersection.Intersect(a0, a1, a2, b0, b1, b2, out var p, out var q)) continue;
                    zone = zone.Union(BoxOf(p, q, q));
                    found = true;
                }
            }

            return found;
        }

        private static Aabb Common(in Aabb x, in Aabb y) =>
            new(Vector3.Max(x.Min, y.Min), Vector3.Min(x.Max, y.Max));

        private static Aabb BoxOf(in Vec3d a, in Vec3d b, in Vec3d c) =>
            new(
                new Vector3(
                    (float)Math.Min(a.X, Math.Min(b.X, c.X)),
                    (float)Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                    (float)Math.Min(a.Z, Math.Min(b.Z, c.Z))),
                new Vector3(
                    (float)Math.Max(a.X, Math.Max(b.X, c.X)),
                    (float)Math.Max(a.Y, Math.Max(b.Y, c.Y)),
                    (float)Math.Max(a.Z, Math.Max(b.Z, c.Z))));
    }

    /// <summary>Наибольший замер <see cref="GC.GetTotalMemory"/> в контрольных точках; потокобезопасно.</summary>
    private sealed class PeakMemory
    {
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public void Sample()
        {
            var current = GC.GetTotalMemory(false);
            var seen = Interlocked.Read(ref _value);
            while (current > seen)
            {
                var previous = Interlocked.CompareExchange(ref _value, current, seen);
                if (previous == seen) return;
                seen = previous;
            }
        }
    }
}
