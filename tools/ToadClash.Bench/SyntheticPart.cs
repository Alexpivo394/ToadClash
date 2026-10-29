using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Alexpivo394.Revit.Clash.Geometry;
using Alexpivo394.Revit.Clash.Rules;

namespace ToadClash.Bench;

/// <summary>
///     Генератор синтетической части здания: 8 разделов, 10 этажей по 3600 мм на сетке 60x60 м.
///     КР - плиты, колонны, стены; АР - перегородки и двери; ОВВ - короба, ОВО, ВК, ПТ - трубы, ЭОМ, СС - лотки.
///     Инженерные сети - цепочки "участок - фитинг - участок", соединённые через ConnectedUniqueIds.
///     Результат целиком определяется числом элементов, числом моделей и seed.
/// </summary>
/// <summary>Профиль синтетической части: обычный (аккуратные трассы по полосам) или плотный (трассы в общем потолочном пространстве, диагональные и наклонные, стояки пучками, много коллизий).</summary>
public enum SyntheticProfile
{
    /// <summary>Аккуратные трассы по полосам, отдельные слои по высоте.</summary>
    Standard,

    /// <summary>Общее потолочное пространство, диагональные и наклонные трассы, пучки стояков.</summary>
    Dense
}

public static class SyntheticPart
{
    private const float FloorHeight = 3600;
    private const float SlabThickness = 200;
    private const int Floors = 10;
    private const float Grid = 6000;
    private const float BuildingSize = 60000;

    /// <summary>Доля "неаккуратных" цепочек, идущих где попало и пересекающих всё на пути.</summary>
    private const double CarelessShare = 0.008;

    /// <summary>Доли цепочек, прижатых к плите перекрытия с неглубоким (меньше допуска) и глубоким проникновением.</summary>
    private const double ShallowShare = 0.06;

    private const double DeepShare = 0.008;

    private const double DenseCarelessShare = 0.005;
    private const double DenseDeepShare = 0.01;

    /// <summary>Плотный профиль: доли цепочек с диагональной и наклонной трассой, доля элементов в пучках стояков.</summary>
    private const double DiagonalShare = 0.27;

    private const double SlopedShare = 0.08;
    private const int RiserPercent = 8;

    /// <summary>Отступ цепочек от краёв полосы и друг от друга, мм.</summary>
    private const float BandMargin = 40;

    private const float ChainGap = 60;

    /// <summary>Максимальная длина прямого участка трубы или короба между соединениями, мм.</summary>
    private const float MaxPieceLength = 1700;

    private static readonly (string Discipline, int Weight)[] Plan =
    [
        ("КР", 10), ("АР", 12), ("ОВВ", 10), ("ОВВ", 8), ("ОВО", 8), ("ВК", 9),
        ("ВК", 8), ("ПТ", 7), ("ЭОМ", 9), ("ЭОМ", 8), ("СС", 6), ("СС", 5)
    ];

    /// <summary>Начала координат моделей в общих координатах, мм: миллионы, как в реальных проектах. Здание стоит в начале первой.</summary>
    private static readonly (double X, double Y, double Z)[] Origins =
    [
        (2_345_000, 6_789_000, 0), (2_344_000, 6_788_500, 0), (2_346_000, 6_790_500, -1_000)
    ];

    /// <summary>Полосы вдоль X внутри строки ячеек (относительно 6000 j): свободные от колонн, стен и перегородок.</summary>
    private static readonly (float Lo, float Hi)[] Bands = [(250, 1450), (1550, 2950), (3050, 4450), (4550, 5750)];

    /// <summary>Разделы синтетической части.</summary>
    public static IReadOnlyList<string> Disciplines { get; } = Plan.Select(p => p.Discipline).Distinct().ToArray();

    /// <summary>Строит все модели части и держит их в памяти. Для больших частей используйте <see cref="Enumerate"/>.</summary>
    public static IReadOnlyList<(ModelGeometry Geometry, ModelParameters Parameters, string Discipline)> Generate(
        int elements, int models, int seed, SyntheticProfile profile = SyntheticProfile.Standard) =>
        Enumerate(elements, models, seed, profile).ToList();

