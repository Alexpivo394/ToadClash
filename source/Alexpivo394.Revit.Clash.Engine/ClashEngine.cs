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
    public const int AlgorithmVersion = 2;

    private const int PairsPerBatch = 256;
    private const long MemorySampleInterval = 10_000;

    /// <summary>Расширение зоны сверх допуска при оценке глубины, мм.</summary>
    private const float ZoneMargin = 1;

    /// <summary>Наибольшее число вершин элемента, проверяемых на вложенность в другой элемент пары.</summary>
    private const int MaxInsideSamples = 256;

    /// <summary>Наибольшее число точек местной глубины на пару.</summary>
    private const int MaxLocalSamples = 192;

    /// <summary>Доля поверхности меньшего элемента под совпадающими гранями, с которой пара - дубликат.</summary>
    private const double DuplicateShare = 0.25;

    /// <summary>Доли отрезка пересечения, в которых берутся точки местной глубины: концы часто лежат на рёбрах.</summary>
    private static readonly double[] SegmentShares = [0.02, 0.25, 0.5, 0.75, 0.98];

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
        private readonly Lazy<double>[] _area;

        public NarrowPhase(ClashScene scene)
        {
            Scene = scene;
            var elements = scene.Elements;
            _bvh = new Lazy<MeshBvh>[elements.Count];
            _area = new Lazy<double>[elements.Count];
            for (var i = 0; i < _bvh.Length; i++)
            {
                var element = elements[i];
                _bvh[i] = new Lazy<MeshBvh>(() => MeshBvh.Build(element), LazyThreadSafetyMode.ExecutionAndPublication);
                _area[i] = new Lazy<double>(() => Area(element), LazyThreadSafetyMode.ExecutionAndPublication);
            }

            Containment = new Containment(elements, BvhOf);
        }

        public ClashScene Scene { get; }

        public Containment Containment { get; }

        public MeshBvh BvhOf(int element) => _bvh[element].Value;

        /// <summary>Площадь поверхности элемента, мм².</summary>
        public double AreaOf(int element) => _area[element].Value;

        private static double Area(SceneElement element)
        {
            var area = 0.0;
            var indices = element.Indices;
            for (var t = 0; t + 2 < indices.Length; t += 3)
            {
                var a = Vec3d.At(element.Positions, indices[t]);
                var b = Vec3d.At(element.Positions, indices[t + 1]);
                var c = Vec3d.At(element.Positions, indices[t + 2]);
                area += Math.Sqrt(Vec3d.Cross(b - a, c - a).LengthSquared) / 2;
            }

            return area;
        }
    }

    /// <summary>Проверка пар одним потоком со своими буферами.</summary>
    private sealed class Worker(NarrowPhase narrow)
    {
        private readonly List<int> _candidates = [];
        private readonly List<int> _smallTriangles = [];
        private readonly PenetrationDepth.Scratch _scratch = new();
        private readonly List<Vector3> _shared = [];
        private readonly List<int> _inCommon = [];
        private readonly List<int> _sharedSource = [];
        private readonly List<LocalDepth.Sample> _surface = [];
        private readonly List<Vec3d> _normals = [];
        private double _coplanarArea;
        private readonly List<LocalDepth.Sample> _samples = [];
        private readonly HashSet<(long, long, long)> _cells = [];
        private readonly LocalDepth.Rays _rays = new();

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
            _sharedSource.Clear();
            _surface.Clear();
            _normals.Clear();
            _coplanarArea = 0;
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
                SampleVertices(inner);
            }

            var tolerance = Math.Max(pair.ToleranceMm, MinToleranceMm);
            // Решение — по глубине, которая записывается в коллизию: повтор с основой сравнивает с допуском её же.
            var depth = (float)PenetrationDepth.Estimate(
                a, narrow.BvhOf(pair.A), b, narrow.BvhOf(pair.B), zone.Expand(tolerance + ZoneMargin), _shared, _scratch);
            if (!(depth > tolerance)) return false;

            // Оценка по зоне видит тела целиком и не знает о вырезах: у перегородки, заходящей в край отверстия пола
            // на 1 мм, глубина выходит толщиной пола. Местная глубина по точкам пересечения это различает. Она считается
            // до двух наибольших допусков клеток пары: нахлёст до этой величины записывается своей толщиной; дошла до предела -
            // тело окружено другим или вошло глубоко, остаётся оценка по зоне.
            var bvhA = narrow.BvhOf(pair.A);
            var bvhB = narrow.BvhOf(pair.B);
            if (bvhA.IsClosed && bvhB.IsClosed && !Duplicates(pair))
            {
                var largest = LargestTolerance(pair);
                var limit = 2 * largest + ZoneMargin;
                var local = LocalDepth.Estimate(bvhA, bvhB, Samples(limit), _normals, _surface.Count > 0, limit, _rays);
                if (local < limit - ZoneMargin / 2) depth = Math.Min(depth, (float)local);
                if (!(depth > tolerance)) return false;
            }

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

        // Дубликат (копия элемента на том же месте или почти): сонаправленные совпадающие грани покрывают заметную долю
        // поверхности меньшего элемента. У дубликата из тонких листов местная глубина - толщина листа, а коллизия -
        // весь элемент; узкий нахлёст у выреза даёт ничтожную долю.
        private bool Duplicates(in CandidatePair pair) =>
            _coplanarArea >= DuplicateShare * Math.Min(narrow.AreaOf(pair.A), narrow.AreaOf(pair.B));

        private static float LargestTolerance(in CandidatePair pair)
        {
            var largest = Math.Max(pair.ToleranceMm, MinToleranceMm);
            foreach (var cell in pair.Cells) largest = Math.Max(largest, cell.ToleranceMm);
            return largest;
        }

        // Точки местной глубины: точки пересечения поверхностей, сдвинутые внутрь обоих тел (против суммы наружных
        // нормалей; у встречных граней - касание, такие точки не берутся), и вершины одного тела внутри другого, сдвинутые
        // к середине своего тела. По одной на ячейку со стороной в 1/4 предела, не больше MaxLocalSamples.
        private List<LocalDepth.Sample> Samples(float limit)
        {
            var cell = limit / 4;
            _cells.Clear();
            _samples.Clear();
            foreach (var sample in _surface)
            {
                var inward = (sample.NormalA + sample.NormalB) * -1;
                if (inward.LengthSquared >= 0.01) Add(sample with { Point = sample.Point + inward.Normalized() * LocalDepth.Nudge });
            }

            for (var i = 0; i < _shared.Count; i++)
            {
                var point = new Vec3d(_shared[i].X, _shared[i].Y, _shared[i].Z);
                var center = narrow.Scene.Elements[_sharedSource[i]].Bounds.Center;
                var inward = new Vec3d(center.X, center.Y, center.Z) - point;
                if (inward.LengthSquared > 0) point += inward.Normalized() * LocalDepth.Nudge;
                Add(new LocalDepth.Sample(point, default, default));
            }

            if (_samples.Count <= MaxLocalSamples) return _samples;
            var thinned = new List<LocalDepth.Sample>(MaxLocalSamples);
            for (var i = 0; i < MaxLocalSamples; i++) thinned.Add(_samples[(int)((long)i * _samples.Count / MaxLocalSamples)]);
            return thinned;

            void Add(in LocalDepth.Sample sample)
            {
                var p = sample.Point;
                if (_cells.Add(((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell), (long)Math.Floor(p.Z / cell)))) _samples.Add(sample);
            }
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
                if (!narrow.Containment.ContainsPoint(outer, Vec3d.At(positions, v), _candidates)) continue;
                _shared.Add(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
                _sharedSource.Add(source);
            }
        }

        // Равномерная выборка вершин элемента, не больше MaxInsideSamples, в общие точки.
        private void SampleVertices(int element)
        {
            var positions = narrow.Scene.Elements[element].Positions;
            var count = positions.Length / 3;
            var take = Math.Min(MaxInsideSamples, count);
            for (var i = 0; i < take; i++)
            {
                var v = (int)((long)i * count / take);
                _shared.Add(new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]));
                _sharedSource.Add(element);
            }
        }

        // Нормали пересекающихся граней пары без повторов (с точностью до 2.5°), не больше MaxPairNormals: направления
        // местной глубины для всех точек - у точки на ребре своей нормали отделяющей грани нет.
        private void AddNormal(in Vec3d normal)
        {
            if (_normals.Count >= LocalDepth.MaxPairNormals) return;
            foreach (var known in _normals)
                if (Math.Abs(Vec3d.Dot(known, normal)) > 0.999) return;
            _normals.Add(normal);
        }

        // Все отрезки пересечения треугольников пары; zone - их общий габарит. Треугольники берутся из BVH,
        // то есть с внешней ориентацией для замкнутых сеток. Точки отрезков (SegmentShares) с направлением внутрь обоих
        // тел (против суммы наружных нормалей) идут в точки местной глубины, нормали - в направления.
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
                    if (!TriangleIntersection.Intersect(a0, a1, a2, b0, b1, b2, out var p, out var q, out var coplanar)) continue;
                    _coplanarArea += coplanar;
                    zone = zone.Union(BoxOf(p, q, q));
                    var normalSmall = Vec3d.Cross(a1 - a0, a2 - a0).Normalized();
                    var normalLarge = Vec3d.Cross(b1 - b0, b2 - b0).Normalized();
                    foreach (var share in SegmentShares) _surface.Add(new LocalDepth.Sample(p + (q - p) * share, normalSmall, normalLarge));
                    AddNormal(normalSmall);
                    AddNormal(normalLarge);
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
