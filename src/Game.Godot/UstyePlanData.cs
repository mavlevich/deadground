using System.IO;
using System.Text.Json;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Parsed plan geometry and height evaluation for Ustye map preview.
/// Loaded from packaged resource res://Maps/ustye/layout.json with filesystem fallback.
/// Single editable source remains docs/maps/ustye/layout.json.
/// Derived Catmull-Rom splines provide smooth river and coastline geometry.
/// </summary>
public sealed class UstyePlanData
{
    public sealed record Peak(float CenterX, float CenterZ, float Amplitude, float SigmaX, float SigmaZ);

    public sealed record Crossing(
        string Id,
        string RoadId,
        Vector2 Center,
        Vector2 EndA,
        Vector2 EndB,
        float DeckWidth,
        string Notes);

    public sealed record Road(string Id, string Name, string Mode, float Width, Vector2[] Points);

    public sealed record Structure(
        string Id,
        string Kind,
        string RegionId,
        Vector2 Min,
        Vector2 Size,
        float Height);

    public sealed record Site(
        string Id,
        string Label,
        string Name,
        Vector2 Position,
        string Kind,
        float GroundHeightTarget,
        float PlatformHeightTarget,
        string Note);

    public sealed record Region(string Id, string Kind, Vector2[] Polygon);

    public float ExtentX { get; } = 5000f;
    public float ExtentZ { get; } = 5000f;
    public float BaseHeight { get; } = 4f;
    public Peak[] Peaks { get; }
    public Vector2[] River { get; }
    public Vector2[] RiverSpline { get; }
    public float RiverWidth { get; } = 100f;
    public float BankSlope { get; } = 24f;
    public Vector2[] Bay { get; }
    public Vector2[] BayCoastSpline { get; }
    public Vector2[] SmoothedBayPolygon { get; }
    public float RiverMinX { get; }
    public float RiverMaxX { get; }
    public Crossing[] Crossings { get; }
    public Road[] Roads { get; }
    public Road[] AccessRoads { get; }
    public Structure[] Structures { get; }
    public Site[] Sites { get; }
    public Region[] Regions { get; }