    /// <summary>Строит модели по одной, чтобы вызывающий мог записать модель и освободить память.</summary>
    public static IEnumerable<(ModelGeometry Geometry, ModelParameters Parameters, string Discipline)> Enumerate(
        int elements, int models, int seed, SyntheticProfile profile = SyntheticProfile.Standard)
    {
        if (elements <= 0) throw new ArgumentOutOfRangeException(nameof(elements));
        if (models <= 0) throw new ArgumentOutOfRangeException(nameof(models));

        var plans = Enumerable.Range(0, models).Select(i => Plan[i % Plan.Length]).ToArray();
        var counts = Distribute(elements, plans.Select(p => p.Weight).ToArray());
        for (var index = 0; index < models; index++)
        {
            var discipline = plans[index].Discipline;
            var sameBefore = plans.Take(index).Count(p => p.Discipline == discipline);
            var sameTotal = plans.Count(p => p.Discipline == discipline);
            var rng = new Random(unchecked(seed * 7919 + index * 104729 + 1));
            var builder = new ModelBuilder($"{discipline}-{sameBefore + 1}", discipline, index, Origins[index % Origins.Length], counts[index]);
            switch (discipline)
            {
                case "КР": BuildStructure(builder, rng); break;
                case "АР": BuildArchitecture(builder, rng); break;
                default: BuildMep(builder, rng, sameTotal, sameBefore, profile == SyntheticProfile.Dense); break;
            }

            yield return builder.Finish();
        }
    }

    /// <summary>Конфигурация: набор на раздел (все элементы с категорией), матрица - все пары разделов, допуск 20.</summary>
    public static ClashConfiguration CreateConfiguration()
    {
        var sets = Disciplines.Select(d => new DisciplineSet(
            StableGuid(d),
            d,
            $"Все элементы {d}",
            [new ConditionGroup([new Condition(new PropertyRef(PropertyKind.Category), ConditionOperator.IsNotEmpty, null)])])).ToList();
        var matrix = new List<DisciplineCell>();
        for (var i = 0; i < Disciplines.Count; i++)
        for (var j = i; j < Disciplines.Count; j++)
            matrix.Add(new DisciplineCell(Disciplines[i], Disciplines[j], true, 20));
        return ConfigurationDefaults.FromDisciplines(sets, matrix, []);
    }

