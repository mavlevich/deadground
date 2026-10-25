using System.Diagnostics;
using System.IO;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Standalone 3D visual preview and inspection mode for the 5000 x 5000 m Ustye map.
/// Built from packaged resource res://Maps/ustye/layout.json with filesystem fallback.
/// Single editable source remains docs/maps/ustye/layout.json.
/// </summary>
public partial class UstyeMapPreview : Node3D
{
    public sealed record CameraLandmark(
        string Id,
        string Name,
        Vector3 Focus,
        float Size,
        float Pitch,
        float Yaw);

    internal sealed class ScaleVehicle
    {
        public string Id { get; }
        public string? Faction { get; }
        internal VehicleVisual Visual { get; }
        public Vector3 GroundPosition { get; }
        public int ActiveLod { get; private set; }

        internal ScaleVehicle(string id, string? faction, VehicleVisual visual, Vector3 pos)
        {
            Id = id;
            Faction = faction;
            Visual = visual;
            GroundPosition = pos;
            ActiveLod = 0;
        }

        public void UpdateLod(float cameraSize)
        {
            int targetLod = cameraSize < UnitView.Lod1CameraSize ? 0 : cameraSize < UnitView.Lod2CameraSize ? 1 : 2;
            targetLod = Math.Clamp(targetLod, 0, Visual.LodCount - 1);
            if (targetLod == ActiveLod) return;

            Visual.GetOrCreateLod(ActiveLod).Visible = false;
            Visual.GetOrCreateLod(targetLod).Visible = true;
            ActiveLod = targetLod;
        }
    }

    private UstyePlanData _plan = null!;
    private Camera3D _camera = null!;
    private CameraRig _cameraRig = null!;
    private DirectionalLight3D _sun = null!;
    private WorldEnvironment _environment = null!;

    private Node3D _terrainRoot = null!;
    private Node3D _waterRoot = null!;
    private Node3D _roadsRoot = null!;
    private Node3D _structuresRoot = null!;
    private Node3D _landmarksRoot = null!;
    private Node3D _vegetationRoot = null!;
    private Node3D _vehiclesRoot = null!;
    private Node3D _contextRoot = null!;

    private Mesh _coniferTreeMesh = null!;
    private Mesh _deciduousTreeMesh = null!;
    private readonly List<MultiMeshInstance3D> _coniferMultiMeshes = [];
    private readonly List<MultiMeshInstance3D> _deciduousMultiMeshes = [];
    private readonly List<ScaleVehicle> _scaleVehicles = [];

    private CanvasLayer _hud = null!;
    private Label _titleLabel = null!;
    private Label _landmarkLabel = null!;
    private Label _telemetryLabel = null!;
    private Label _helpLabel = null!;

    private CameraLandmark[] _landmarks = [];
    private int _currentLandmarkIndex;
    private bool _isTransitioning;
    private Vector3 _transitionStartFocus;
    private Vector3 _transitionTargetFocus;
    private float _transitionStartSize;
    private float _transitionTargetSize;
    private float _transitionStartPitch;
    private float _transitionTargetPitch;
    private float _transitionStartYaw;
    private float _transitionTargetYaw;
    private double _transitionElapsed;
    private const double TransitionDuration = 0.8;

    // HUD telemetry throttling (4 Hz = every 0.25s)
    private double _telemetryTimer = 0.25;
    private int _lastTelemetryFocusX = int.MinValue;
    private int _lastTelemetryFocusZ = int.MinValue;
    private int _lastTelemetryElev = int.MinValue;
    private int _lastTelemetrySize = int.MinValue;
    private bool _lastTelemetryPerspective;
    private int _lastTelemetryFps = -1;

    private string? _screenshotPath;
    private double _screenshotDelay = 0.5;
    private bool _screenshotQuit;
    private string? _captureDir;
    private int _captureStep = -1;
    private double _captureTimer;

    // Benchmark state using monotonic wall clock
    private bool _runBenchmark;
    private int _benchmarkStage; // 0=none, 1=warmup overview, 2=collect overview, 3=warmup town, 4=collect town, 5=done
    private long _benchmarkStageStartTimestamp;
    private long _lastFrameTimestamp;
    private readonly List<double> _overviewFrameTimes = [];
    private readonly List<double> _townFrameTimes = [];
    private readonly List<double> _overviewCpuTimes = [];
    private readonly List<double> _townCpuTimes = [];
    private long _loadTimeMs;
    private ulong _overviewDrawCalls;
    private ulong _overviewObjects;
    private ulong _overviewPrimitives;
    private ulong _townDrawCalls;
    private ulong _townObjects;
    private ulong _townPrimitives;

