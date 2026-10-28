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
    /// <exception cref="OperationCanceledException">Прогон отменён.</exception>
    public ClashRunResult Run(IReadOnlyList<ModelInput> models, ClashConfiguration config, CancellationToken ct)
    {
        if (models is null) throw new ArgumentNullException(nameof(models));
        if (config is null) throw new ArgumentNullException(nameof(config));
        ct.ThrowIfCancellationRequested();

        var threads = _options.MaxDegreeOfParallelism > 0 ? _options.MaxDegreeOfParallelism : Environment.ProcessorCount;
        var peak = new PeakMemory();

        var scene = ClashScene.Build(models, SetMembership.Evaluate(config, models));
        peak.Sample();
        ct.ThrowIfCancellationRequested();

        var clock = Stopwatch.StartNew();
        var pairs = BroadPhase.Find(scene, config, new PairFilter(scene, config), threads);
        var broadPhase = clock.Elapsed;
        peak.Sample();
        ct.ThrowIfCancellationRequested();

        clock.Restart();
        var hits = new List<ClashHit>();
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

        var stats = new ClashRunStats(scene.ElementCount, scene.TriangleCount, pairs.Count, broadPhase, narrowPhase, peak.Value);
        return new ClashRunResult(hits, stats);
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
                if (narrow.Containment.IsInside(pair.A, pair.B, _candidates)) zone = a.Bounds;
                else if (narrow.Containment.IsInside(pair.B, pair.A, _candidates)) zone = b.Bounds;
                else return false;
            }

            var tolerance = Math.Max(pair.ToleranceMm, MinToleranceMm);
            var depth = PenetrationDepth.Estimate(
                a, narrow.BvhOf(pair.A), b, narrow.BvhOf(pair.B), zone.Expand(tolerance + ZoneMargin), _shared, _scratch);
            if (!(depth > tolerance)) return false;

            var (first, second) = a.Ref.CompareTo(b.Ref) <= 0 ? (a.Ref, b.Ref) : (b.Ref, a.Ref);
            hit = new ClashHit(
                first,
                second,
                scene.OriginX + ((double)zone.Min.X + zone.Max.X) / 2,
                scene.OriginY + ((double)zone.Min.Y + zone.Max.Y) / 2,
                scene.OriginZ + ((double)zone.Min.Z + zone.Max.Z) / 2,
                (float)depth);
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