    private static Guid StableGuid(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    private static int[] Distribute(int total, int[] weights)
    {
        var sum = weights.Sum();
        var result = weights.Select(w => (int)((long)total * w / sum)).ToArray();
        for (var i = 0; total - result.Sum() > 0; i = (i + 1) % result.Length) result[i]++;
        return result;
    }

    private static string LevelName(int floor) => $"Этаж {floor + 1:D2}";

    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    private static void Shuffle<T>(IList<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ---------------------------------------------------------------- КР

    private static void BuildStructure(ModelBuilder b, Random rng)
    {
        var total = b.Target;
        var columnCount = Math.Min(11 * 11 * Floors, total * 12 / 100);
        var wallBudget = total * 35 / 100;

        var columns = new List<(int I, int J, int F)>();
        for (var f = 0; f < Floors; f++)
        for (var i = 0; i <= 10; i++)
        for (var j = 0; j <= 10; j++)
            columns.Add((i, j, f));
        Shuffle(columns, rng);
        foreach (var (i, j, f) in columns.Take(columnCount))
        {
            var x = i * Grid;
            var y = j * Grid;
            b.Add("OST_StructuralColumns", "Колонна 500x500", "Колонна 500x500", LevelName(f),
                MeshPrimitives.Box(V(x - 250, y - 250, f * FloorHeight), V(x + 250, y + 250, (f + 1) * FloorHeight - SlabThickness)));
        }

        AddWalls(b, rng, wallBudget);
        AddSlabs(b, b.Remaining);
    }

    /// <summary>Линии стен: X-стены сплошные по осям y = 3000 + 6000 j, Y-стены - участки между X-стенами по x = 3000 + 6000 i.</summary>
    private static void AddWalls(ModelBuilder b, Random rng, int budget)
    {
        var yIntervals = new List<(float A, float B)> { (0, 2900) };
        for (var j = 0; j <= 8; j++) yIntervals.Add((3100 + j * Grid, 8900 + j * Grid));
        yIntervals.Add((57100, 60000));

        var runs = new List<(int F, bool AlongX, float Line, float A, float B)>();
        for (var f = 0; f < Floors; f++)
        {
            for (var j = 0; j < 10; j++) runs.Add((f, true, 3000 + j * Grid, 0, BuildingSize));
            for (var i = 0; i < 10; i++)
                foreach (var (a, c) in yIntervals) runs.Add((f, false, 3000 + i * Grid, a, c));
        }

        Shuffle(runs, rng);
        var totalLength = runs.Sum(r => r.B - r.A);
        var pieceLength = Math.Max(300f, totalLength / Math.Max(budget, 1));
        var built = 0;
        foreach (var (f, alongX, line, a, c) in runs)
        {
            if (built >= budget) break;
            var pieces = Math.Max(1, (int)MathF.Round((c - a) / pieceLength));
            var z0 = f * FloorHeight;
            var z1 = (f + 1) * FloorHeight - SlabThickness;
            for (var p = 0; p < pieces && b.Remaining > 0; p++)
            {
                var s = a + (c - a) * p / pieces;
                var e = a + (c - a) * (p + 1) / pieces;
                var mesh = alongX
                    ? MeshPrimitives.Box(V(s, line - 100, z0), V(e, line + 100, z1))
                    : MeshPrimitives.Box(V(line - 100, s, z0), V(line + 100, e, z1));
                b.Add("OST_Walls", "Стена 200", "Стена 200", LevelName(f), mesh);
            }

            built += pieces;
        }
    }

    /// <summary>Плиты на 11 уровнях (0..10): сетка плиток на всю площадь, лишние плитки делят пополам.</summary>
    private static void AddSlabs(ModelBuilder b, int count)
    {
        var levels = Floors + 1;
        for (var level = 0; level < levels && b.Remaining > 0; level++)
        {
            var n = Math.Min(b.Remaining, count / levels + (level < count % levels ? 1 : 0));
            if (n <= 0) continue;
            var rows = Math.Max(1, (int)Math.Sqrt(n));
            var cols = n / rows;
            var extra = n - rows * cols;
            var z1 = level * FloorHeight;
            var levelName = LevelName(Math.Min(level, Floors - 1));
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var x0 = BuildingSize * c / cols;
                var x1 = BuildingSize * (c + 1) / cols;
                var y0 = BuildingSize * r / rows;
                var y1 = BuildingSize * (r + 1) / rows;
                var split = r * cols + c < extra;
                var xm = split ? (x0 + x1) / 2 : x1;
                b.Add("OST_Floors", "Плита 200", "Плита 200", levelName,
                    MeshPrimitives.Box(V(x0, y0, z1 - SlabThickness), V(xm, y1, z1)));
                if (split)
                    b.Add("OST_Floors", "Плита 200", "Плита 200", levelName,
                        MeshPrimitives.Box(V(xm, y0, z1 - SlabThickness), V(x1, y1, z1)));
            }
        }
    }

    // ---------------------------------------------------------------- АР

    /// <summary>Перегородки по линиям y = 1500 + 3000 k между Y-стенами; часть участков - дверные блоки.</summary>
    private static void BuildArchitecture(ModelBuilder b, Random rng)
    {
        var intervals = new List<(int F, float Y, int Bay)>();
        for (var f = 0; f < Floors; f++)
        for (var j = 0; j < 10; j++)
        for (var k = 0; k < 2; k++)
        for (var bay = 0; bay < 9; bay++)
            intervals.Add((f, j * Grid + 1500 + k * 3000, bay));
        Shuffle(intervals, rng);

        var perInterval = Math.Max(1, (b.Target + intervals.Count - 1) / intervals.Count);
        foreach (var (f, y, bay) in intervals)
        {
            var a = 3100 + bay * Grid;
            var c = 8900 + bay * Grid;
            for (var p = 0; p < perInterval && b.Remaining > 0; p++)
            {
                var s = a + (c - a) * p / perInterval;
                var e = a + (c - a) * (p + 1) / perInterval;
                var z0 = f * FloorHeight;
                if (rng.NextDouble() < 0.15)
                    b.Add("OST_Doors", "Дверь 900x2100", "Дверь 900x2100", LevelName(f),
                        MeshPrimitives.Box(V(s, y - 50, z0), V(e, y + 50, z0 + 2100)));
                else
                    b.Add("OST_Walls", "Перегородка 100", "Перегородка 100", LevelName(f),
                        MeshPrimitives.Box(V(s, y - 50, z0), V(e, y + 50, (f + 1) * FloorHeight - SlabThickness)));
            }

            if (b.Remaining == 0) break;
        }
    }

    // ---------------------------------------------------------------- инженерные сети

    private enum MepKind
    {
        Pipe,
        Duct,
        Tray
    }

    private sealed record MepSpec(MepKind Kind, float Radius, float Width, float Height, int Segments)
    {
        public float HalfWidth => Kind == MepKind.Pipe ? Radius : Width / 2;
        public float HalfHeight => Kind == MepKind.Pipe ? Radius : Height / 2;
        public float Bend => Kind == MepKind.Pipe ? 2.5f * Radius + 50 : Width;
        public float Size => Kind == MepKind.Pipe ? 2 * Radius : Width;

        public Vector2[] Profile { get; } =
            Kind == MepKind.Pipe ? MeshPrimitives.Circle(Segments, Radius) : MeshPrimitives.Rectangle(Width, Height);
    }

    private sealed record ChainPart(string Category, string Family, string Type, ElementMesh Mesh, double Size);

    /// <summary>Свободная часть полосы в ячейке этажа: цепочки укладываются от края вдоль Y, <see cref="Cursor"/> - первая свободная Y.</summary>
    private sealed class Band(int floor, int bay, float cursor, float limit)
    {
        public int Floor { get; } = floor;
        public int Bay { get; } = bay;
        public float Cursor { get; set; } = cursor;
        public float Limit { get; } = limit;
    }

    private static (MepKind Kind, float Z) LayerOf(string discipline) => discipline switch
    {
        "ОВВ" => (MepKind.Duct, 3130),
        "ЭОМ" => (MepKind.Tray, 2850),
        "СС" => (MepKind.Tray, 2740),
        "ОВО" => (MepKind.Pipe, 2550),
        "ВК" => (MepKind.Pipe, 2330),
        _ => (MepKind.Pipe, 2110)
    };


    private static MepSpec RandomSpec(MepKind kind, Random rng)
    {
        switch (kind)
        {
            case MepKind.Pipe:
                float[] radii = [15, 20, 25, 32, 40, 50, 65, 80, 100];
                return new MepSpec(kind, radii[rng.Next(radii.Length)], 0, 0, 16 + 2 * rng.Next(5));
            case MepKind.Duct:
                float[] widths = [200, 300, 400, 500, 600];
                var w = widths[rng.Next(widths.Length)];
                return new MepSpec(kind, 0, w, Math.Min(w, 150 + 50 * rng.Next(6)), 4);
            default:
                float[] trayWidths = [100, 200, 300, 400, 600];
                float[] trayHeights = [50, 60, 100];
                return new MepSpec(kind, 0, trayWidths[rng.Next(trayWidths.Length)], trayHeights[rng.Next(trayHeights.Length)], 4);
        }
    }

    private static void BuildMep(ModelBuilder b, Random rng, int sameTotal, int sameBefore, bool dense)
    {
        var (kind, bandZ) = LayerOf(b.Discipline);
        var carelessShare = dense ? DenseCarelessShare : CarelessShare;
        var deepShare = dense ? DenseDeepShare : DeepShare;
        if (dense) BuildRisers(b, kind, rng);
        var bands = new List<Band>();
        for (var row = 0; row < 10; row++)
        {
            if (row % sameTotal != sameBefore) continue;
            for (var f = 0; f < Floors; f++)
            for (var bay = 0; bay < 9; bay++)
            foreach (var (lo, hi) in Bands)
                bands.Add(new Band(f, bay, row * Grid + lo + BandMargin + (dense ? (float)rng.NextDouble() * 400 : 0), row * Grid + hi - BandMargin));
        }

        while (b.Remaining > 0)
        {
            var spec = RandomSpec(kind, rng);
            var roll = rng.NextDouble();
            var width = 2 * spec.HalfWidth;
            if (dense)
            {
                var style = rng.NextDouble();
                if (style < DiagonalShare + SlopedShare)
                {
                    var free = FreePath(spec, rng, style < DiagonalShare, out var freeFloor);
                    BuildChain(b, spec, free, LevelName(freeFloor), rng);
                    continue;
                }
            }

            if (roll < carelessShare || bands.Count == 0)
            {
                var careless = CarelessPath(spec, rng, out var floor);
                BuildChain(b, spec, careless, LevelName(floor), rng);
                continue;
            }

            var pick = rng.Next(bands.Count);
            var band = bands[pick];
            if (band.Cursor + width > band.Limit)
            {
                bands[pick] = bands[^1];
                bands.RemoveAt(bands.Count - 1);
                continue;
            }

            var z = band.Floor * FloorHeight + bandZ;
            if (dense) z = band.Floor * FloorHeight + 2100 + spec.HalfHeight + (float)rng.NextDouble() * (1200 - 2 * spec.HalfHeight);
            if (roll < carelessShare + ShallowShare + deepShare)
            {
                var depth = roll < carelessShare + ShallowShare
                    ? 3 + (float)rng.NextDouble() * 13
                    : 25 + (float)rng.NextDouble() * 95;
                z = (band.Floor + 1) * FloorHeight - SlabThickness + depth - spec.HalfHeight;
            }

            var path = LanePath(spec, band, z, rng);
            band.Cursor = path.Max(p => p.Y) + spec.HalfWidth + ChainGap;
            BuildChain(b, spec, path, LevelName(band.Floor), rng);
        }
    }

    /// <summary>Цепочка вдоль X внутри ячейки с 0-2 уступами в плане в сторону свободной части полосы.</summary>
    private static List<Vector3> LanePath(MepSpec spec, Band band, float z, Random rng)
    {
        var bend = spec.Bend;
        var minLeg = 2 * bend + 300;
        var x = 3300 + band.Bay * Grid + 100 + (float)rng.NextDouble() * 150;
        var xEnd = 8700 + band.Bay * Grid - 100 - (float)rng.NextDouble() * 150;
        var y = band.Cursor + spec.HalfWidth;
        var path = new List<Vector3> { V(x, y, z) };
        var jogs = rng.Next(3);
        var offset = 0f;
        while (jogs > 0 && xEnd - x >= 2 * minLeg + 100)
        {
            var jog = 2 * bend + 100 + (float)rng.NextDouble() * 350;
            if (offset == 0 && y + jog + spec.HalfWidth > band.Limit) break;

            x += minLeg + (float)rng.NextDouble() * Math.Min(800, xEnd - x - 2 * minLeg);
            path.Add(V(x, y, z));
            if (offset == 0)
            {
                offset = jog;
                y += jog;
            }
            else
            {
                y -= offset;
                offset = 0;
            }

            path.Add(V(x, y, z));
            jogs--;
        }

        path.Add(V(xEnd, y, z));
        return path;
    }

    /// <summary>Центры шахт в плане, мм: пучки стояков всех разделов идут через них.</summary>
    private static readonly (float X, float Y)[] Shafts =
    [
        (4500, 4500), (4500, 28500), (4500, 52500), (16500, 16500), (16500, 40500), (28500, 4500),
        (28500, 28500), (28500, 52500), (40500, 16500), (40500, 40500), (52500, 4500), (52500, 28500)
    ];

    /// <summary>Пучки вертикальных стояков в шахтах: разделы делят шахты, ячейки сетки сдвинуты по индексу модели.</summary>
    private static void BuildRisers(ModelBuilder b, MepKind kind, Random rng)
    {
        var budget = b.Target * RiserPercent / 100;
        var pitch = kind == MepKind.Pipe ? 260f : 700f;
        var cols = kind == MepKind.Pipe ? 9 : 4;
        var shift = b.Index % 12 * 20 - 100;
        for (var counter = 0; budget > 0 && b.Remaining > 0; counter++)
        {
            var slot = counter / Shafts.Length;
            if (slot >= cols * cols) break;

            var (sx, sy) = Shafts[counter % Shafts.Length];
            var x = sx + (slot % cols - (cols - 1) / 2f) * pitch + shift;
            var y = sy + (slot / cols - (cols - 1) / 2f) * pitch + shift;
            var from = rng.Next(4);
            var to = 6 + rng.Next(4);
            var before = b.Remaining;
            BuildChain(b, RandomSpec(kind, rng), [V(x, y, from * FloorHeight + 100), V(x, y, (to + 1) * FloorHeight - 100)],
                LevelName(from), rng);
            budget -= before - b.Remaining;
        }
    }

    /// <summary>Свободная трасса: диагональная в плане или наклонная по высоте, из одного участка.</summary>
    private static List<Vector3> FreePath(MepSpec spec, Random rng, bool diagonal, out int floor)
    {
        floor = rng.Next(Floors);
        var angle = diagonal ? rng.NextDouble() * Math.PI : rng.Next(2) * Math.PI / 2;
        var length = 1800 + (float)rng.NextDouble() * 2000;
        var dx = length * (float)Math.Cos(angle);
        var dy = length * (float)Math.Sin(angle);
        var x = 1500 + (float)rng.NextDouble() * 57000;
        var y = 1500 + (float)rng.NextDouble() * 57000;
        if (x + dx is < 1000 or > 59000) dx = -dx;
        if (y + dy is < 1000 or > 59000) dy = -dy;
        var z = floor * FloorHeight + 2100 + spec.HalfHeight + (float)rng.NextDouble() * (1200 - 2 * spec.HalfHeight);
        var dz = 0f;
        if (!diagonal)
        {
            dz = length * (0.03f + (float)rng.NextDouble() * 0.03f);
            if (z - floor * FloorHeight > 2700) dz = -dz;
        }

        return [V(x, y, z), V(x + dx, y + dy, z + dz)];
    }

    /// <summary>Цепочка где попало: длинный горизонтальный участок, для труб ещё стояк через перекрытие.</summary>
    private static List<Vector3> CarelessPath(MepSpec spec, Random rng, out int floor)
    {
        floor = rng.Next(Floors);
        var alongX = rng.Next(2) == 0;
        var x = 1500 + (float)rng.NextDouble() * 57000;
        var y = 1500 + (float)rng.NextDouble() * 57000;
        var z = floor * FloorHeight + 2100 + (float)rng.NextDouble() * 1200;
        var length = 3500 + (float)rng.NextDouble() * 5500;
        var path = new List<Vector3> { V(x, y, z) };
        if (alongX) x += x + length < 59500 ? length : -length;
        else y += y + length < 59500 ? length : -length;
        path.Add(V(x, y, z));

        if (spec.Kind == MepKind.Pipe && rng.NextDouble() < 0.6)
        {
            var top = (floor + 1) * FloorHeight - SlabThickness + 250 + (float)rng.NextDouble() * 650;
            z = Math.Max(top, z + 2 * spec.Bend + 100);
            path.Add(V(x, y, z));
        }

        var turn = 2500 + (float)rng.NextDouble() * 2500;
        if (alongX) y += y + turn < 59500 ? turn : -turn;
        else x += x + turn < 59500 ? turn : -turn;
        path.Add(V(x, y, z));
        return path;
    }

    /// <summary>Строит элементы цепочки по ломаной: участки, отводы в углах, тройники на длинных участках труб.</summary>
    private static void BuildChain(ModelBuilder b, MepSpec spec, List<Vector3> path, string level, Random rng)
    {
        var parts = new List<ChainPart>();
        var bend = spec.Bend;
        var legs = path.Count - 1;
        for (var i = 0; i < legs; i++)
        {
            var dir = Vector3.Normalize(path[i + 1] - path[i]);
            var start = i > 0 ? path[i] + dir * bend : path[i];
            var end = i < legs - 1 ? path[i + 1] - dir * bend : path[i + 1];
            AddLeg(parts, spec, start, end, dir, rng);
            if (i < legs - 1)
            {
                var next = Vector3.Normalize(path[i + 2] - path[i + 1]);
                parts.Add(Elbow(spec, path[i + 1], dir, next, rng));
            }
        }

        b.AddChain(parts.Take(b.Remaining).ToList(), level);
    }

    /// <summary>Участок режется на трубы/короба не длиннее <see cref="MaxPieceLength"/>, между ними муфты; на трубах часть муфт - тройники.</summary>
    private static void AddLeg(List<ChainPart> parts, MepSpec spec, Vector3 start, Vector3 end, Vector3 dir, Random rng)
    {
        var length = Vector3.Distance(start, end);
        if (length < 20) return;

        var pieces = Math.Max(1, (int)MathF.Ceiling(length / MaxPieceLength));
        var couplingLength = spec.Kind == MepKind.Pipe ? Math.Max(4 * spec.Radius, 100) : 150;
        var teeLength = Math.Max(6 * spec.Radius, 250);
        var from = start;
        for (var k = 1; k <= pieces; k++)
        {
            var isLast = k == pieces;
            var joint = start + dir * (length * k / pieces);
            var isTee = !isLast && spec.Kind == MepKind.Pipe && rng.NextDouble() < 0.3;
            var half = (isTee ? teeLength : couplingLength) / 2;
            var to = isLast ? end : joint - dir * half;
            if (Vector3.Distance(from, to) >= 20) parts.Add(Straight(spec, from, to));
            if (isLast) break;

            parts.Add(isTee ? Tee(spec, joint, dir, teeLength, rng) : Coupling(spec, joint, dir, couplingLength, rng));
            from = joint + dir * half;
        }
    }

    private static ChainPart Coupling(MepSpec spec, Vector3 joint, Vector3 dir, float length, Random rng)
    {
        var target = spec.Kind switch
        {
            MepKind.Pipe => rng.Next(500, 1201),
            MepKind.Duct => rng.Next(300, 501),
            _ => rng.Next(100, 301)
        };
        var m = spec.Profile.Length;
        var rings = Math.Max(1, (target - 2 * m) / (2 * m));
        var mesh = MeshPrimitives.Straight(joint - dir * (length / 2), joint + dir * (length / 2), spec.Profile, rings);
        return spec.Kind switch
        {
            MepKind.Pipe => new ChainPart("OST_PipeFitting", "Муфта", $"Муфта {spec.Size:0}", mesh, spec.Size),
            MepKind.Duct => new ChainPart("OST_DuctFitting", "Соединение воздуховода", $"Соединение {spec.Width:0}x{spec.Height:0}", mesh, spec.Size),
            _ => new ChainPart("OST_CableTrayFitting", "Соединитель лотка", $"Соединитель {spec.Width:0}x{spec.Height:0}", mesh, spec.Size)
        };
    }

    private static ChainPart Straight(MepSpec spec, Vector3 a, Vector3 b)
    {
        var mesh = MeshPrimitives.Straight(a, b, spec.Profile);
        return spec.Kind switch
        {
            MepKind.Pipe => new ChainPart("OST_PipeCurves", "Труба", $"Труба {spec.Size:0}", mesh, spec.Size),
            MepKind.Duct => new ChainPart("OST_DuctCurves", "Воздуховод", $"Короб {spec.Width:0}x{spec.Height:0}", mesh, spec.Size),
            _ => new ChainPart("OST_CableTray", "Лоток", $"Лоток {spec.Width:0}x{spec.Height:0}", mesh, spec.Size)
        };
    }

    private static ChainPart Elbow(MepSpec spec, Vector3 corner, Vector3 d1, Vector3 d2, Random rng)
    {
        var m = spec.Profile.Length;
        var target = spec.Kind switch
        {
            MepKind.Pipe => rng.Next(500, 2001),
            MepKind.Duct => rng.Next(300, 801),
            _ => rng.Next(100, 301)
        };
        var steps = Math.Max(4, (target - 2 * m) / (2 * m));
        var mesh = MeshPrimitives.Elbow(corner, d1, d2, spec.Bend, spec.Profile, steps);
        return spec.Kind switch
        {
            MepKind.Pipe => new ChainPart("OST_PipeFitting", "Отвод", $"Отвод {spec.Size:0}", mesh, spec.Size),
            MepKind.Duct => new ChainPart("OST_DuctFitting", "Отвод воздуховода", $"Отвод {spec.Width:0}x{spec.Height:0}", mesh, spec.Size),
            _ => new ChainPart("OST_CableTrayFitting", "Угол лотка", $"Угол {spec.Width:0}x{spec.Height:0}", mesh, spec.Size)
        };
    }

    private static ChainPart Tee(MepSpec spec, Vector3 mid, Vector3 dir, float length, Random rng)
    {
        var target = rng.Next(500, 2001);
        var m = spec.Profile.Length;
        var rings = Math.Max(1, (target / 2 - 2 * m) / (2 * m));
        var side = rng.Next(3) switch
        {
            0 => Vector3.UnitZ,
            1 => -Vector3.UnitZ,
            _ => Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, dir))
        };
        var main = MeshPrimitives.Straight(mid - dir * (length / 2), mid + dir * (length / 2), spec.Profile, rings);
        var branch = MeshPrimitives.Straight(mid, mid + side * (length * 0.75f), MeshPrimitives.Circle(m, spec.Radius * 0.8f), rings);
        return new ChainPart("OST_PipeFitting", "Тройник", $"Тройник {spec.Size:0}", MeshPrimitives.Merge(main, branch), spec.Size);
    }

    // ---------------------------------------------------------------- сборка модели

    private sealed class ModelBuilder
    {
        private readonly string _key;
        private readonly int _modelIndex;
        private readonly Vector3 _delta;
        private readonly List<ElementGeometry> _elements = [];
        private readonly Dictionary<string, IReadOnlyList<ParameterValue>> _parameters = [];
        private readonly ModelHeader _header;
        private int _next;

        public ModelBuilder(string key, string discipline, int modelIndex, (double X, double Y, double Z) origin, int target)
        {
            _key = key;
            Discipline = discipline;
            _modelIndex = modelIndex;
            Target = target;
            _delta = new Vector3(
                (float)(Origins[0].X - origin.X),
                (float)(Origins[0].Y - origin.Y),
                (float)(Origins[0].Z - origin.Z));
            _header = new ModelHeader(key, $"{key}.rvt", 2024, DateTimeOffset.UnixEpoch, true, "{3D}", origin.X, origin.Y, origin.Z);
        }

        public int Index => _modelIndex;

        public string Discipline { get; }

        public int Target { get; }

        public int Remaining => Target - _elements.Count;

        public void Add(string category, string family, string type, string level, ElementMesh mesh) =>
            AddElement(NewId(), category, family, type, level, mesh, [], null);

        /// <summary>Добавляет цепочку: соседние части связаны взаимно через ConnectedUniqueIds.</summary>
        public void AddChain(IReadOnlyList<ChainPart> parts, string level)
        {
            var ids = parts.Select(_ => NewId()).ToArray();
            for (var i = 0; i < parts.Count; i++)
            {
                var connected = new List<string>(2);
                if (i > 0) connected.Add(ids[i - 1]);
                if (i < parts.Count - 1) connected.Add(ids[i + 1]);
                AddElement(ids[i], parts[i].Category, parts[i].Family, parts[i].Type, level, parts[i].Mesh, connected, parts[i].Size);
            }
        }

        public (ModelGeometry, ModelParameters, string) Finish() =>
            (new ModelGeometry(_header, _elements, []), new ModelParameters(_parameters), Discipline);

        private string NewId() => $"{_key}-{_next++:x7}";

        private void AddElement(string id, string category, string family, string type, string level, ElementMesh mesh,
            IReadOnlyList<string> connected, double? size)
        {
            var positions = mesh.Positions;
            for (var i = 0; i < positions.Length; i += 3)
            {
                positions[i] += _delta.X;
                positions[i + 1] += _delta.Y;
                positions[i + 2] += _delta.Z;
            }

            var info = new ElementInfo(id, (_modelIndex + 1) * 10_000_000L + _elements.Count + 1, category, family, type, level, null, connected);
            _elements.Add(new ElementGeometry(info, mesh, Aabb.FromPositions(positions), Fingerprint.Compute(mesh, _header)));
            if (size is { } s)
                _parameters[id] = [new ParameterValue("Размер", null, null, s)];
        }
    }
}