    public UstyePlanData Plan => _plan;
    public Camera3D Camera => _camera;
    internal CameraRig Rig => _cameraRig;
    public CameraLandmark[] Landmarks => _landmarks;
    public Node3D TerrainRoot => _terrainRoot;
    public Node3D WaterRoot => _waterRoot;
    public Node3D RoadsRoot => _roadsRoot;
    public Node3D StructuresRoot => _structuresRoot;
    public Node3D LandmarksRoot => _landmarksRoot;
    public Node3D VehiclesRoot => _vehiclesRoot;
    public Mesh ConiferTreeMesh => _coniferTreeMesh;
    public Mesh DeciduousTreeMesh => _deciduousTreeMesh;
    public IReadOnlyList<MultiMeshInstance3D> ConiferMultiMeshes => _coniferMultiMeshes;
    public IReadOnlyList<MultiMeshInstance3D> DeciduousMultiMeshes => _deciduousMultiMeshes;
    public int TotalConiferCount => _coniferMultiMeshes.Sum(m => m.Multimesh?.InstanceCount ?? 0);
    public int TotalDeciduousCount => _deciduousMultiMeshes.Sum(m => m.Multimesh?.InstanceCount ?? 0);
    internal IReadOnlyList<ScaleVehicle> ScaleVehicles => _scaleVehicles;

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (_runBenchmark && what is (int)NotificationApplicationFocusOut or (int)NotificationWMWindowFocusOut)
        {
            Godot.Engine.MaxFps = 0;
        }
    }

    public override void _Ready()
    {
        Stopwatch sw = Stopwatch.StartNew();
        long t0 = sw.ElapsedMilliseconds;

        _plan = UstyePlanData.Load();
        long tPlan = sw.ElapsedMilliseconds - t0;

        ParseArgs();
        SetupEnvironment();
        SetupCamera();

        long tBeforeTerrain = sw.ElapsedMilliseconds;
        SetupTerrain();
        long tTerrain = sw.ElapsedMilliseconds - tBeforeTerrain;

        long tBeforeWater = sw.ElapsedMilliseconds;
        SetupWater();
        long tWater = sw.ElapsedMilliseconds - tBeforeWater;

        long tBeforeRoads = sw.ElapsedMilliseconds;
        SetupRoads();
        long tRoads = sw.ElapsedMilliseconds - tBeforeRoads;

        long tBeforeCross = sw.ElapsedMilliseconds;
        SetupCrossings();
        long tCross = sw.ElapsedMilliseconds - tBeforeCross;

        long tBeforeStruct = sw.ElapsedMilliseconds;
        SetupStructures();
        long tStruct = sw.ElapsedMilliseconds - tBeforeStruct;

        SetupLandmarkSites();

        long tBeforeVeg = sw.ElapsedMilliseconds;
        SetupVegetation();
        long tVeg = sw.ElapsedMilliseconds - tBeforeVeg;

        SetupScaleVehicles();
        SetupLocalContext();
        SetupHud();

        SelectLandmark(0, immediate: true);
        sw.Stop();
        _loadTimeMs = sw.ElapsedMilliseconds;

        GD.Print(FormattableString.Invariant(
            $"UstyeMapPreview ready in {_loadTimeMs} ms (Plan={tPlan}ms, Terrain={tTerrain}ms, Water={tWater}ms, Roads={tRoads}ms, Crossings={tCross}ms, Structures={tStruct}ms, Veg={tVeg}ms)"));

        if (_runBenchmark)
        {
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Godot.Engine.MaxFps = 0;

            _benchmarkStage = 1;
            _benchmarkStageStartTimestamp = Stopwatch.GetTimestamp();
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            _overviewFrameTimes.Clear();
            _townFrameTimes.Clear();
            _overviewCpuTimes.Clear();
            _townCpuTimes.Clear();
        }
    }

    private void ParseArgs()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        string[] allArgs = OS.GetCmdlineArgs();
        string[] args = [.. userArgs, .. allArgs];

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--screenshot" && i + 1 < args.Length)
            {
                _screenshotPath = args[i + 1];
                i++;
            }
            else if (args[i] == "--screenshot-delay" && i + 1 < args.Length &&
                     double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double d))
            {
                _screenshotDelay = d;
                i++;
            }
            else if (args[i] == "--screenshot-quit")
            {
                _screenshotQuit = true;
            }
            else if (args[i] == "--map-preview-capture-all" && i + 1 < args.Length)
            {
                _captureDir = args[i + 1];
                _captureStep = 0;
                _captureTimer = 0.5;
                i++;
            }
            else if (args[i] == "--benchmark")
            {
                _runBenchmark = true;
            }
        }
    }

    private void SetupEnvironment()
    {
        // Neutral Cold War daylight: warm directional sun with cool sky fill
        _sun = new DirectionalLight3D
        {
            Name = "Sun",
            LightColor = new Color("fff3e3"),
            LightEnergy = 1.22f,
            ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 1800f,
            DirectionalShadowPancakeSize = 15f,
            RotationDegrees = new Vector3(-48f, -40f, 0f)
        };
        AddChild(_sun);

        Godot.Environment env = new()
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky
            {
                SkyMaterial = new ProceduralSkyMaterial
                {
                    SkyTopColor = new Color("527396"),
                    SkyHorizonColor = new Color("8d9fae"),
                    GroundBottomColor = new Color("22241e"),
                    GroundHorizonColor = new Color("7f8d7c"),
                    SunAngleMax = 30f
                }
            },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("708ba4"),
            AmbientLightEnergy = 0.48f,
            FogEnabled = true,
            FogMode = Godot.Environment.FogModeEnum.Exponential,
            FogDensity = 0.00008f,
            FogLightColor = new Color("8fa2b2"),
            FogLightEnergy = 0.42f
        };

        _environment = new WorldEnvironment
        {
            Name = "Environment",
            Environment = env
        };
        AddChild(_environment);
    }

    private void SetupCamera()
    {
        _camera = new Camera3D
        {
            Name = "PreviewCamera",
            Current = true,
            Near = 1f,
            Far = 10000f,
            Fov = 55f
        };
        AddChild(_camera);

        Vector3 initialFocus = new(2500f, _plan.SampleTerrainElevation(2500f, 2500f), 2500f);
        _cameraRig = new CameraRig(_camera, initialFocus, 4500f, _plan.ExtentX, _plan.ExtentZ);
        _cameraRig.Model.SetSizeLimits(30f, 5500f);
        _cameraRig.FarClipMargin = 3000f;
        _cameraRig.GroundHeight = (x, z) => _plan.SampleTerrainElevation(x, z);

        _landmarks =
        [
            new("overview", "1: Map Overview", new Vector3(2500f, 12f, 2500f), 4600f, 0.90f, 0f),
            new("town", "2: Riverside Town & Central Bridge", new Vector3(2600f, 4.5f, 2450f), 380f, 0.62f, -0.40f),
            new("church", "3: Church Ridge (OBS-W)", new Vector3(1850f, 35f, 1450f), 220f, 0.68f, 0.35f),
            new("sawmill", "4: Sawmill & Water Tower (TIMBER)", new Vector3(2530f, 4.0f, 3680f), 240f, 0.65f, -0.55f),
            new("radio", "5: Radio Hill (OBS-E)", new Vector3(3750f, 45f, 3370f), 240f, 0.68f, -0.4f),
            new("lighthouse", "6: Coastal Lighthouse (LIGHT)", new Vector3(4380f, 10f, 3950f), 220f, 0.65f, 0.5f),
            new("cross_n", "7: Northern Crossing (CROSS-N)", new Vector3(2400f, 4.2f, 1000f), 380f, 0.60f, -0.55f),
            new("cross_s", "8: Southern Crossing (CROSS-S)", new Vector3(2860f, 4.2f, 3550f), 220f, 0.72f, -0.25f),
            new("depot", "9: Ammo Depot (DEPOT)", new Vector3(3510f, 4f, 2730f), 250f, 0.70f, 0.2f),
            new("entry_a", "0: Southwest Entry (ENTRY-A)", new Vector3(500f, 4f, 4400f), 280f, 0.75f, 0f)
        ];
    }

    internal bool IsRiparianChunk(int cx, int cz)
    {
        // 500 x 500 m chunk bounding box
        float minX = cx * 500f;
        float maxX = (cx + 1) * 500f;
        float minZ = cz * 500f;
        float maxZ = (cz + 1) * 500f;

        Vector2[] testPts =
        [
            new(minX, minZ), new(maxX, minZ),
            new(minX, maxZ), new(maxX, maxZ),
            new((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f)
        ];

        foreach (Vector2 p in testPts)
        {
            if (UstyePlanData.DistanceToPolyline(p, _plan.RiverSpline) < 260f)
                return true;
            if (UstyePlanData.DistanceToPolyline(p, _plan.BayCoastSpline) < 260f)
                return true;
            if (p.X > 3200f && p.Y > 3200f)
                return true;
        }

        return false;
    }

    private static float FastHash2D(int x, int z)
    {
        uint h = (uint)(x * 374761393 + z * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return (h & 0x00FFFFFF) / 16777216.0f;
    }

    public static float SmoothNoise2D(float x, float z, float frequency)
    {
        float fx = x * frequency;
        float fz = z * frequency;
        int ix = (int)MathF.Floor(fx);
        int iz = (int)MathF.Floor(fz);
        float tx = fx - ix;
        float tz = fz - iz;

        // Smooth cubic Hermite interpolation
        float sx = tx * tx * (3f - 2f * tx);
        float sz = tz * tz * (3f - 2f * tz);

        float n00 = FastHash2D(ix, iz);
        float n10 = FastHash2D(ix + 1, iz);
        float n01 = FastHash2D(ix, iz + 1);
        float n11 = FastHash2D(ix + 1, iz + 1);

        float nx0 = Mathf.Lerp(n00, n10, sx);
        float nx1 = Mathf.Lerp(n01, n11, sx);
        return Mathf.Lerp(nx0, nx1, sz);
    }

    private Color EvaluateBiomeColor(float wx, float wz, float wy, Vector3 norm, Vector3 lightDir)
    {
        Vector2 pt = new(wx, wz);
        float halfWidth = _plan.RiverWidth * 0.50f;

        // 1. Water corridor & shoreline
        if (wy <= 1.95f)
        {
            // Submerged bed / shallows sand & dark silt
            return new Color("3f3b30");
        }

        if (wx >= _plan.RiverMinX - (halfWidth + 35f) && wx <= _plan.RiverMaxX + (halfWidth + 35f))
        {
            float rDist = UstyePlanData.DistanceToPolyline(pt, _plan.RiverSpline);
            if (rDist < halfWidth + 30f && wy <= 3.2f)
            {
                // Wet riparian verge & reeds
                float t = Math.Clamp((wy - 1.95f) / 1.25f, 0f, 1f);
                return new Color("35482e").Lerp(new Color("3b4e2f"), t);
            }
        }

        // 2. Industrial / logistics yards
        // Sawmill yard around TIMBER / ACCESS-MILL
        if (wx >= 2420f && wx <= 2650f && wz >= 3550f && wz <= 3780f)
        {
            float dCenter = pt.DistanceTo(new Vector2(2530f, 3680f));
            if (dCenter < 110f)
            {
                float t = Math.Clamp(dCenter / 110f, 0f, 1f);
                return new Color("4e4233").Lerp(new Color("3b4e2f"), t * t);
            }
        }

        // Ammo depot yard around DEPOT / ACCESS-DEPOT
        if (wx >= 3410f && wx <= 3660f && wz >= 2620f && wz <= 2860f)
        {
            float dCenter = pt.DistanceTo(new Vector2(3510f, 2730f));
            if (dCenter < 120f)
            {
                float t = Math.Clamp(dCenter / 120f, 0f, 1f);
                return new Color("484742").Lerp(new Color("3b4e2f"), t * t);
            }
        }

        // 3. Residential town courtyards (near panel blocks in Town)
        if (wx >= 2450f && wx <= 2850f && wz >= 2350f && wz <= 2650f)
        {
            foreach (UstyePlanData.Structure st in _plan.Structures)
            {
                if (st.Kind == "panel_block")
                {
                    if (wx >= st.Min.X - 16f && wx <= st.Min.X + st.Size.X + 16f &&
                        wz >= st.Min.Y - 16f && wz <= st.Min.Y + st.Size.Y + 16f)
                    {
                        return new Color("505355").Lerp(new Color("3f4733"), 0.35f);
                    }
                }
            }
        }

        // 4. Forest floor
        foreach (UstyePlanData.Region reg in _plan.Regions)
        {
            if (UstyePlanData.PointInPolygon(pt, reg.Polygon))
            {
                if (reg.Kind == "dense_forest")
                {
                    return new Color("2c2a22"); // Pine needle mulch and dark loam
                }
                if (reg.Kind == "sparse_forest")
                {
                    return new Color("3d482c"); // Woodland soil & grass
                }
            }
        }

        // 5. Road shoulders (within 3.5m of road ribbon edge)
        foreach (UstyePlanData.Road road in _plan.Roads)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (Vector2 p in road.Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minZ) minZ = p.Y;
                if (p.Y > maxZ) maxZ = p.Y;
            }
            float buf = road.Width * 0.5f + 4.0f;
            if (wx >= minX - buf && wx <= maxX + buf && wz >= minZ - buf && wz <= maxZ + buf)
            {
                float d = UstyePlanData.DistanceToPolyline(pt, road.Points);
                if (d < buf)
                {
                    return new Color("4c4639"); // Compacted gravel shoulder
                }
            }
        }

        // 6. Natural Baltic moraine meadow with smooth multi-octave macro-variation
        // Continuous Hermite noise: primary wavelength 350m, secondary wavelength 140m
        float n1 = SmoothNoise2D(wx, wz, 1f / 350f);
        float n2 = SmoothNoise2D(wx, wz, 1f / 140f);
        float macro = 0.70f * n1 + 0.30f * n2;

        Color coolMeadow = new("384a2c");
        Color dryRye = new("465031");
        Color richPasture = new("314124");

        Color meadowBase = macro < 0.50f
            ? coolMeadow.Lerp(dryRye, macro / 0.50f)
            : dryRye.Lerp(richPasture, (macro - 0.50f) / 0.50f);

        // Moraine ridges & peaks: exposed sandy gravel loam
        if (wy > 14f)
        {
            float ridgeFactor = Math.Clamp((wy - 14f) / 26f, 0f, 1f);
            meadowBase = meadowBase.Lerp(new Color("5a5c43"), ridgeFactor);
        }

        float shade = Mathf.Clamp(0.55f + 0.45f * norm.Dot(lightDir), 0.35f, 1.0f);
        return new Color(meadowBase.R * shade, meadowBase.G * shade, meadowBase.B * shade, 1f);
    }

    private void SetupTerrain()
    {
        _terrainRoot = new Node3D { Name = "TerrainRoot" };
        AddChild(_terrainRoot);

        // Multi-resolution terrain: 10 x 10 chunks (500 x 500 m).
        // Inland chunks: step = 25.0 m (20 x 20 quads, 800 triangles).
        // Riparian/water chunks: step = 6.25 m (80 x 80 quads, 12,800 triangles).
        // Shared boundaries between fine and coarse chunks are linearly stitched
        // with exact vertex coincidence, eliminating all gaps and normal tears.
        const float chunkSize = 500f;
        int chunkCols = (int)(_plan.ExtentX / chunkSize);
        int chunkRows = (int)(_plan.ExtentZ / chunkSize);

        StandardMaterial3D terrainMaterial = new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
            VertexColorUseAsAlbedo = true,
            Roughness = 0.95f,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled
        };

        Vector3 lightDir = new Vector3(-0.55f, 0.75f, -0.35f).Normalized();

        for (int cz = 0; cz < chunkRows; cz++)
        {
            for (int cx = 0; cx < chunkCols; cx++)
            {
                bool isFine = IsRiparianChunk(cx, cz);
                float step = isFine ? 6.25f : 25.0f;
                int cellsPerChunk = (int)(chunkSize / step);

                float originX = cx * chunkSize;
                float originZ = cz * chunkSize;

                int vCols = cellsPerChunk + 1;
                int vRows = cellsPerChunk + 1;
                Vector3[] vertices = new Vector3[vCols * vRows];
                Vector3[] normals = new Vector3[vertices.Length];
                Color[] colors = new Color[vertices.Length];
                int[] indices = new int[cellsPerChunk * cellsPerChunk * 6];

                // Check if neighbors are coarse to stitch boundaries
                bool coarseWest = isFine && cx > 0 && !IsRiparianChunk(cx - 1, cz);
                bool coarseEast = isFine && cx < chunkCols - 1 && !IsRiparianChunk(cx + 1, cz);
                bool coarseSouth = isFine && cz > 0 && !IsRiparianChunk(cx, cz - 1);
                bool coarseNorth = isFine && cz < chunkRows - 1 && !IsRiparianChunk(cx, cz + 1);

                // 1. Pre-evaluate heights across the chunk grid
                float[,] heights = new float[vCols, vRows];
                for (int lz = 0; lz <= cellsPerChunk; lz++)
                {
                    float wz = originZ + lz * step;
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        float wx = originX + lx * step;
                        heights[lx, lz] = _plan.SampleTerrainElevation(wx, wz);
                    }
                }

                // 2. Seamless border stitching for fine chunks adjacent to coarse neighbors
                if (coarseWest)
                {
                    for (int lz = 0; lz <= cellsPerChunk; lz++)
                    {
                        int k = lz / 4;
                        int rem = lz % 4;
                        float y0 = _plan.SampleTerrainElevation(originX, originZ + k * 25f);
                        float y1 = _plan.SampleTerrainElevation(originX, originZ + (k + 1) * 25f);
                        heights[0, lz] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }
                if (coarseEast)
                {
                    for (int lz = 0; lz <= cellsPerChunk; lz++)
                    {
                        int k = lz / 4;
                        int rem = lz % 4;
                        float y0 = _plan.SampleTerrainElevation(originX + chunkSize, originZ + k * 25f);
                        float y1 = _plan.SampleTerrainElevation(originX + chunkSize, originZ + (k + 1) * 25f);
                        heights[cellsPerChunk, lz] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }
                if (coarseSouth)
                {
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        int k = lx / 4;
                        int rem = lx % 4;
                        float y0 = _plan.SampleTerrainElevation(originX + k * 25f, originZ);
                        float y1 = _plan.SampleTerrainElevation(originX + (k + 1) * 25f, originZ);
                        heights[lx, 0] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }
                if (coarseNorth)
                {
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        int k = lx / 4;
                        int rem = lx % 4;
                        float y0 = _plan.SampleTerrainElevation(originX + k * 25f, originZ + chunkSize);
                        float y1 = _plan.SampleTerrainElevation(originX + (k + 1) * 25f, originZ + chunkSize);
                        heights[lx, cellsPerChunk] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }

                // 3. Assemble vertices, normals, and biome colors
                for (int lz = 0; lz <= cellsPerChunk; lz++)
                {
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        int idx = lz * vCols + lx;
                        float wx = originX + lx * step;
                        float wz = originZ + lz * step;
                        float wy = heights[lx, lz];

                        // Compute normal from fast cached grid heights
                        float hL = lx > 0 ? heights[lx - 1, lz] : _plan.SampleTerrainElevation(wx - step, wz);
                        float hR = lx < cellsPerChunk ? heights[lx + 1, lz] : _plan.SampleTerrainElevation(wx + step, wz);
                        float hD = lz > 0 ? heights[lx, lz - 1] : _plan.SampleTerrainElevation(wx, wz - step);
                        float hU = lz < cellsPerChunk ? heights[lx, lz + 1] : _plan.SampleTerrainElevation(wx, wz + step);
                        Vector3 norm = new Vector3(hL - hR, 2f * step, hD - hU).Normalized();

                        if (coarseWest && lx == 0)
                        {
                            int k = lz / 4;
                            int rem = lz % 4;
                            Vector3 n0 = _plan.SampleTerrainNormal(originX, originZ + k * 25f, 25f);
                            Vector3 n1 = _plan.SampleTerrainNormal(originX, originZ + (k + 1) * 25f, 25f);
                            norm = n0.Lerp(n1, rem / 4f).Normalized();
                        }
                        else if (coarseEast && lx == cellsPerChunk)
                        {
                            int k = lz / 4;
                            int rem = lz % 4;
                            Vector3 n0 = _plan.SampleTerrainNormal(originX + chunkSize, originZ + k * 25f, 25f);
                            Vector3 n1 = _plan.SampleTerrainNormal(originX + chunkSize, originZ + (k + 1) * 25f, 25f);
                            norm = n0.Lerp(n1, rem / 4f).Normalized();
                        }
                        else if (coarseSouth && lz == 0)
                        {
                            int k = lx / 4;
                            int rem = lx % 4;
                            Vector3 n0 = _plan.SampleTerrainNormal(originX + k * 25f, originZ, 25f);
                            Vector3 n1 = _plan.SampleTerrainNormal(originX + (k + 1) * 25f, originZ, 25f);
                            norm = n0.Lerp(n1, rem / 4f).Normalized();
                        }
                        else if (coarseNorth && lz == cellsPerChunk)
                        {
                            int k = lx / 4;
                            int rem = lx % 4;
                            Vector3 n0 = _plan.SampleTerrainNormal(originX + k * 25f, originZ + chunkSize, 25f);
                            Vector3 n1 = _plan.SampleTerrainNormal(originX + (k + 1) * 25f, originZ + chunkSize, 25f);
                            norm = n0.Lerp(n1, rem / 4f).Normalized();
                        }

                        vertices[idx] = new Vector3(wx, wy, wz);
                        normals[idx] = norm;
                        colors[idx] = EvaluateBiomeColor(wx, wz, wy, norm, lightDir);
                    }
                }

                int iOffset = 0;
                for (int lz = 0; lz < cellsPerChunk; lz++)
                {
                    for (int lx = 0; lx < cellsPerChunk; lx++)
                    {
                        int a = lz * vCols + lx;
                        int b = a + 1;
                        int c = a + vCols;
                        int d = c + 1;

                        indices[iOffset++] = a;
                        indices[iOffset++] = b;
                        indices[iOffset++] = c;

                        indices[iOffset++] = b;
                        indices[iOffset++] = d;
                        indices[iOffset++] = c;
                    }
                }

                using Godot.Collections.Array arrays = new();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                arrays[(int)Mesh.ArrayType.Color] = colors;
                arrays[(int)Mesh.ArrayType.Index] = indices;

                ArrayMesh chunkMesh = new();
                chunkMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

                MeshInstance3D chunkInstance = new()
                {
                    Name = $"TerrainChunk_{cx}_{cz}",
                    Mesh = chunkMesh,
                    MaterialOverride = terrainMaterial
                };
                _terrainRoot.AddChild(chunkInstance);
            }
        }
    }

    private void SetupWater()
    {
        _waterRoot = new Node3D { Name = "WaterRoot" };
        AddChild(_waterRoot);

        // Load custom spatial water shader
        Shader waterShader = GD.Load<Shader>("res://UstyeWater.gdshader");
        ShaderMaterial waterMaterial = new()
        {
            Shader = waterShader
        };

        // 1. River water ribbon along smooth Catmull-Rom spline (~256 dense cross-sections)
        Vector2[] spline = _plan.RiverSpline;
        float halfWidth = _plan.RiverWidth * 0.52f; // Slight overlap with bank

        Vector3[] riverVerts = new Vector3[spline.Length * 2];
        Vector3[] riverNorms = new Vector3[riverVerts.Length];
        Vector2[] riverUvs = new Vector2[riverVerts.Length];
        int[] riverIndices = new int[(spline.Length - 1) * 6];

        float cumulativeLength = 0f;

        for (int i = 0; i < spline.Length; i++)
        {
            Vector2 curr = spline[i];
            if (i > 0) cumulativeLength += curr.DistanceTo(spline[i - 1]);

            Vector2 tangent = i < spline.Length - 1
                ? (spline[i + 1] - curr).Normalized()
                : (curr - spline[i - 1]).Normalized();
            Vector2 normal = new(-tangent.Y, tangent.X);

            Vector2 left = curr - normal * halfWidth;
            Vector2 right = curr + normal * halfWidth;

            riverVerts[i * 2] = new Vector3(left.X, 2.0f, left.Y);
            riverVerts[i * 2 + 1] = new Vector3(right.X, 2.0f, right.Y);
            riverNorms[i * 2] = Vector3.Up;
            riverNorms[i * 2 + 1] = Vector3.Up;

            float v = cumulativeLength * 0.015f;
            riverUvs[i * 2] = new Vector2(0f, v);
            riverUvs[i * 2 + 1] = new Vector2(1f, v);
        }

        int rOffset = 0;
        for (int i = 0; i < spline.Length - 1; i++)
        {
            int a = i * 2;
            int b = i * 2 + 1;
            int c = (i + 1) * 2;
            int d = (i + 1) * 2 + 1;

            riverIndices[rOffset++] = a;
            riverIndices[rOffset++] = b;
            riverIndices[rOffset++] = c;

            riverIndices[rOffset++] = b;
            riverIndices[rOffset++] = d;
            riverIndices[rOffset++] = c;
        }

        using Godot.Collections.Array riverArrays = new();
        riverArrays.Resize((int)Mesh.ArrayType.Max);
        riverArrays[(int)Mesh.ArrayType.Vertex] = riverVerts;
        riverArrays[(int)Mesh.ArrayType.Normal] = riverNorms;
        riverArrays[(int)Mesh.ArrayType.TexUV] = riverUvs;
        riverArrays[(int)Mesh.ArrayType.Index] = riverIndices;

        ArrayMesh riverMesh = new();
        riverMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, riverArrays);

        MeshInstance3D riverInstance = new()
        {
            Name = "RiverWater",
            Mesh = riverMesh,
            MaterialOverride = waterMaterial
        };
        _waterRoot.AddChild(riverInstance);

        // 2. Bay water surface: tessellated grid covering bay polygon at Y = 2.0 m
        Vector2[] bayCoast = _plan.BayCoastSpline;
        List<Vector3> bayVerts = [];
        List<Vector3> bayNorms = [];
        List<Vector2> bayUvs = [];
        List<int> bayIndices = [];

        // Center point in bay for smooth fan/grid
        Vector2 bayCenter = new(4500f, 4500f);
        bayVerts.Add(new Vector3(bayCenter.X, 2.0f, bayCenter.Y));
        bayNorms.Add(Vector3.Up);
        bayUvs.Add(new Vector2(0.5f, 0.5f));

        for (int i = 0; i < bayCoast.Length; i++)
        {
            Vector2 p = bayCoast[i];
            bayVerts.Add(new Vector3(p.X, 2.0f, p.Y));
            bayNorms.Add(Vector3.Up);
            bayUvs.Add(new Vector2(p.X * 0.001f, p.Y * 0.001f));
        }

        // Corner at (5000, 5000)
        bayVerts.Add(new Vector3(5000f, 2.0f, 5000f));
        bayNorms.Add(Vector3.Up);
        bayUvs.Add(new Vector2(5.0f, 5.0f));

        int totalBay = bayVerts.Count;
        for (int i = 1; i < totalBay - 1; i++)
        {
            bayIndices.Add(0);
            bayIndices.Add(i);
            bayIndices.Add(i + 1);
        }
        bayIndices.Add(0);
        bayIndices.Add(totalBay - 1);
        bayIndices.Add(1);

        using Godot.Collections.Array bayArrays = new();
        bayArrays.Resize((int)Mesh.ArrayType.Max);
        bayArrays[(int)Mesh.ArrayType.Vertex] = bayVerts.ToArray();
        bayArrays[(int)Mesh.ArrayType.Normal] = bayNorms.ToArray();
        bayArrays[(int)Mesh.ArrayType.TexUV] = bayUvs.ToArray();
        bayArrays[(int)Mesh.ArrayType.Index] = bayIndices.ToArray();

        ArrayMesh bayMesh = new();
        bayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, bayArrays);

        MeshInstance3D bayInstance = new()
        {
            Name = "BayWater",
            Mesh = bayMesh,
            MaterialOverride = waterMaterial
        };
        _waterRoot.AddChild(bayInstance);
    }

    private void SetupRoads()
    {
        _roadsRoot = new Node3D { Name = "RoadsRoot" };
        AddChild(_roadsRoot);

        StandardMaterial3D asphaltMaterial = new()
        {
            AlbedoColor = new Color("353638"),
            Roughness = 0.88f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        StandardMaterial3D gravelMaterial = new()
        {
            AlbedoColor = new Color("585246"),
            Roughness = 0.94f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        StandardMaterial3D dirtPathMaterial = new()
        {
            AlbedoColor = new Color("655b4b"),
            Roughness = 0.98f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        foreach (UstyePlanData.Road road in _plan.Roads)
        {
            Material mat = road.Id switch
            {
                "central" => asphaltMaterial,
                "foot" => dirtPathMaterial,
                _ => gravelMaterial
            };

            // For crossing routes, find the crossing deck span to avoid drawing road on riverbed
            UstyePlanData.Crossing? crossing = _plan.Crossings.FirstOrDefault(c => c.RoadId == road.Id);
            BuildRoadRibbon(road.Points, road.Width, mat, $"Road_{road.Id}", crossing);
        }

        foreach (UstyePlanData.Road access in _plan.AccessRoads)
        {
            BuildRoadRibbon(access.Points, access.Width, gravelMaterial, $"Road_{access.Id}", null);
        }
    }

    private static List<Vector2> SubdividePoints(Vector2[] points, float maxStep = 6.0f)
    {
        List<Vector2> result = [];
        if (points.Length == 0) return result;
        result.Add(points[0]);
        for (int i = 0; i < points.Length - 1; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[i + 1];
            float dist = a.DistanceTo(b);
            int steps = Math.Max(1, (int)MathF.Ceiling(dist / maxStep));
            for (int s = 1; s <= steps; s++)
            {
                result.Add(a.Lerp(b, (float)s / steps));
            }
        }
        return result;
    }

    private static List<List<Vector2>> SplitRoadAtCrossing(Vector2[] points, UstyePlanData.Crossing? crossing)
    {
        if (crossing is null || points.Length < 2)
        {
            return [points.ToList()];
        }

        Vector2 pA = crossing.EndA;
        Vector2 pB = crossing.EndB;

        Vector2 pStart = points[0].DistanceTo(pB) < points[0].DistanceTo(pA) ? pB : pA;
        Vector2 pEnd = pStart == pA ? pB : pA;

        Vector2 span = pEnd - pStart;
        float spanLen = span.Length();
        Vector2 spanDir = span / spanLen;

        List<Vector2> appA = [];
        List<Vector2> appB = [];

        foreach (Vector2 p in points)
        {
            float proj = (p - pStart).Dot(spanDir);
            if (proj <= 0.01f)
            {
                if (p.DistanceTo(pStart) > 2.0f)
                {
                    appA.Add(p);
                }
            }
            else if (proj >= spanLen - 0.01f)
            {
                if (p.DistanceTo(pEnd) > 2.0f)
                {
                    appB.Add(p);
                }
            }
        }

        appA.Add(pStart);
        appB.Insert(0, pEnd);

        return [appA, appB];
    }

    private void BuildRoadRibbon(Vector2[] points, float width, Material material, string name, UstyePlanData.Crossing? crossing)
    {
        if (points.Length < 2) return;

        List<List<Vector2>> strips = SplitRoadAtCrossing(points, crossing);
        float halfWidth = width * 0.5f;

        List<Vector3> verts = [];
        List<Vector3> norms = [];
        List<int> indices = [];

        foreach (List<Vector2> strip in strips)
        {
            if (strip.Count < 2) continue;
            List<Vector2> densePoints = SubdividePoints(strip.ToArray(), 6.0f);
            int stripBaseVertex = verts.Count;

            for (int i = 0; i < densePoints.Count; i++)
            {
                Vector2 curr = densePoints[i];

                Vector2 tangent = i < densePoints.Count - 1
                    ? (densePoints[i + 1] - curr).Normalized()
                    : (curr - densePoints[i - 1]).Normalized();
                Vector2 normal = new(-tangent.Y, tangent.X);

                Vector2 left = curr - normal * halfWidth;
                Vector2 right = curr + normal * halfWidth;

                // Road crown sits cleanly +0.12 m on terrain without hovering or clipping
                float yLeft = _plan.SampleTerrainElevation(left.X, left.Y) + 0.12f;
                float yRight = _plan.SampleTerrainElevation(right.X, right.Y) + 0.12f;

                int baseIdx = verts.Count;
                verts.Add(new Vector3(left.X, yLeft, left.Y));
                verts.Add(new Vector3(right.X, yRight, right.Y));
                norms.Add(Vector3.Up);
                norms.Add(Vector3.Up);

                // Only connect triangles within the current strip!
                if (baseIdx >= stripBaseVertex + 2)
                {
                    int a = baseIdx - 2;
                    int b = baseIdx - 1;
                    int c = baseIdx;
                    int d = baseIdx + 1;

                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);

                    indices.Add(b);
                    indices.Add(d);
                    indices.Add(c);
                }
            }
        }

        if (indices.Count == 0) return;

        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        MeshInstance3D inst = new()
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = material
        };
        _roadsRoot.AddChild(inst);
    }

    private void SetupCrossings()
    {
        StandardMaterial3D concreteBridgeMat = new()
        {
            AlbedoColor = new Color("727578"),
            Roughness = 0.85f
        };

        StandardMaterial3D steelDeckMat = new()
        {
            AlbedoColor = new Color("484d52"),
            Roughness = 0.70f,
            Metallic = 0.35f
        };

        StandardMaterial3D railingMat = new()
        {
            AlbedoColor = new Color("35383c"),
            Roughness = 0.60f
        };

        foreach (UstyePlanData.Crossing crossing in _plan.Crossings)
        {
            Node3D bridgeRoot = new() { Name = $"Bridge_{crossing.Id}" };
            _roadsRoot.AddChild(bridgeRoot);

            Vector2 a = crossing.EndA;
            Vector2 b = crossing.EndB;

            // Height of road approaches at EndA and EndB
            float hA = _plan.SampleTerrainElevation(a.X, a.Y) + 0.12f;
            float hB = _plan.SampleTerrainElevation(b.X, b.Y) + 0.12f;

            Vector3 pA = new(a.X, hA, a.Y);
            Vector3 pB = new(b.X, hB, b.Y);
            Vector3 spanVec = pB - pA;
            float length = spanVec.Length();
            float deckWidth = crossing.DeckWidth;
            float deckThickness = crossing.Id == "CROSS-C" ? 1.6f : 1.2f;

            // Bridge basis: axisX along span from EndA to EndB
            Vector3 axisX = spanVec.Normalized();
            Vector2 dir2D = (b - a).Normalized();
            Vector3 axisZ = new Vector3(-dir2D.Y, 0f, dir2D.X).Normalized();
            Vector3 axisY = axisZ.Cross(axisX).Normalized();
            Basis deckBasis = new(axisX, axisY, axisZ);

            Vector3 topCenter = (pA + pB) * 0.5f;
            Vector3 boxCenter = topCenter - axisY * (deckThickness * 0.5f);

            Material deckMat = crossing.Id == "CROSS-C" ? concreteBridgeMat : steelDeckMat;
            MeshInstance3D deckMesh = new()
            {
                Name = "Deck",
                Mesh = new BoxMesh { Size = new Vector3(length, deckThickness, deckWidth) },
                MaterialOverride = deckMat,
                Transform = new Transform3D(deckBasis, boxCenter)
            };
            bridgeRoot.AddChild(deckMesh);

            float railHeight = 1.1f;
            float railThickness = 0.25f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 railCenter = boxCenter +
                    axisY * (deckThickness * 0.5f + railHeight * 0.5f) +
                    axisZ * (side * (deckWidth * 0.5f - railThickness * 0.5f));

                MeshInstance3D rail = new()
                {
                    Name = $"Railing_{side}",
                    Mesh = new BoxMesh { Size = new Vector3(length, railHeight, railThickness) },
                    MaterialOverride = railingMat,
                    Transform = new Transform3D(deckBasis, railCenter)
                };
                bridgeRoot.AddChild(rail);
            }

            // Abutment blocks anchored into the river banks at EndA and EndB
            float yaw2D = -MathF.Atan2(dir2D.Y, dir2D.X);
            MeshInstance3D abutmentA = new()
            {
                Name = "Abutment_A",
                Mesh = new BoxMesh { Size = new Vector3(8f, 5f, deckWidth * 1.15f) },
                MaterialOverride = concreteBridgeMat,
                Position = new Vector3(a.X, hA - 2.5f, a.Y),
                Rotation = new Vector3(0f, yaw2D, 0f)
            };
            bridgeRoot.AddChild(abutmentA);

            MeshInstance3D abutmentB = new()
            {
                Name = "Abutment_B",
                Mesh = new BoxMesh { Size = new Vector3(8f, 5f, deckWidth * 1.15f) },
                MaterialOverride = concreteBridgeMat,
                Position = new Vector3(b.X, hB - 2.5f, b.Y),
                Rotation = new Vector3(0f, yaw2D, 0f)
            };
            bridgeRoot.AddChild(abutmentB);

            int numPiers = length > 200f ? 4 : 2;
            for (int p = 1; p <= numPiers; p++)
            {
                float t = (float)p / (numPiers + 1);
                Vector2 pierPos = a.Lerp(b, t);
                float deckBottomY = Mathf.Lerp(hA, hB, t) - deckThickness;
                float bedY = _plan.SampleTerrainElevation(pierPos.X, pierPos.Y);
                float pierHeight = MathF.Max(1.0f, deckBottomY - bedY);
                float pierCenterY = bedY + pierHeight * 0.5f;

                MeshInstance3D pier = new()
                {
                    Name = $"Pier_{p}",
                    Mesh = new BoxMesh { Size = new Vector3(4f, pierHeight, deckWidth * 0.85f) },
                    MaterialOverride = concreteBridgeMat,
                    Position = new Vector3(pierPos.X, pierCenterY, pierPos.Y),
                    Rotation = new Vector3(0f, yaw2D, 0f)
                };
                bridgeRoot.AddChild(pier);
            }
        }
    }

    private void SetupStructures()
    {
        _structuresRoot = new Node3D { Name = "StructuresRoot" };
        AddChild(_structuresRoot);

        StandardMaterial3D panelBlockMat = new()
        {
            AlbedoColor = new Color("ababa7"),
            Roughness = 0.90f
        };

        StandardMaterial3D windowBandMat = new()
        {
            AlbedoColor = new Color("3e4248"),
            Roughness = 0.55f,
            Metallic = 0.15f
        };

        StandardMaterial3D warehouseWallMat = new()
        {
            AlbedoColor = new Color("656e6d"),
            Roughness = 0.88f
        };

        StandardMaterial3D warehouseRoofMat = new()
        {
            AlbedoColor = new Color("4a5252"),
            Roughness = 0.80f
        };

        StandardMaterial3D logMat = new()
        {
            AlbedoColor = new Color("785838"),
            Roughness = 0.95f
        };

        StandardMaterial3D waterTowerMat = new()
        {
            AlbedoColor = new Color("888e92"),
            Roughness = 0.85f
        };

        StandardMaterial3D waterTankMat = new()
        {
            AlbedoColor = new Color("4e5a62"),
            Roughness = 0.65f,
            Metallic = 0.30f
        };

        foreach (UstyePlanData.Structure structure in _plan.Structures)
        {
            Vector2 min = structure.Min;
            Vector2 size = structure.Size;
            Vector2 center = min + size * 0.5f;
            float baseElevation = _plan.SampleTerrainElevation(center.X, center.Y);

            Node3D node = new() { Name = $"Structure_{structure.Id}" };
            _structuresRoot.AddChild(node);

            if (structure.Kind == "panel_block")
            {
                MeshInstance3D blockMesh = new()
                {
                    Name = "BlockBody",
                    Mesh = new BoxMesh { Size = new Vector3(size.X, structure.Height, size.Y) },
                    MaterialOverride = panelBlockMat,
                    Position = new Vector3(center.X, baseElevation + structure.Height * 0.5f, center.Y)
                };
                node.AddChild(blockMesh);

                for (int floor = 1; floor <= 4; floor++)
                {
                    float yBand = baseElevation + floor * (structure.Height / 5f);
                    MeshInstance3D bandNorth = new()
                    {
                        Name = $"WindowBand_N_{floor}",
                        Mesh = new BoxMesh { Size = new Vector3(size.X * 0.92f, 1.1f, 0.15f) },
                        MaterialOverride = windowBandMat,
                        Position = new Vector3(center.X, yBand, center.Y - size.Y * 0.5f - 0.08f)
                    };
                    node.AddChild(bandNorth);

                    MeshInstance3D bandSouth = new()
                    {
                        Name = $"WindowBand_S_{floor}",
                        Mesh = new BoxMesh { Size = new Vector3(size.X * 0.92f, 1.1f, 0.15f) },
                        MaterialOverride = windowBandMat,
                        Position = new Vector3(center.X, yBand, center.Y + size.Y * 0.5f + 0.08f)
                    };
                    node.AddChild(bandSouth);
                }

                MeshInstance3D parapet = new()
                {
                    Name = "Parapet",
                    Mesh = new BoxMesh { Size = new Vector3(size.X * 1.01f, 0.6f, size.Y * 1.01f) },
                    MaterialOverride = panelBlockMat,
                    Position = new Vector3(center.X, baseElevation + structure.Height + 0.3f, center.Y)
                };
                node.AddChild(parapet);
            }
            else if (structure.Kind == "warehouse")
            {
                float wallH = structure.Height * 0.7f;
                float roofH = structure.Height * 0.3f;

                MeshInstance3D walls = new()
                {
                    Name = "WarehouseWalls",
                    Mesh = new BoxMesh { Size = new Vector3(size.X, wallH, size.Y) },
                    MaterialOverride = warehouseWallMat,
                    Position = new Vector3(center.X, baseElevation + wallH * 0.5f, center.Y)
                };
                node.AddChild(walls);

                MeshInstance3D roof = new()
                {
                    Name = "WarehouseRoof",
                    Mesh = new BoxMesh { Size = new Vector3(size.X * 1.03f, roofH, size.Y * 1.03f) },
                    MaterialOverride = warehouseRoofMat,
                    Position = new Vector3(center.X, baseElevation + wallH + roofH * 0.5f, center.Y)
                };
                node.AddChild(roof);
            }
            else if (structure.Kind == "log_stack")
            {
                MeshInstance3D logs = new()
                {
                    Name = "LogStackBody",
                    Mesh = new BoxMesh { Size = new Vector3(size.X, structure.Height, size.Y) },
                    MaterialOverride = logMat,
                    Position = new Vector3(center.X, baseElevation + structure.Height * 0.5f, center.Y)
                };
                node.AddChild(logs);
            }
            else if (structure.Kind == "water_tower")
            {
                float shaftHeight = 16f;
                float tankHeight = 6f;

                MeshInstance3D shaft = new()
                {
                    Name = "TowerShaft",
                    Mesh = new CylinderMesh { TopRadius = 4.5f, BottomRadius = 5.5f, Height = shaftHeight },
                    MaterialOverride = waterTowerMat,
                    Position = new Vector3(center.X, baseElevation + shaftHeight * 0.5f, center.Y)
                };
                node.AddChild(shaft);

                MeshInstance3D tank = new()
                {
                    Name = "WaterTank",
                    Mesh = new CylinderMesh { TopRadius = 8.5f, BottomRadius = 8.5f, Height = tankHeight },
                    MaterialOverride = waterTankMat,
                    Position = new Vector3(center.X, baseElevation + shaftHeight + tankHeight * 0.5f, center.Y)
                };
                node.AddChild(tank);

                MeshInstance3D roof = new()
                {
                    Name = "TankRoof",
                    Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 9.0f, Height = 2.5f },
                    MaterialOverride = waterTowerMat,
                    Position = new Vector3(center.X, baseElevation + shaftHeight + tankHeight + 1.25f, center.Y)
                };
                node.AddChild(roof);

                MeshInstance3D gallery = new()
                {
                    Name = "ObservationPlatform",
                    Mesh = new CylinderMesh { TopRadius = 9.5f, BottomRadius = 9.5f, Height = 0.5f },
                    MaterialOverride = panelBlockMat,
                    Position = new Vector3(center.X, baseElevation + 18.0f, center.Y)
                };
                node.AddChild(gallery);
            }
        }
    }

    private void SetupLocalContext()
    {
        _contextRoot = new Node3D { Name = "LocalContextRoot" };
        AddChild(_contextRoot);

        StandardMaterial3D concretePadMat = new()
        {
            AlbedoColor = new Color("555759"),
            Roughness = 0.90f
        };

        StandardMaterial3D timberPalletMat = new()
        {
            AlbedoColor = new Color("654c30"),
            Roughness = 0.92f
        };

        StandardMaterial3D earthenBermMat = new()
        {
            AlbedoColor = new Color("4a473c"),
            Roughness = 0.96f
        };

        // 1. Town West and Town East courtyard pads between blocks
        foreach (UstyePlanData.Structure st in _plan.Structures)
        {
            if (st.Kind == "panel_block")
            {
                Vector2 center = st.Min + st.Size * 0.5f;
                float y = _plan.SampleTerrainElevation(center.X, center.Y);

                MeshInstance3D pad = new()
                {
                    Name = $"Courtyard_{st.Id}",
                    Mesh = new BoxMesh { Size = new Vector3(st.Size.X + 8f, 0.15f, st.Size.Y + 8f) },
                    MaterialOverride = concretePadMat,
                    Position = new Vector3(center.X, y + 0.05f, center.Y)
                };
                _contextRoot.AddChild(pad);
            }
        }

        // 2. Sawmill timber yard details
        Vector3[] lumberPiles =
        [
            new(2470f, 0f, 3620f),
            new(2490f, 0f, 3635f),
            new(2510f, 0f, 3650f)
        ];
        for (int i = 0; i < lumberPiles.Length; i++)
        {
            Vector3 pos = lumberPiles[i];
            float y = _plan.SampleTerrainElevation(pos.X, pos.Z);
            MeshInstance3D pile = new()
            {
                Name = $"LumberPile_{i}",
                Mesh = new BoxMesh { Size = new Vector3(18f, 2.2f, 6.5f) },
                MaterialOverride = timberPalletMat,
                Position = new Vector3(pos.X, y + 1.1f, pos.Z),
                Rotation = new Vector3(0f, 0.2f * i, 0f)
            };
            _contextRoot.AddChild(pile);
        }

        // 3. Ammo depot blast berms
        Vector3[] depotBerms =
        [
            new(3440f, 0f, 2660f),
            new(3570f, 0f, 2660f),
            new(3440f, 0f, 2790f),
            new(3570f, 0f, 2790f)
        ];
        for (int i = 0; i < depotBerms.Length; i++)
        {
            Vector3 pos = depotBerms[i];
            float y = _plan.SampleTerrainElevation(pos.X, pos.Z);
            MeshInstance3D berm = new()
            {
                Name = $"DepotBerm_{i}",
                Mesh = new BoxMesh { Size = new Vector3(85f, 2.8f, 5.0f) },
                MaterialOverride = earthenBermMat,
                Position = new Vector3(pos.X, y + 1.4f, pos.Z)
            };
            _contextRoot.AddChild(berm);
        }
    }

    private void SetupLandmarkSites()
    {
        _landmarksRoot = new Node3D { Name = "LandmarksRoot" };
        AddChild(_landmarksRoot);

        StandardMaterial3D stoneChurchMat = new()
        {
            AlbedoColor = new Color("c8c4bc"),
            Roughness = 0.92f
        };

        StandardMaterial3D churchRoofMat = new()
        {
            AlbedoColor = new Color("586356"),
            Roughness = 0.75f
        };

        StandardMaterial3D copperSpireMat = new()
        {
            AlbedoColor = new Color("427a65"),
            Roughness = 0.50f,
            Metallic = 0.35f
        };

        StandardMaterial3D redMastMat = new()
        {
            AlbedoColor = new Color("c4352b"),
            Roughness = 0.65f,
            Metallic = 0.25f
        };

        StandardMaterial3D whiteMastMat = new()
        {
            AlbedoColor = new Color("e8e8ea"),
            Roughness = 0.65f
        };

        StandardMaterial3D beaconMat = new()
        {
            AlbedoColor = new Color("ff3322"),
            EmissionEnabled = true,
            Emission = new Color("ff2211"),
            EmissionEnergyMultiplier = 3.0f
        };

        foreach (UstyePlanData.Site site in _plan.Sites)
        {
            Vector2 pos = site.Position;
            float baseElevation = _plan.SampleTerrainElevation(pos.X, pos.Y);

            if (site.Id == "OBS-W")
            {
                Node3D church = new() { Name = "Landmark_Church" };
                _landmarksRoot.AddChild(church);

                MeshInstance3D nave = new()
                {
                    Name = "Nave",
                    Mesh = new BoxMesh { Size = new Vector3(32f, 12f, 16f) },
                    MaterialOverride = stoneChurchMat,
                    Position = new Vector3(pos.X, baseElevation + 6f, pos.Y)
                };
                church.AddChild(nave);

                MeshInstance3D roof = new()
                {
                    Name = "NaveRoof",
                    Mesh = new BoxMesh { Size = new Vector3(33f, 5f, 17f) },
                    MaterialOverride = churchRoofMat,
                    Position = new Vector3(pos.X, baseElevation + 14.5f, pos.Y)
                };
                church.AddChild(roof);

                MeshInstance3D tower = new()
                {
                    Name = "BellTower",
                    Mesh = new BoxMesh { Size = new Vector3(9f, 22f, 9f) },
                    MaterialOverride = stoneChurchMat,
                    Position = new Vector3(pos.X - 16f, baseElevation + 11f, pos.Y)
                };
                church.AddChild(tower);

                MeshInstance3D spire = new()
                {
                    Name = "Spire",
                    Mesh = new CylinderMesh { TopRadius = 0.2f, BottomRadius = 4.8f, Height = 9f },
                    MaterialOverride = copperSpireMat,
                    Position = new Vector3(pos.X - 16f, baseElevation + 26.5f, pos.Y)
                };
                church.AddChild(spire);
            }
            else if (site.Id == "OBS-E")
            {
                Node3D mast = new() { Name = "Landmark_RadioMast" };
                _landmarksRoot.AddChild(mast);

                int sections = 6;
                float secHeight = 6.0f;
                for (int s = 0; s < sections; s++)
                {
                    float ySec = baseElevation + s * secHeight + secHeight * 0.5f;
                    float width = Mathf.Lerp(2.8f, 1.2f, (float)s / sections);
                    MeshInstance3D seg = new()
                    {
                        Name = $"MastSegment_{s}",
                        Mesh = new BoxMesh { Size = new Vector3(width, secHeight, width) },
                        MaterialOverride = s % 2 == 0 ? redMastMat : whiteMastMat,
                        Position = new Vector3(pos.X, ySec, pos.Y)
                    };
                    mast.AddChild(seg);
                }

                MeshInstance3D crossArm = new()
                {
                    Name = "AntennaCrossArm",
                    Mesh = new BoxMesh { Size = new Vector3(10f, 0.4f, 0.4f) },
                    MaterialOverride = whiteMastMat,
                    Position = new Vector3(pos.X, baseElevation + sections * secHeight - 1.5f, pos.Y)
                };
                mast.AddChild(crossArm);

                MeshInstance3D beacon = new()
                {
                    Name = "MastBeacon",
                    Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) },
                    MaterialOverride = beaconMat,
                    Position = new Vector3(pos.X, baseElevation + sections * secHeight + 0.5f, pos.Y)
                };
                mast.AddChild(beacon);
            }
            else if (site.Id == "LIGHT")
            {
                Node3D lighthouse = new() { Name = "Landmark_Lighthouse" };
                _landmarksRoot.AddChild(lighthouse);

                float towerH = 20f;
                MeshInstance3D tower = new()
                {
                    Name = "LighthouseTower",
                    Mesh = new CylinderMesh { TopRadius = 3.6f, BottomRadius = 5.2f, Height = towerH },
                    MaterialOverride = whiteMastMat,
                    Position = new Vector3(pos.X, baseElevation + towerH * 0.5f, pos.Y)
                };
                lighthouse.AddChild(tower);

                MeshInstance3D gallery = new()
                {
                    Name = "GalleryRing",
                    Mesh = new CylinderMesh { TopRadius = 4.6f, BottomRadius = 4.6f, Height = 0.8f },
                    MaterialOverride = redMastMat,
                    Position = new Vector3(pos.X, baseElevation + towerH + 0.4f, pos.Y)
                };
                lighthouse.AddChild(gallery);

                MeshInstance3D lantern = new()
                {
                    Name = "LanternHouse",
                    Mesh = new CylinderMesh { TopRadius = 2.8f, BottomRadius = 2.8f, Height = 3.5f },
                    MaterialOverride = beaconMat,
                    Position = new Vector3(pos.X, baseElevation + towerH + 2.5f, pos.Y)
                };
                lighthouse.AddChild(lantern);

                MeshInstance3D dome = new()
                {
                    Name = "LanternDome",
                    Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 3.0f, Height = 1.8f },
                    MaterialOverride = redMastMat,
                    Position = new Vector3(pos.X, baseElevation + towerH + 4.8f, pos.Y)
                };
                lighthouse.AddChild(dome);
            }
        }
    }

    private static ArrayMesh CreateOffsetMesh(PrimitiveMesh primitive, Vector3 offset)
    {
        using Godot.Collections.Array arrays = primitive.GetMeshArrays();
        Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] += offset;
        }
        arrays[(int)Mesh.ArrayType.Vertex] = verts;

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static Mesh BuildConiferTreeMesh()
    {
        CylinderMesh primitive = new()
        {
            TopRadius = 0.15f,
            BottomRadius = 3.2f,
            Height = 13.0f,
            RadialSegments = 7,
            Rings = 1
        };
        return CreateOffsetMesh(primitive, new Vector3(0f, 13.0f * 0.5f, 0f));
    }

    private static Mesh BuildDeciduousTreeMesh()
    {
        SphereMesh primitive = new()
        {
            Radius = 4.0f,
            Height = 8.5f,
            RadialSegments = 8,
            Rings = 5
        };
        return CreateOffsetMesh(primitive, new Vector3(0f, 8.5f * 0.5f, 0f));
    }

    private void SetupVegetation()
    {
        _vegetationRoot = new Node3D { Name = "VegetationRoot" };
        AddChild(_vegetationRoot);

        _coniferTreeMesh = BuildConiferTreeMesh();
        StandardMaterial3D coniferMat = new()
        {
            AlbedoColor = new Color("234226"),
            Roughness = 0.95f
        };

        _deciduousTreeMesh = BuildDeciduousTreeMesh();
        StandardMaterial3D deciduousMat = new()
        {
            AlbedoColor = new Color("42562c"),
            Roughness = 0.95f
        };

        const float chunkSize = 500f;
        int chunkCols = (int)(_plan.ExtentX / chunkSize);
        int chunkRows = (int)(_plan.ExtentZ / chunkSize);

        List<Transform3D>[,] coniferBuckets = new List<Transform3D>[chunkCols, chunkRows];
        List<Transform3D>[,] deciduousBuckets = new List<Transform3D>[chunkCols, chunkRows];

        for (int cz = 0; cz < chunkRows; cz++)
        {
            for (int cx = 0; cx < chunkCols; cx++)
            {
                coniferBuckets[cx, cz] = [];
                deciduousBuckets[cx, cz] = [];
            }
        }

        foreach (UstyePlanData.Region region in _plan.Regions)
        {
            if (region.Kind == "dense_forest")
            {
                CollectChunkedTrees(region.Polygon, step: 26f, chunkSize, chunkCols, chunkRows, coniferBuckets);
            }
            else if (region.Kind == "sparse_forest")
            {
                CollectChunkedTrees(region.Polygon, step: 42f, chunkSize, chunkCols, chunkRows, deciduousBuckets);
            }
        }

        // Town courtyard deciduous trees for minimal urban context
        Vector2[] townCourtyardTrees =
        [
            new(2120f, 2400f), new(2190f, 2405f),
            new(2110f, 2505f), new(2185f, 2515f),
            new(3010f, 2400f), new(3090f, 2405f),
            new(3015f, 2505f), new(3085f, 2515f)
        ];
        foreach (Vector2 p in townCourtyardTrees)
        {
            float y = _plan.SampleTerrainElevation(p.X, p.Y);
            int cx = Math.Clamp((int)(p.X / chunkSize), 0, chunkCols - 1);
            int cz = Math.Clamp((int)(p.Y / chunkSize), 0, chunkRows - 1);
            Transform3D t = new(Basis.Identity.Scaled(Vector3.One * 0.9f), new Vector3(p.X, y, p.Y));
            deciduousBuckets[cx, cz].Add(t);
        }

        _coniferMultiMeshes.Clear();
        _deciduousMultiMeshes.Clear();

        for (int cz = 0; cz < chunkRows; cz++)
        {
            for (int cx = 0; cx < chunkCols; cx++)
            {
                List<Transform3D> coniferList = coniferBuckets[cx, cz];
                if (coniferList.Count > 0)
                {
                    MultiMesh mm = new()
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        Mesh = _coniferTreeMesh,
                        InstanceCount = coniferList.Count
                    };
                    for (int i = 0; i < coniferList.Count; i++)
                    {
                        mm.SetInstanceTransform(i, coniferList[i]);
                    }

                    MultiMeshInstance3D mmi = new()
                    {
                        Name = $"Conifer_Chunk_{cx}_{cz}",
                        Multimesh = mm,
                        MaterialOverride = coniferMat
                    };
                    _vegetationRoot.AddChild(mmi);
                    _coniferMultiMeshes.Add(mmi);
                }

                List<Transform3D> deciduousList = deciduousBuckets[cx, cz];
                if (deciduousList.Count > 0)
                {
                    MultiMesh mm = new()
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        Mesh = _deciduousTreeMesh,
                        InstanceCount = deciduousList.Count
                    };
                    for (int i = 0; i < deciduousList.Count; i++)
                    {
                        mm.SetInstanceTransform(i, deciduousList[i]);
                    }

                    MultiMeshInstance3D mmi = new()
                    {
                        Name = $"Deciduous_Chunk_{cx}_{cz}",
                        Multimesh = mm,
                        MaterialOverride = deciduousMat
                    };
                    _vegetationRoot.AddChild(mmi);
                    _deciduousMultiMeshes.Add(mmi);
                }
            }
        }
    }

    private bool IsInClearing(Vector2 pt)
    {
        foreach (UstyePlanData.Region reg in _plan.Regions)
        {
            if (reg.Kind != "dense_forest" && reg.Kind != "sparse_forest")
            {
                if (UstyePlanData.PointInPolygon(pt, reg.Polygon))
                    return true;
            }
        }

        foreach (UstyePlanData.Structure st in _plan.Structures)
        {
            if (pt.X >= st.Min.X - 6f && pt.X <= st.Min.X + st.Size.X + 6f &&
                pt.Y >= st.Min.Y - 6f && pt.Y <= st.Min.Y + st.Size.Y + 6f)
                return true;
        }

        foreach (UstyePlanData.Road road in _plan.Roads)
        {
            if (UstyePlanData.DistanceToPolyline(pt, road.Points) < 14f)
                return true;
        }

        foreach (UstyePlanData.Road access in _plan.AccessRoads)
        {
            if (UstyePlanData.DistanceToPolyline(pt, access.Points) < 14f)
                return true;
        }

        return false;
    }

    private void CollectChunkedTrees(
        Vector2[] polygon,
        float step,
        float chunkSize,
        int chunkCols,
        int chunkRows,
        List<Transform3D>[,] buckets)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector2 p in polygon)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minZ) minZ = p.Y;
            if (p.Y > maxZ) maxZ = p.Y;
        }

        for (float x = minX; x <= maxX; x += step)
        {
            for (float z = minZ; z <= maxZ; z += step)
            {
                uint hash = (uint)((int)x * 73856093 ^ (int)z * 19349663);
                float jx = ((hash & 0xFF) / 255f - 0.5f) * (step * 0.5f);
                float jz = (((hash >> 8) & 0xFF) / 255f - 0.5f) * (step * 0.5f);
                float scale = 0.85f + (((hash >> 16) & 0xFF) / 255f) * 0.35f;
                float yaw = (((hash >> 24) & 0xFF) / 255f) * Mathf.Tau;

                Vector2 samplePt = new(x + jx, z + jz);
                if (UstyePlanData.PointInPolygon(samplePt, polygon) && !IsInClearing(samplePt))
                {
                    float y = _plan.SampleTerrainElevation(samplePt.X, samplePt.Y);
                    if (y > 2.2f)
                    {
                        int cx = Math.Clamp((int)(samplePt.X / chunkSize), 0, chunkCols - 1);
                        int cz = Math.Clamp((int)(samplePt.Y / chunkSize), 0, chunkRows - 1);

                        Transform3D t = new(
                            Basis.FromEuler(new Vector3(0f, yaw, 0f)).Scaled(Vector3.One * scale),
                            new Vector3(samplePt.X, y, samplePt.Y));
                        buckets[cx, cz].Add(t);
                    }
                }
            }
        }
    }

    private void SetupScaleVehicles()
    {
        _vehiclesRoot = new Node3D { Name = "ScaleVehiclesRoot" };
        AddChild(_vehiclesRoot);
        _scaleVehicles.Clear();

        // Authentic historical combat vehicles with scale 1.0 (1 Godot unit = 1 meter)
        PlaceScaleVehicle("t62", "soviet", new Vector3(2160f, 0f, 2435f), -0.1f);
        PlaceScaleVehicle("btr70", "soviet", new Vector3(2140f, 0f, 2465f), 0.2f);
        PlaceScaleVehicle("brdm2", null, new Vector3(2500f, 0f, 2450f), 0f);
        PlaceScaleVehicle("m60a1", "us", new Vector3(2800f, 0f, 2450f), MathF.PI);
        PlaceScaleVehicle("m60a1", "us", new Vector3(3050f, 0f, 2435f), MathF.PI + 0.15f);
        PlaceScaleVehicle("btr70", "soviet", new Vector3(2530f, 0f, 3630f), 0.8f);

        UpdateVehicleLods(_cameraRig.Size);
    }

    private void PlaceScaleVehicle(string id, string? faction, Vector3 pos, float yaw)
    {
        VehicleVisual? visual = VehicleVisualCatalog.InstantiateVisual(id, faction);
        if (visual is null) return;

        Node3D root = visual.Root;
        float groundY = _plan.SampleTerrainElevation(pos.X, pos.Z);
        Vector3 groundPos = new(pos.X, groundY, pos.Z);
        root.Position = groundPos;
        root.Rotation = new Vector3(0f, yaw, 0f);
        _vehiclesRoot.AddChild(root);

        _scaleVehicles.Add(new ScaleVehicle(id, faction, visual, groundPos));
    }

    private void UpdateVehicleLods(float cameraSize)
    {
        foreach (ScaleVehicle sv in _scaleVehicles)
        {
            sv.UpdateLod(cameraSize);
        }
    }

    private void SetupHud()
    {
        _hud = new CanvasLayer { Name = "HUD" };
        AddChild(_hud);

        PanelContainer topPanel = new()
        {
            Position = new Vector2(16, 12),
            Size = new Vector2(580, 84),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hud.AddChild(topPanel);

        VBoxContainer topBox = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        topPanel.AddChild(topBox);

        _titleLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "USTYE: BACKWATER CROSSING | 5000 x 5000 m PREVIEW"
        };
        topBox.AddChild(_titleLabel);

        _landmarkLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "Orient: 1: Map Overview"
        };
        topBox.AddChild(_landmarkLabel);

        _telemetryLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "Focus: [2500, 2500] | Ground: 4.0 m | Size: 4500 m | Mode: Perspective | FPS: 60"
        };
        topBox.AddChild(_telemetryLabel);

        PanelContainer bottomPanel = new()
        {
            Position = new Vector2(16, 830),
            Size = new Vector2(1000, 48),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hud.AddChild(bottomPanel);

        _helpLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = "WASD/Arrows: Pan | Wheel: Zoom | MMB/RMB/Alt+Drag: Orbit | V: Projection | 1-9, 0: Jump to Landmark | Esc: Exit"
        };
        bottomPanel.AddChild(_helpLabel);

        UpdateLandmarkHud();
        UpdateTelemetryHud(force: true);
    }

    public void SelectLandmark(int index, bool immediate = false)
    {
        if (index < 0 || index >= _landmarks.Length) return;

        _currentLandmarkIndex = index;
        CameraLandmark lm = _landmarks[index];

        if (immediate)
        {
            _isTransitioning = false;
            _cameraRig.CenterOn(lm.Focus);
            _cameraRig.Model.SetZoom(lm.Size);
            _cameraRig.Orbit(lm.Yaw - _cameraRig.ViewYaw, lm.Pitch - _cameraRig.ViewPitch);
        }
        else
        {
            _isTransitioning = true;
            _transitionElapsed = 0.0;
            _transitionStartFocus = _cameraRig.Focus;
            _transitionTargetFocus = lm.Focus;
            _transitionStartSize = _cameraRig.Size;
            _transitionTargetSize = lm.Size;
            _transitionStartPitch = _cameraRig.ViewPitch;
            _transitionTargetPitch = lm.Pitch;
            _transitionStartYaw = _cameraRig.ViewYaw;
            _transitionTargetYaw = lm.Yaw;
        }

        UpdateLandmarkHud();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_runBenchmark || _captureDir is not null || _screenshotPath is not null)
            return;

        if (_cameraRig.HandleZoomInput(@event))
        {
            _isTransitioning = false;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventMouseMotion motion &&
            ((motion.ButtonMask & (MouseButtonMask.Middle | MouseButtonMask.Right)) != 0 ||
             (motion.AltPressed && (motion.ButtonMask & MouseButtonMask.Left) != 0)))
        {
            _isTransitioning = false;
            _cameraRig.DragOrbit(motion.Relative);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.Keycode == Key.Escape)
        {
            GetTree().Quit();
        }
        else if (key.Keycode == Key.V)
        {
            _isTransitioning = false;
            _cameraRig.SetPerspective(!_cameraRig.IsPerspective);
        }
        else
        {
            int landmarkIndex = key.Keycode switch
            {
                Key.Key1 => 0,
                Key.Key2 => 1,
                Key.Key3 => 2,
                Key.Key4 => 3,
                Key.Key5 => 4,
                Key.Key6 => 5,
                Key.Key7 => 6,
                Key.Key8 => 7,
                Key.Key9 => 8,
                Key.Key0 => 9,
                _ => -1
            };
            if (landmarkIndex < 0)
                return;
            SelectLandmark(landmarkIndex);
        }

        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        long processStartTimestamp = Stopwatch.GetTimestamp();

        if (_isTransitioning)
        {
            _transitionElapsed += delta;
            float t = (float)Math.Clamp(_transitionElapsed / TransitionDuration, 0.0, 1.0);
            float smoothT = Mathf.SmoothStep(0f, 1f, t);

            Vector3 currentFocus = _transitionStartFocus.Lerp(_transitionTargetFocus, smoothT);
            float currentSize = Mathf.Lerp(_transitionStartSize, _transitionTargetSize, smoothT);
            float currentPitch = Mathf.Lerp(_transitionStartPitch, _transitionTargetPitch, smoothT);
            float currentYaw = Mathf.Lerp(_transitionStartYaw, _transitionTargetYaw, smoothT);

            _cameraRig.CenterOn(currentFocus);
            _cameraRig.Model.SetZoom(currentSize);
            _cameraRig.Orbit(currentYaw - _cameraRig.ViewYaw, currentPitch - _cameraRig.ViewPitch);

            if (t >= 1.0f)
            {
                _isTransitioning = false;
            }
        }

        _cameraRig.Update(delta, allowInput: !_isTransitioning && !_runBenchmark);
        UpdateVehicleLods(_cameraRig.Size);

        _telemetryTimer += delta;
        if (_telemetryTimer >= 0.25)
        {
            _telemetryTimer = 0.0;
            UpdateTelemetryHud(force: false);
        }

        long processEndTimestamp = Stopwatch.GetTimestamp();
        double cpuTimeMs = (double)(processEndTimestamp - processStartTimestamp) / Stopwatch.Frequency * 1000.0;

        if (_runBenchmark)
        {
            ProcessBenchmarkMonotonic(cpuTimeMs);
        }
        else
        {
            HandleCaptures(delta);
        }
    }

    private void UpdateLandmarkHud()
    {
        if (_landmarkLabel is null || _currentLandmarkIndex < 0 || _currentLandmarkIndex >= _landmarks.Length) return;
        _landmarkLabel.Text = $"Orient: {_landmarks[_currentLandmarkIndex].Name}";
    }

    private void UpdateTelemetryHud(bool force)
    {
        if (_telemetryLabel is null) return;

        Vector3 f = _cameraRig.Focus;
        int fx = (int)MathF.Round(f.X);
        int fz = (int)MathF.Round(f.Z);
        float elev = _plan.SampleTerrainElevation(f.X, f.Z);
        int elevTenths = (int)MathF.Round(elev * 10f);
        int size = (int)MathF.Round(_cameraRig.Size);
        bool persp = _cameraRig.IsPerspective;
        int fps = (int)Math.Round(Godot.Engine.GetFramesPerSecond());

        if (!force &&
            fx == _lastTelemetryFocusX &&
            fz == _lastTelemetryFocusZ &&
            elevTenths == _lastTelemetryElev &&
            size == _lastTelemetrySize &&
            persp == _lastTelemetryPerspective &&
            fps == _lastTelemetryFps)
        {
            return;
        }

        _lastTelemetryFocusX = fx;
        _lastTelemetryFocusZ = fz;
        _lastTelemetryElev = elevTenths;
        _lastTelemetrySize = size;
        _lastTelemetryPerspective = persp;
        _lastTelemetryFps = fps;

        string proj = persp ? "Perspective" : "Orthographic";
        _telemetryLabel.Text =
            $"Focus: [{fx}, {fz}] | Ground: {(elevTenths / 10f):F1} m | Size: {size} m | Mode: {proj} | FPS: {fps}";
    }

    private void ProcessBenchmarkMonotonic(double cpuTimeMs)
    {
        if (Godot.Engine.MaxFps != 0)
        {
            Godot.Engine.MaxFps = 0;
        }

        long now = Stopwatch.GetTimestamp();
        double frameIntervalMs = (double)(now - _lastFrameTimestamp) / Stopwatch.Frequency * 1000.0;
        _lastFrameTimestamp = now;

        double stageElapsedSec = (double)(now - _benchmarkStageStartTimestamp) / Stopwatch.Frequency;

        switch (_benchmarkStage)
        {
            case 1: // Warm up Overview view for 3.0 real seconds
                if (stageElapsedSec >= 3.0)
                {
                    _benchmarkStage = 2;
                    _benchmarkStageStartTimestamp = now;
                    _overviewFrameTimes.Clear();
                    _overviewCpuTimes.Clear();
                }
                break;

            case 2: // Collect 600 consecutive frame intervals for Overview
                _overviewFrameTimes.Add(frameIntervalMs);
                _overviewCpuTimes.Add(cpuTimeMs);
                if (_overviewFrameTimes.Count >= 600)
                {
                    _overviewDrawCalls = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
                    _overviewObjects = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame);
                    _overviewPrimitives = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);

                    SelectLandmark(1, immediate: true);
                    _benchmarkStage = 3;
                    _benchmarkStageStartTimestamp = Stopwatch.GetTimestamp();
                }
                break;

            case 3: // Warm up Town view for 3.0 real seconds
                if (stageElapsedSec >= 3.0)
                {
                    _benchmarkStage = 4;
                    _benchmarkStageStartTimestamp = now;
                    _townFrameTimes.Clear();
                    _townCpuTimes.Clear();
                }
                break;

            case 4: // Collect 600 consecutive frame intervals for Town view
                _townFrameTimes.Add(frameIntervalMs);
                _townCpuTimes.Add(cpuTimeMs);
                if (_townFrameTimes.Count >= 600)
                {
                    _townDrawCalls = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
                    _townObjects = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame);
                    _townPrimitives = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);

                    _benchmarkStage = 5;
                    PrintAndSaveBenchmarkReport();
                    GetTree().Quit(0);
                }
                break;
        }
    }

    private sealed record BenchmarkMetrics(
        double MeanMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs,
        double MeanFps,
        double P50Fps,
        double MeanCpuMs,
        ulong DrawCalls,
        ulong Objects,
        ulong Primitives);

    private static BenchmarkMetrics ComputeMetrics(
        List<double> samples,
        List<double> cpuSamples,
        ulong drawCalls,
        ulong objects,
        ulong primitives)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        int n = sorted.Length;
        double mean = sorted.Average();
        double p50 = sorted[(int)(0.50 * (n - 1))];
        double p95 = sorted[(int)(0.95 * (n - 1))];
        double p99 = sorted[(int)(0.99 * (n - 1))];
        double max = sorted[n - 1];
        double meanCpu = cpuSamples.Count > 0 ? cpuSamples.Average() : 0.0;

        return new BenchmarkMetrics(
            mean,
            p50,
            p95,
            p99,
            max,
            mean > 0 ? 1000.0 / mean : 0,
            p50 > 0 ? 1000.0 / p50 : 0,
            meanCpu,
            drawCalls,
            objects,
            primitives);
    }

    private void PrintAndSaveBenchmarkReport()
    {
        BenchmarkMetrics ov = ComputeMetrics(_overviewFrameTimes, _overviewCpuTimes, _overviewDrawCalls, _overviewObjects, _overviewPrimitives);
        BenchmarkMetrics tw = ComputeMetrics(_townFrameTimes, _townCpuTimes, _townDrawCalls, _townObjects, _townPrimitives);

        ulong staticMem = OS.GetStaticMemoryUsage();
        long managedMem = GC.GetTotalMemory(false);

        string gpuName = RenderingServer.GetVideoAdapterName();
        string gpuVendor = RenderingServer.GetVideoAdapterVendor();
        string gpuApi = RenderingServer.GetVideoAdapterApiVersion();
        string backendName = OS.GetName() == "macOS" ? $"Metal {gpuApi}" : gpuApi;
        string godotVer = Godot.Engine.GetVersionInfo()["string"].AsString();
        Vector2I winSize = DisplayServer.WindowGetSize();
        Vector2 vpSize = GetViewport()?.GetVisibleRect().Size ?? Vector2.Zero;
        string vsyncMode = DisplayServer.WindowGetVsyncMode().ToString();
        int maxFps = Godot.Engine.MaxFps;
        string msaaMode = (GetViewport()?.Msaa3D ?? Viewport.Msaa.Disabled).ToString();

        GD.Print("================================================================================");
        GD.Print("USTYE MAP PREVIEW PERFORMANCE BENCHMARK REPORT");
        GD.Print("================================================================================");
        GD.Print($"Platform: {OS.GetName()} ({OS.GetVersion()}) | Architecture: {Godot.Engine.GetArchitectureName()}");
        GD.Print($"Engine: Godot {godotVer} | Backend: {backendName}");
        GD.Print($"GPU: {gpuVendor} {gpuName}");
        GD.Print($"Logical Window: {winSize.X} x {winSize.Y} | Render Viewport: {vpSize.X} x {vpSize.Y}");
        GD.Print($"VSync Mode: {vsyncMode} | Max FPS: {(maxFps == 0 ? "Unlimited (0)" : maxFps.ToString())} | MSAA 3D: {msaaMode}");
        GD.Print("Platform pacing consideration: on macOS systems with ProMotion displays, CVDisplayLink or the system compositor may pace presentation at the display refresh rate (e.g. 120 Hz / 8.33 ms). Uncapped benchmark reports wall clock frame intervals and CPU process time.");
        GD.Print(FormattableString.Invariant($"Load time: {_loadTimeMs} ms (plan + 100 terrain chunks + water + roads + 23 structures + {TotalConiferCount + TotalDeciduousCount} trees + 6 vehicles)"));
        GD.Print(FormattableString.Invariant($"Static memory: {staticMem / (1024 * 1024.0):F2} MiB | Managed heap: {managedMem / (1024 * 1024.0):F2} MiB"));
        GD.Print("--------------------------------------------------------------------------------");
        GD.Print(FormattableString.Invariant($"1. Overview View (Camera Size 4600 m, {_overviewFrameTimes.Count} frames):"));
        GD.Print(FormattableString.Invariant($"   Frame interval: Mean {ov.MeanMs:F2} ms | p50 {ov.P50Ms:F2} ms | p95 {ov.P95Ms:F2} ms | p99 {ov.P99Ms:F2} ms | Max {ov.MaxMs:F2} ms"));
        GD.Print(FormattableString.Invariant($"   CPU process time: Mean {ov.MeanCpuMs:F3} ms | Throughput: Mean {ov.MeanFps:F0} FPS | Median {ov.P50Fps:F0} FPS"));
        GD.Print(FormattableString.Invariant($"   Draw calls: {ov.DrawCalls} | Objects: {ov.Objects} | Primitives: {ov.Primitives:N0}"));
        GD.Print("--------------------------------------------------------------------------------");
        GD.Print(FormattableString.Invariant($"2. Riverside Town View (Camera Size 380 m, {_townFrameTimes.Count} frames):"));
        GD.Print(FormattableString.Invariant($"   Frame interval: Mean {tw.MeanMs:F2} ms | p50 {tw.P50Ms:F2} ms | p95 {tw.P95Ms:F2} ms | p99 {tw.P99Ms:F2} ms | Max {tw.MaxMs:F2} ms"));
        GD.Print(FormattableString.Invariant($"   CPU process time: Mean {tw.MeanCpuMs:F3} ms | Throughput: Mean {tw.MeanFps:F0} FPS | Median {tw.P50Fps:F0} FPS"));
        GD.Print(FormattableString.Invariant($"   Draw calls: {tw.DrawCalls} | Objects: {tw.Objects} | Primitives: {tw.Primitives:N0}"));
        GD.Print("================================================================================");

        string mdPath = ResolveCapturePath("docs/maps/ustye/benchmark-results.md");
        string? mdDir = Path.GetDirectoryName(mdPath);
        if (!string.IsNullOrEmpty(mdDir)) Directory.CreateDirectory(mdDir);

        string mdContent = FormattableString.Invariant(
$@"# Ustye Map Preview Benchmark Results

Measured with unconstrained frame rate (`--benchmark`), VSync set to Disabled, and FPS limit set to 0.
Monotonic timestamps (`Stopwatch.GetTimestamp`) record wall clock frame intervals.
Warmup period of 3.0 real seconds per angle was applied before recording 600 consecutive frame intervals.

## Hardware & Environment

- **OS / Platform**: {OS.GetName()} ({OS.GetVersion()}) {Godot.Engine.GetArchitectureName()}
- **Engine**: Godot {godotVer}
- **Backend / API**: {backendName}
- **GPU**: {gpuVendor} {gpuName}
- **Window Size**: {winSize.X} x {winSize.Y}
- **Render Viewport**: {vpSize.X} x {vpSize.Y}
- **VSync**: {vsyncMode}
- **Max FPS**: {(maxFps == 0 ? "Unlimited (0)" : maxFps.ToString())}
- **MSAA 3D**: {msaaMode}
- **Load Time**: {_loadTimeMs} ms
- **Memory**: Static {staticMem / (1024 * 1024.0):F2} MiB, Managed {managedMem / (1024 * 1024.0):F2} MiB

> [!NOTE]
> Platform pacing consideration: on macOS systems with ProMotion displays, CVDisplayLink or the system compositor may pace presentation at the display refresh rate (e.g. 120 Hz / 8.33 ms). Uncapped benchmark reports wall clock frame intervals and CPU process time.

## Measurements (600 consecutive frames per viewpoint)

| Metric | Overview View (Size 4600 m) | Riverside Town View (Size 380 m) |
|---|---|---|
| **Mean Frame Interval** | {ov.MeanMs:F2} ms | {tw.MeanMs:F2} ms |
| **p50 (Median)** | {ov.P50Ms:F2} ms | {tw.P50Ms:F2} ms |
| **p95** | {ov.P95Ms:F2} ms | {tw.P95Ms:F2} ms |
| **p99** | {ov.P99Ms:F2} ms | {tw.P99Ms:F2} ms |
| **Max Frame Interval** | {ov.MaxMs:F2} ms | {tw.MaxMs:F2} ms |
| **Mean CPU Process Time** | {ov.MeanCpuMs:F3} ms | {tw.MeanCpuMs:F3} ms |
| **Throughput (Mean / Median)** | {ov.MeanFps:F0} / {ov.P50Fps:F0} FPS | {tw.MeanFps:F0} / {tw.P50Fps:F0} FPS |
| **Draw Calls** | {ov.DrawCalls} | {tw.DrawCalls} |
| **Objects Rendered** | {ov.Objects} | {tw.Objects} |
| **Primitives (Triangles)** | {ov.Primitives:N0} | {tw.Primitives:N0} |

Spatial chunking partitions the vegetation into 10 x 10 chunks matching terrain, enabling frustum and shadow culling when zoomed into local sites like Town.
");
        File.WriteAllText(mdPath, mdContent);
        GD.Print($"BENCHMARK_REPORT_SAVED {mdPath}");
    }

    private static string ResolveCapturePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        string repoRoot = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), "../.."));
        return Path.GetFullPath(Path.Combine(repoRoot, path));
    }

    private void HandleCaptures(double delta)
    {
        if (_screenshotPath is not null)
        {
            _screenshotDelay -= delta;
            if (_screenshotDelay <= 0)
            {
                string resolved = ResolveCapturePath(_screenshotPath);
                string? dir = Path.GetDirectoryName(resolved);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                Image? img = GetViewport()?.GetTexture()?.GetImage();
                if (img is not null)
                {
                    img.SavePng(resolved);
                    GD.Print($"SCREENSHOT_SAVED {resolved}");
                }
                else
                {
                    GD.Print($"CANNOT_CAPTURE_HEADLESS: Viewport texture image is null in headless mode for {resolved}");
                }
                _screenshotPath = null;
                if (_screenshotQuit)
                {
                    GetTree().Quit(0);
                }
            }
            return;
        }

        if (_captureDir is not null && _captureStep >= 0)
        {
            _captureTimer -= delta;
            if (_captureTimer <= 0)
            {
                string[] fileNames =
                [
                    "01_overview.png",
                    "02_town_and_bridge.png",
                    "03_crossings.png",
                    "04_sawmill.png"
                ];

                int[] landmarkIndices = [0, 1, 6, 3];

                if (_captureStep < fileNames.Length)
                {
                    string resolvedDir = ResolveCapturePath(_captureDir);
                    Directory.CreateDirectory(resolvedDir);
                    string outPath = Path.Combine(resolvedDir, fileNames[_captureStep]);

                    Image? img = GetViewport()?.GetTexture()?.GetImage();
                    if (img is not null)
                    {
                        img.SavePng(outPath);
                        GD.Print($"MAP_PREVIEW_CAPTURED [{_captureStep + 1}/{fileNames.Length}] {outPath}");
                    }
                    else
                    {
                        GD.Print($"CANNOT_CAPTURE_HEADLESS: Viewport texture image is null in headless mode for {outPath}");
                    }

                    _captureStep++;
                    if (_captureStep < fileNames.Length)
                    {
                        SelectLandmark(landmarkIndices[_captureStep], immediate: true);
                        _captureTimer = 0.5;
                    }
                    else
                    {
                        GD.Print("ALL_MAP_PREVIEWS_CAPTURED");
                        GetTree().Quit(0);
                    }
                }
            }
        }
    }
}