    public static string ResolvePlanPath()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "Maps/ustye/layout.json")),
            Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "../../docs/maps/ustye/layout.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "docs/maps/ustye/layout.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src/Game.Godot/Maps/ustye/layout.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "docs/maps/ustye/layout.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Maps/ustye/layout.json")),
            Path.GetFullPath("docs/maps/ustye/layout.json"),
            Path.GetFullPath("src/Game.Godot/Maps/ustye/layout.json")
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException($"Cannot find Ustye layout.json. Checked: {string.Join(", ", candidates)}");
    }

    public static UstyePlanData Load()
    {
        const string resPath = "res://Maps/ustye/layout.json";
        if (Godot.FileAccess.FileExists(resPath))
        {
            string resJson = Godot.FileAccess.GetFileAsString(resPath);
            if (!string.IsNullOrEmpty(resJson))
            {
                return Parse(resJson);
            }
        }

        string path = ResolvePlanPath();
        string json = File.ReadAllText(path);
        return Parse(json);
    }

    public static UstyePlanData Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        float extentX = 5000f;
        float extentZ = 5000f;
        if (root.TryGetProperty("extent_m", out JsonElement extentEl) && extentEl.GetArrayLength() == 2)
        {
            extentX = extentEl[0].GetSingle();
            extentZ = extentEl[1].GetSingle();
        }

        JsonElement heightModelEl = root.GetProperty("height_model");
        float baseHeight = heightModelEl.GetProperty("base_m").GetSingle();
        List<Peak> peaks = [];
        foreach (JsonElement peakEl in heightModelEl.GetProperty("peaks").EnumerateArray())
        {
            JsonElement centerEl = peakEl.GetProperty("center_m");
            peaks.Add(new Peak(
                centerEl[0].GetSingle(),
                centerEl[1].GetSingle(),
                peakEl.GetProperty("amplitude_m").GetSingle(),
                peakEl.GetProperty("sigma_x_m").GetSingle(),
                peakEl.GetProperty("sigma_z_m").GetSingle()));
        }

        JsonElement waterEl = root.GetProperty("water");
        List<Vector2> river = [];
        foreach (JsonElement pt in waterEl.GetProperty("river").EnumerateArray())
        {
            river.Add(new Vector2(pt[0].GetSingle(), pt[1].GetSingle()));
        }
        float riverWidth = waterEl.GetProperty("river_width_m").GetSingle();
        if (!float.IsFinite(riverWidth) || riverWidth <= 0f)
        {
            throw new InvalidDataException($"Invalid river_width_m '{riverWidth}'. Must be a positive finite number.");
        }

        float bankSlope = waterEl.TryGetProperty("bank_slope_m", out JsonElement bsEl) ? bsEl.GetSingle() : 24f;
        if (!float.IsFinite(bankSlope) || bankSlope <= 0f)
        {
            throw new InvalidDataException($"Invalid bank_slope_m '{bankSlope}'. Must be a positive finite number.");
        }

        List<Vector2> bay = [];
        foreach (JsonElement pt in waterEl.GetProperty("bay").EnumerateArray())
        {
            bay.Add(new Vector2(pt[0].GetSingle(), pt[1].GetSingle()));
        }

        List<Crossing> crossings = [];
        foreach (JsonElement cr in root.GetProperty("crossings").EnumerateArray())
        {
            JsonElement centerEl = cr.GetProperty("center");
            JsonElement deckEl = cr.GetProperty("deck_endpoints");
            crossings.Add(new Crossing(
                cr.GetProperty("id").GetString()!,
                cr.GetProperty("road_id").GetString()!,
                new Vector2(centerEl[0].GetSingle(), centerEl[1].GetSingle()),
                new Vector2(deckEl[0][0].GetSingle(), deckEl[0][1].GetSingle()),
                new Vector2(deckEl[1][0].GetSingle(), deckEl[1][1].GetSingle()),
                cr.GetProperty("deck_width_m").GetSingle(),
                cr.TryGetProperty("notes", out JsonElement noteEl) ? noteEl.GetString() ?? "" : ""));
        }

        List<Road> roads = [];
        foreach (JsonElement roadEl in root.GetProperty("roads").EnumerateArray())
        {
            string roadId = roadEl.GetProperty("id").GetString()!;
            if (!roadEl.TryGetProperty("width_m", out JsonElement wEl))
            {
                throw new InvalidDataException($"Road '{roadId}' is missing 'width_m'.");
            }
            float width = wEl.GetSingle();
            if (!float.IsFinite(width) || width <= 0f)
            {
                throw new InvalidDataException($"Road '{roadId}' has invalid width '{width}'. Must be a positive finite number.");
            }

            List<Vector2> pts = [];
            foreach (JsonElement pt in roadEl.GetProperty("points").EnumerateArray())
            {
                pts.Add(new Vector2(pt[0].GetSingle(), pt[1].GetSingle()));
            }
            roads.Add(new Road(
                roadId,
                roadEl.GetProperty("name").GetString()!,
                roadEl.GetProperty("mode").GetString()!,
                width,
                pts.ToArray()));
        }

        List<Road> accessRoads = [];
        if (root.TryGetProperty("access_roads", out JsonElement accessEl))
        {
            foreach (JsonElement roadEl in accessEl.EnumerateArray())
            {
                string accessId = roadEl.GetProperty("id").GetString()!;
                if (!roadEl.TryGetProperty("width_m", out JsonElement wEl))
                {
                    throw new InvalidDataException($"Access road '{accessId}' is missing 'width_m'.");
                }
                float width = wEl.GetSingle();
                if (!float.IsFinite(width) || width <= 0f)
                {
                    throw new InvalidDataException($"Access road '{accessId}' has invalid width '{width}'. Must be a positive finite number.");
                }

                List<Vector2> pts = [];
                foreach (JsonElement pt in roadEl.GetProperty("points").EnumerateArray())
                {
                    pts.Add(new Vector2(pt[0].GetSingle(), pt[1].GetSingle()));
                }
                accessRoads.Add(new Road(
                    accessId,
                    accessId,
                    "access",
                    width,
                    pts.ToArray()));
            }
        }

        List<Structure> structures = [];
        foreach (JsonElement stEl in root.GetProperty("structures").EnumerateArray())
        {
            JsonElement minEl = stEl.GetProperty("min");
            JsonElement sizeEl = stEl.GetProperty("size");
            structures.Add(new Structure(
                stEl.GetProperty("id").GetString()!,
                stEl.GetProperty("kind").GetString()!,
                stEl.TryGetProperty("region_id", out JsonElement rId) ? rId.GetString() ?? "" : "",
                new Vector2(minEl[0].GetSingle(), minEl[1].GetSingle()),
                new Vector2(sizeEl[0].GetSingle(), sizeEl[1].GetSingle()),
                stEl.GetProperty("height_m").GetSingle()));
        }

        List<Site> sites = [];
        foreach (JsonElement siteEl in root.GetProperty("sites").EnumerateArray())
        {
            JsonElement posEl = siteEl.GetProperty("position");
            float gTarget = siteEl.TryGetProperty("ground_height_target_m", out JsonElement gh) ? gh.GetSingle() : 0f;
            float pTarget = siteEl.TryGetProperty("platform_height_target_m", out JsonElement ph) ? ph.GetSingle() : 0f;
            string note = siteEl.TryGetProperty("note", out JsonElement nEl) ? nEl.GetString() ?? "" : "";
            sites.Add(new Site(
                siteEl.GetProperty("id").GetString()!,
                siteEl.GetProperty("label").GetString()!,
                siteEl.GetProperty("name").GetString()!,
                new Vector2(posEl[0].GetSingle(), posEl[1].GetSingle()),
                siteEl.GetProperty("kind").GetString()!,
                gTarget,
                pTarget,
                note));
        }

        List<Region> regions = [];
        foreach (JsonElement regEl in root.GetProperty("regions").EnumerateArray())
        {
            List<Vector2> poly = [];
            foreach (JsonElement pt in regEl.GetProperty("polygon").EnumerateArray())
            {
                poly.Add(new Vector2(pt[0].GetSingle(), pt[1].GetSingle()));
            }
            regions.Add(new Region(
                regEl.GetProperty("id").GetString()!,
                regEl.GetProperty("kind").GetString()!,
                poly.ToArray()));
        }

        return new UstyePlanData(
            extentX,
            extentZ,
            baseHeight,
            peaks.ToArray(),
            river.ToArray(),
            riverWidth,
            bankSlope,
            bay.ToArray(),
            crossings.ToArray(),
            roads.ToArray(),
            accessRoads.ToArray(),
            structures.ToArray(),
            sites.ToArray(),
            regions.ToArray());
    }

    private UstyePlanData(
        float extentX,
        float extentZ,
        float baseHeight,
        Peak[] peaks,
        Vector2[] river,
        float riverWidth,
        float bankSlope,
        Vector2[] bay,
        Crossing[] crossings,
        Road[] roads,
        Road[] accessRoads,
        Structure[] structures,
        Site[] sites,
        Region[] regions)
    {
        ExtentX = extentX;
        ExtentZ = extentZ;
        BaseHeight = baseHeight;
        Peaks = peaks;
        River = river;
        RiverWidth = riverWidth;
        BankSlope = bankSlope;
        Bay = bay;
        Crossings = crossings;
        Roads = roads;
        AccessRoads = accessRoads;
        Structures = structures;
        Sites = sites;
        Regions = regions;

        // Generate smooth Catmull-Rom splines passing exactly through declared points
        RiverSpline = GenerateCatmullRomSpline(river, 32);

        float rMinX = float.MaxValue;
        float rMaxX = float.MinValue;
        foreach (Vector2 p in RiverSpline)
        {
            if (p.X < rMinX) rMinX = p.X;
            if (p.X > rMaxX) rMaxX = p.X;
        }
        RiverMinX = rMinX;
        RiverMaxX = rMaxX;

        // Coastline portion of the bay polygon is points 0..5
        Vector2[] bayCoastPoints = bay.Length >= 6 ? bay[..6] : bay;
        BayCoastSpline = GenerateCatmullRomSpline(bayCoastPoints, 24);

        // Smoothed bay polygon: BayCoastSpline from [5000, 3300] to [3300, 5000],
        // then southeast corner [5000, 5000] closing back to [5000, 3300]
        SmoothedBayPolygon = [.. BayCoastSpline, new Vector2(5000f, 5000f)];
    }

    public static Vector2[] GenerateCatmullRomSpline(IReadOnlyList<Vector2> points, int subdivisionsPerSegment)
    {
        if (points.Count < 2) return points.ToArray();

        List<Vector2> result = [];
        int n = points.Count;

        for (int i = 0; i < n - 1; i++)
        {
            Vector2 p0 = points[Math.Max(0, i - 1)];
            Vector2 p1 = points[i];
            Vector2 p2 = points[i + 1];
            Vector2 p3 = points[Math.Min(n - 1, i + 2)];

            for (int step = 0; step < subdivisionsPerSegment; step++)
            {
                float t = (float)step / subdivisionsPerSegment;
                float t2 = t * t;
                float t3 = t2 * t;

                Vector2 pt = 0.5f * (
                    (2f * p1) +
                    (-p0 + p2) * t +
                    (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                    (-p0 + 3f * p1 - 3f * p2 + p3) * t3
                );
                result.Add(pt);
            }
        }
        result.Add(points[^1]);
        return result.ToArray();
    }

    /// <summary>
    /// Pure mathematical height before carving water features.
    /// Matches sample_height in tools/map-authoring/ustye_plan.py.
    /// </summary>
    public float SampleHeight(float x, float z)
    {
        float h = BaseHeight;
        foreach (Peak p in Peaks)
        {
            float dx = (x - p.CenterX) / p.SigmaX;
            float dz = (z - p.CenterZ) / p.SigmaZ;
            h += p.Amplitude * MathF.Exp(-(dx * dx + dz * dz));
        }
        return h;
    }

    /// <summary>
    /// Terrain elevation taking river channel and bay bed into account.
    /// River surface is at Y = 2.0 m; riverbed is carved below water level.
    /// Forms a clear profile: channel bed (0.8m) -> shallows shelf (0.8..1.95m) -> riparian bank (1.95..terrain).
    /// </summary>
    public float SampleTerrainElevation(float x, float z)
    {
        float h = SampleHeight(x, z);
        float riverInfluence = RiverWidth * 0.50f + BankSlope; // 50 + 24 = 74 m

        // 1. River corridor: only query polyline if x is within river influence bounding box
        if (x >= RiverMinX - riverInfluence && x <= RiverMaxX + riverInfluence)
        {
            float riverDist = DistanceToPolyline(new Vector2(x, z), RiverSpline);
            float halfWidth = RiverWidth * 0.50f;
            float channelFloor = RiverWidth * 0.32f;

            if (riverDist <= channelFloor)
            {
                // Flat riverbed under water surface (water Y = 2.0 m)
                h = 0.8f;
            }
            else if (riverDist <= halfWidth)
            {
                // Shallows shelf rising gently from 0.8m to 1.95m waterline
                float t = (riverDist - channelFloor) / (halfWidth - channelFloor);
                h = Mathf.Lerp(0.8f, 1.95f, Mathf.SmoothStep(0f, 1f, t));
            }
            else if (riverDist < halfWidth + BankSlope)
            {
                // Sloping river bank from 1.95m waterline up to natural terrain
                float t = (riverDist - halfWidth) / BankSlope;
                float bankTarget = MathF.Max(h, 2.4f);
                h = Mathf.Lerp(1.95f, bankTarget, Mathf.SmoothStep(0f, 1f, t));
            }
        }

        // 2. Bay basin in southeast corner: uses SmoothedBayPolygon and BayCoastSpline
        if (x > 3200f && z > 3200f)
        {
            Vector2 pt = new(x, z);
            bool inBay = PointInPolygon(pt, SmoothedBayPolygon);
            float bayDist = DistanceToPolyline(pt, BayCoastSpline);

            if (inBay)
            {
                // Inside bay: drop from 1.95m at coast down to 0.5m seabed over 45m
                float t = Math.Clamp(bayDist / 45f, 0f, 1f);
                float bayFloor = Mathf.Lerp(1.95f, 0.5f, Mathf.SmoothStep(0f, 1f, t));
                h = bayFloor;
            }
            else if (bayDist < 45f)
            {
                // Coastal landward slope rising from 1.95m waterline up to natural terrain
                float t = Math.Clamp(bayDist / 45f, 0f, 1f);
                float bankTarget = MathF.Max(h, 2.2f);
                h = Mathf.Lerp(1.95f, bankTarget, Mathf.SmoothStep(0f, 1f, t));
            }
        }

        return h;
    }

    public Vector3 SampleTerrainNormal(float x, float z, float step = 4.0f)
    {
        float hL = SampleTerrainElevation(x - step, z);
        float hR = SampleTerrainElevation(x + step, z);
        float hD = SampleTerrainElevation(x, z - step);
        float hU = SampleTerrainElevation(x, z + step);
        return new Vector3(hL - hR, 2f * step, hD - hU).Normalized();
    }

    public static float DistanceToSegmentSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 delta = b - a;
        float lenSq = delta.LengthSquared();
        if (lenSq < 1e-6f) return p.DistanceSquaredTo(a);
        float t = Math.Clamp((p - a).Dot(delta) / lenSq, 0f, 1f);
        Vector2 proj = a + delta * t;
        return p.DistanceSquaredTo(proj);
    }

    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        return MathF.Sqrt(DistanceToSegmentSquared(p, a, b));
    }

    public static float DistanceToPolyline(Vector2 p, IReadOnlyList<Vector2> polyline)
    {
        if (polyline.Count == 0) return float.MaxValue;
        if (polyline.Count == 1) return p.DistanceTo(polyline[0]);
        float minSq = float.MaxValue;
        for (int i = 0; i < polyline.Count - 1; i++)
        {
            Vector2 a = polyline[i];
            Vector2 b = polyline[i + 1];

            float minX = MathF.Min(a.X, b.X);
            float maxX = MathF.Max(a.X, b.X);
            float minY = MathF.Min(a.Y, b.Y);
            float maxY = MathF.Max(a.Y, b.Y);

            float dx = p.X < minX ? minX - p.X : p.X > maxX ? p.X - maxX : 0f;
            float dy = p.Y < minY ? minY - p.Y : p.Y > maxY ? p.Y - maxY : 0f;
            if (dx * dx + dy * dy >= minSq) continue;

            float dSq = DistanceToSegmentSquared(p, a, b);
            if (dSq < minSq) minSq = dSq;
        }
        return MathF.Sqrt(minSq);
    }

    public static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> polygon)
    {
        bool inside = false;
        int count = polygon.Count;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 a = polygon[i];
            Vector2 b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-9f) + a.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
