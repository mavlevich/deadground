using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

public partial class Main : Node3D
{
    private HeightMap _map = null!;
    private IGameSession _session = null!;
    private Camera3D _camera = null!;
    private CameraRig _cameraRig = null!;
    private EnvironmentView _environmentView = null!;
    private SettingsPanel _settingsPanel = null!;
    private FramePacingMetrics? _benchmarkMetrics;
    private int _benchmarkTargetFrames;
    private float? _benchmarkZoomToSize;
    private int _lodBudgetPerFrame = VehicleLod.DefaultMaxInstantiationsPerFrame;
    private int _terrainShadowStride = TerrainView.DefaultShadowStride;
    private float _cachedTerrainViewportHeightPx = 900f;
    private long _lastFrameTimestamp;
    private int _benchmarkWarmupFrames = 5;
    private BenchmarkRenderOverrides _benchmarkOverrides = new();
    private float _benchmarkWarmupRemainingSec;
    private double _benchmarkElapsedSec;
    private bool _benchmarkActive;
    private bool _benchmarkWarmupCompleted;
    private bool _isTimedBenchmark;
    private FramePacingMetrics? _minuteWindowMetrics;
    private int _minuteIndex = 1;
    private double _minuteElapsedSec;
    private Vector3? _panAnchor;
    private double _panElapsed;
    private bool _benchmarkQuit;

    internal bool IsBenchmarkInputBlocked => _benchmarkActive;
    internal bool IsBenchmarkWarmupCompleted => _benchmarkWarmupCompleted;
    internal int BenchmarkSampleCount => _benchmarkMetrics?.TotalCount ?? 0;
    internal int BenchmarkStoredSampleCount => _benchmarkMetrics?.Count ?? 0;
    private bool _fullscreenCli;
    private Vector2I? _windowSizeCli;
    private EnvironmentViewCapture? _viewCapture;
    private Label _stats = null!, _status = null!, _subtitle = null!, _title = null!;
    private UnitViewSet _unitViews = null!;
    private ContactViewSet _contactViews = null!;
    private WreckViewSet _wreckViews = null!;
    private OrderDestinationView _destination = null!;
    private OrderCursorOverlay _orderCursorOverlay = null!;
    private OrderPreviewController _previewController = null!;
    private DestinationEvaluator _destinationEvaluator = null!;
    private double _hudTimer, _smokeTimer;
    private bool _smokeTest;
    private int _smokeQuitFrames = -1;
    private int _smokeExitCode;
    private bool _terrainWindingValid;
    private long _lastBytes;
    private uint _lastCommandResult;
    private string _scenario = "prototype";
    private CombatEffects _combatEffects = null!;
    private HelicopterAudioController? _helicopterAudio;
    private SmokeView? _smokeView;
    private BuildingDestructionView? _buildingDestructionView;
    private BuildingGarrisonView? _buildingGarrisonView;
    private Control? _garrisonBadgeRoot;
    private StructureId _hoveredGarrisonStructure;
    private uint _pendingAttackGroundMinSeq;
    private uint _pendingAttackGroundMaxSeq;
    private uint _lastHandledAttackGroundResultSeq;
    private string _pendingAttackGroundTargetDesc = "ground point";
    private readonly LineOfSightContext _clientLosContext = new();
    private MissionPanel? _missionPanel;
    private StaticGeometry? _scenarioGeometry;
    private UnitCatalog _catalog = null!;
    private SelectionCard _selectionCard = null!;
    private ContextActionPanel _contextActionPanel = null!;
    private UnitCommandPanel _commandPanel = null!;
    private ControlsHelpPanel _helpPanel = null!;
    private bool _debugHudVisible;
    private bool _showRangeRings;
    private float _lastSelectionCardHeight = -1;
    private HudTargetingMode _hudTargetingMode;
    private uint _hudTargetSelectionHash;
    private int _hudTargetSelectionCount;
    private Vector2? _headlessPointerPosition;
    private OrderNotification _orderNotification = null!;
    private RoleBadgeOverlay _roleBadgeOverlay = null!;
    private SelectionBoxOverlay _selectionBoxOverlay = null!;
    private readonly SelectionController _selectionController = new();
    private PanelContainer _topHud = null!, _utilityHud = null!;
    private TacticalMinimap _minimap = null!;
    private readonly Rect2[] _badgeOccludingPanels = new Rect2[9];
    private readonly Rect2[] _inputOccludingPanels = new Rect2[8];
    private FogOfWarView? _fogOfWar;
    private string? _screenshotPath;
    private double _screenshotTimer = 0.5;
    private string? _screenshotOnFinishPath;
    private double _screenshotOnFinishTimer = 1.0;
    private bool _screenshotQuit;
    private string? _forestDemoCaptureDir;
    private int _forestDemoStage = -1;
    private double _forestDemoTimer = 0.5;
    private bool _forestDemoTargetSpotted;
    private bool _forestDemoContactLost;
    private string? _transportDemoCaptureDir;
    private int _transportDemoStage = -1;
    private double _transportDemoTimer = 0.5;
    private bool _transportDemoStage2Arrived;
    private bool _showVehicleCollisionBounds;
    private readonly MeshInstance3D _collisionDebugMeshInstance = new();
    private readonly ImmediateMesh _collisionDebugMesh = new();
    private bool _showVegetationDebug;
    private readonly MeshInstance3D _vegetationDebugMeshInstance = new();
    private readonly ImmediateMesh _vegetationDebugMesh = new();
    private static readonly StandardMaterial3D VegetationDebugMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = false
    };
    private static readonly StandardMaterial3D CollisionBoundsMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        AlbedoColor = Colors.White,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = false
    };
    private static readonly StandardMaterial3D VelocityDebugMaterial = new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(0.2f, 0.9f, 1.0f, 0.9f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        NoDepthTest = false
    };
    private bool _trafficObservedWaiting;
    private bool _trafficObservedResumed;
    private bool _trafficLeaderCleared;
    private int _ustyeSetupIndex;
    private UstyeMapView? _ustyeMapView;
    private TerrainView? _terrainView;
    private IMapShadowLayers? _activeShadowMapLayers;
    private bool _hasExplicitCameraFocus;
    private Vector3 _initialSpawnFocus;
    private float _initialSpawnSize;
    private int _homeViewAssignedPlayerId;
    private SessionMenu _sessionMenu = null!;
    private PauseMenu _pauseMenu = null!;
    private ClientLifecycleController _lifecycleController = null!;
    private bool _isMenuMode;
    private CanvasLayer _hudCanvas = null!;
    private MatchSetup _matchSetup = null!;
    private readonly Node3D _environmentRoot = new() { Name = "EnvironmentRoot" };

    internal float CachedTerrainViewportHeightPx => _cachedTerrainViewportHeightPx;
    internal PanelContainer TopHud => _topHud;
    internal PanelContainer UtilityHud => _utilityHud;
    internal ControlsHelpPanel HelpPanel => _helpPanel;
    internal bool DebugHudVisible => _debugHudVisible;
    internal bool RangeLimitsVisible => _showRangeRings;
    internal Label StatsLabel => _stats;
    internal FogOfWarView? FogOfWar => _fogOfWar;
    internal CameraRig CameraRig => _cameraRig;
    internal Camera3D Camera => _camera;
    internal EnvironmentView EnvironmentView => _environmentView;
    internal SettingsPanel SettingsPanel => _settingsPanel;
    internal SessionMenu SessionMenu => _sessionMenu;
    internal PauseMenu PauseMenu => _pauseMenu;
    internal ClientLifecycleController LifecycleController => _lifecycleController;
    internal bool IsMenuMode => _isMenuMode;
    internal CanvasLayer HudCanvas => _hudCanvas;
    internal MissionPanel? MissionPanel => _missionPanel;
    internal SelectionCard SelectionCard => _selectionCard;
    internal ContextActionPanel ContextActionPanel => _contextActionPanel;
    internal UnitCommandPanel CommandPanel => _commandPanel;
    internal HudTargetingMode CommandTargetingMode => _hudTargetingMode;
    internal OrderNotification OrderNotification => _orderNotification;
    internal RoleBadgeOverlay RoleBadgeOverlay => _roleBadgeOverlay;
    internal SelectionBoxOverlay SelectionBoxOverlay => _selectionBoxOverlay;
    internal SelectionController SelectionController => _selectionController;
    internal TacticalMinimap Minimap => _minimap;
    internal ReadOnlySpan<Rect2> InputOccludingPanels => _inputOccludingPanels;
    internal ReadOnlySpan<Rect2> BadgeOccludingPanels => _badgeOccludingPanels;
    internal UnitViewSet UnitViews => _unitViews;
    internal ContactViewSet ContactViews => _contactViews;
    internal WreckViewSet WreckViews => _wreckViews;
    internal IGameSession Session { get => _session; set => _session = value; }
    internal UnitCatalog Catalog => _catalog;
    internal CombatEffects CombatEffects => _combatEffects;
    internal SmokeView? SmokeView => _smokeView;
    internal OrderDestinationView DestinationMarker => _destination;
    internal HeightMap Map => _map;
    internal string Scenario => _scenario;
    internal OrderCursorOverlay OrderCursorOverlay => _orderCursorOverlay;
    internal OrderPreviewController PreviewController => _previewController;
    internal DestinationEvaluator DestinationEvaluator => _destinationEvaluator;

    public override void _Ready()
    {
        string[] userArgs = OS.GetCmdlineUserArgs();
        string[] allArgs = OS.GetCmdlineArgs();
        bool isLosSandbox = userArgs.Contains("--los-sandbox") || allArgs.Contains("--los-sandbox");
        bool isVehiclePreview = userArgs.Contains("--vehicle-preview") || allArgs.Contains("--vehicle-preview");
        string? mapPreviewTarget = null;
        for (int i = 0; i < userArgs.Length; i++)
        {
            if (userArgs[i] == "--map-preview" && i + 1 < userArgs.Length)
            {
                mapPreviewTarget = userArgs[i + 1].ToLowerInvariant();
                break;
            }
        }
        if (mapPreviewTarget is null)
        {
            for (int i = 0; i < allArgs.Length; i++)
            {
                if (allArgs[i] == "--map-preview" && i + 1 < allArgs.Length)
                {
                    mapPreviewTarget = allArgs[i + 1].ToLowerInvariant();
                    break;
                }
            }
        }
        bool isMapPreview = mapPreviewTarget is not null;
        bool hasConnect = userArgs.Contains("--connect") || allArgs.Contains("--connect");

        if ((isLosSandbox || isVehiclePreview || isMapPreview) && hasConnect)
        {
            string flag = isVehiclePreview ? "--vehicle-preview" : isLosSandbox ? "--los-sandbox" : "--map-preview";
            GD.PushError($"Invalid arguments: {flag} cannot be combined with --connect.");
            SetProcess(false);
            GetTree().Quit(1);
            return;
        }

        if (isMapPreview)
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            if (mapPreviewTarget != "ustye")
            {
                GD.PushError($"Unknown map preview target: '{mapPreviewTarget}'. Available: ustye");
                GetTree().Quit(1);
                return;
            }

            UstyeMapPreview preview = new();
            AddChild(preview);

            bool isSmoke = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
            if (isSmoke)
            {
                bool ok = UstyeMapPreviewRegressionTests.Run(preview);
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            return;
        }

        if (isLosSandbox)
        {
            bool isSmoke = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
            if (isSmoke)
            {
                bool ok = LosSandboxRegressionTests.Run();
                SetProcess(false);
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            SetProcess(false);
            SetProcessUnhandledInput(false);
            AddChild(new LosSandboxView());
            return;
        }

        if (isVehiclePreview)
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            bool isSmoke = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
            if (isSmoke)
            {
                bool ok = VehiclePreviewRegressionTests.Run(this);
                GetTree().Quit(ok ? 0 : 1);
                return;
            }

            AddChild(new VehiclePreviewView());
            return;
        }

        bool isPerfMicrobench = userArgs.Contains("--perf-microbench") || allArgs.Contains("--perf-microbench");
        if (isPerfMicrobench)
        {
            SetProcess(false);
            SetProcessUnhandledInput(false);
            Node3D benchRoot = new();
            AddChild(benchRoot);
            _ = ClientPerfProbe.RunMicrobenchmark(benchRoot).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"CLIENT_PERF_MICROBENCH_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }

        _smokeTest = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");

        bool isCombatProbe = userArgs.Contains("--combat-feedback-probe") || allArgs.Contains("--combat-feedback-probe");
        if (isCombatProbe)
        {
            SetProcess(false);
            _ = CombatFeedbackProbe.Run(this).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"COMBAT_FEEDBACK_PROBE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }

        string? connectTarget = null;
        string? captureDir = null;
        string? effectsCaptureDir = null;
        string? smokeCaptureDir = null;
        string? orderPreviewCaptureDir = null;
        float? overrideCameraSize = null;
        Vector3? overrideCameraFocus = null;
        int port = 7777;
        bool enemyHoldFire = false;
        bool noPrewarm = false;
        bool suspendFogCalc = false;
        bool minimapDemo = false;
        bool showMultiplayerMenu = false;
        string? menuErrorMessage = null;
        string? menuConnectionLostReason = null;

        void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--debug-hud")
                {
                    _debugHudVisible = true;
                }
                else if (args[i] == "--no-prewarm")
                {
                    noPrewarm = true;
                }
                else if (args[i] == "--suspend-fog-calc")
                {
                    suspendFogCalc = true;
                }
                else if (args[i] == "--connect" && i + 1 < args.Length)
                {
                    connectTarget = args[i + 1];
                    i++;
                }
                else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    port = p;
                    i++;
                }
                else if (args[i] == "--scenario" && i + 1 < args.Length)
                {
                    _scenario = args[i + 1].ToLowerInvariant();
                    i++;
                }
                else if (args[i] == "--setup" && i + 1 < args.Length && int.TryParse(args[i + 1], out int sIdx))
                {
                    _ustyeSetupIndex = Math.Clamp(sIdx, 0, 3);
                    i++;
                }
                else if (args[i] == "--screenshot" && i + 1 < args.Length)
                {
                    _screenshotPath = args[i + 1];
                    i++;
                }
                else if (args[i] == "--screenshot-on-finish" && i + 1 < args.Length)
                {
                    _screenshotOnFinishPath = args[i + 1];
                    i++;
                }
                else if (args[i] == "--screenshot-delay" && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out double delay))
                {
                    _screenshotTimer = delay;
                    i++;
                }
                else if (args[i] == "--screenshot-quit")
                {
                    _screenshotQuit = true;
                }
                else if (args[i] == "--forest-demo-capture" && i + 1 < args.Length)
                {
                    _forestDemoCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--transport-demo-capture" && i + 1 < args.Length)
                {
                    _transportDemoCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--enemy-hold-fire")
                {
                    enemyHoldFire = true;
                }
                else if (args[i] == "--camera-size" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float cSize))
                {
                    overrideCameraSize = cSize;
                    i++;
                }
                else if (args[i] == "--benchmark-zoom-to" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out float zoomTo))
                {
                    _benchmarkZoomToSize = zoomTo;
                    i++;
                }
                else if (args[i] == "--lod-budget" && i + 1 < args.Length && int.TryParse(args[i + 1], out int lodBudget))
                {
                    _lodBudgetPerFrame = Math.Max(1, lodBudget);
                    i++;
                }
                else if (args[i] == "--terrain-shadow-stride" && i + 1 < args.Length && int.TryParse(args[i + 1], out int shadowStride))
                {
                    _terrainShadowStride = Math.Max(1, shadowStride);
                    i++;
                }
                else if (args[i] == "--fullscreen")
                {
                    _fullscreenCli = true;
                }
                else if (args[i] == "--window-size" && i + 1 < args.Length)
                {
                    if (TryParseWindowSize(args[i + 1], out Vector2I parsedSize)) _windowSizeCli = parsedSize;
                    i++;
                }
                else if (args[i].StartsWith("--window-size=", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseWindowSize(args[i]["--window-size=".Length..], out Vector2I parsedSize)) _windowSizeCli = parsedSize;
                }
                else if (args[i] == "--camera-focus" && i + 1 < args.Length)
                {
                    string[] parts = args[i + 1].Split(',');
                    if (parts.Length == 2 &&
                        float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float fx) &&
                        float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float fz))
                    {
                        overrideCameraFocus = new Vector3(fx, 0, fz);
                    }
                    else if (parts.Length == 3 &&
                        float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out float fx3) &&
                        float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out float fy3) &&
                        float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out float fz3))
                    {
                        overrideCameraFocus = new Vector3(fx3, fy3, fz3);
                    }
                    i++;
                }
                else if (args[i] == "--vehicle-effects-capture" && i + 1 < args.Length)
                {
                    effectsCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--smoke-capture" && i + 1 < args.Length)
                {
                    smokeCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--order-preview-capture" && i + 1 < args.Length)
                {
                    orderPreviewCaptureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--combat-feedback-capture" && i + 1 < args.Length)
                {
                    captureDir = args[i + 1];
                    i++;
                }
                else if (args[i] == "--minimap-demo")
                {
                    minimapDemo = true;
                }
                else if (args[i] == "--menu-multiplayer")
                {
                    showMultiplayerMenu = true;
                }
                else if (args[i] == "--menu-error" && i + 1 < args.Length)
                {
                    showMultiplayerMenu = true;
                    menuErrorMessage = args[i + 1];
                    i++;
                }
                else if (args[i] == "--menu-connection-lost" && i + 1 < args.Length)
                {
                    menuConnectionLostReason = args[i + 1];
                    i++;
                }
            }
        }

        ParseArgs(allArgs);
        ParseArgs(userArgs);

        _isMenuMode = !ClientLifecycleController.HasExplicitCliMode(userArgs, allArgs);

        StaticGeometry? scenarioGeometry = null;
        MatchSetup setup;

        if (_isMenuMode)
        {
            _scenario = "battle";
            scenarioGeometry = BattleScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = BattleScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);
            _session = new NullSession();
        }
        else
        {
            if (_scenario == "visibility")
            {
                scenarioGeometry = VisibilityScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = VisibilityScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "navigation")
            {
                scenarioGeometry = NavigationScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = NavigationScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "navigation-large")
            {
                scenarioGeometry = LargeNavigationScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = LargeNavigationScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "combat")
            {
                scenarioGeometry = CombatScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = CombatScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "battle")
            {
                scenarioGeometry = BattleScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = BattleScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "recon-raid")
            {
                scenarioGeometry = ReconRaidScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = ReconRaidScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "bridge-crossing")
            {
                scenarioGeometry = BridgeCrossingScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = BridgeCrossingScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "vehicle-traffic")
            {
                scenarioGeometry = VehicleTrafficScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = VehicleTrafficScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "group-move")
            {
                scenarioGeometry = GroupMoveScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = GroupMoveScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "ustye-ground")
            {
                scenarioGeometry = UstyeGroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = UstyeGroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "ustye-raid")
            {
                scenarioGeometry = UstyeRaidScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = UstyeRaidScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "forest-visibility")
            {
                scenarioGeometry = ForestVisibilityScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = ForestVisibilityScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "infantry-playground")
            {
                scenarioGeometry = InfantryPlaygroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = InfantryPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "infantry-transport")
            {
                scenarioGeometry = InfantryTransportScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = InfantryTransportScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "helicopter-playground")
            {
                scenarioGeometry = HelicopterPlaygroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = HelicopterPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "artillery-playground")
            {
                scenarioGeometry = ArtilleryPlaygroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = ArtilleryPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "air-defense-playground")
            {
                scenarioGeometry = AirDefensePlaygroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = AirDefensePlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "urban-destruction-playground")
            {
                scenarioGeometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = UrbanDestructionPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            }
            else if (_scenario == "tank-battle")
            {
                scenarioGeometry = TankBattleScenario.CreateStaticGeometry();
                _map = scenarioGeometry.HeightMap;
                setup = TankBattleScenario.CreateMatchSetup(scenarioGeometry, ColdWarContent.LoadCatalog());
            }
            else
            {
                _map = HeightMap.CreatePrototype();
                setup = PrototypeScenario.CreateMatchSetup(_map);
            }
            if (scenarioGeometry is null)
            {
                scenarioGeometry = new StaticGeometry(_map, ReadOnlySpan<OpaqueStructure>.Empty);
            }
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);

            if (connectTarget is not null)
            {
                string host = connectTarget;
                if (connectTarget.Contains(':'))
                {
                    string[] parts = connectTarget.Split(':');
                    host = parts[0];
                    if (int.TryParse(parts[1], out int p)) port = p;
                }
                _session = ClientSession.Connect(host, port, setup);
            }
            else
            {
                Simulation localSimulation = _scenario switch
                {
                    "visibility" => VisibilityScenario.CreateSimulation(scenarioGeometry!),
                    "navigation" => NavigationScenario.CreateSimulation(scenarioGeometry!),
                    "navigation-large" => LargeNavigationScenario.CreateSimulation(scenarioGeometry!),
                    "combat" => CombatScenario.CreateSimulation(scenarioGeometry!),
                    "recon-raid" => ReconRaidScenario.CreateSimulation(scenarioGeometry!),
                    "battle" => BattleScenario.CreateSimulation(scenarioGeometry!),
                    "bridge-crossing" => BridgeCrossingScenario.CreateSimulation(scenarioGeometry!),
                    "vehicle-traffic" => VehicleTrafficScenario.CreateSimulation(scenarioGeometry!),
                    "group-move" => GroupMoveScenario.CreateSimulation(scenarioGeometry!),
                    "ustye-ground" => UstyeGroundScenario.CreateSimulation(scenarioGeometry!, setupIndex: _ustyeSetupIndex),
                    "ustye-raid" => UstyeRaidScenario.CreateSimulation(scenarioGeometry!),
                    "forest-visibility" => ForestVisibilityScenario.CreateSimulation(scenarioGeometry!),
                    "infantry-playground" => InfantryPlaygroundScenario.CreateSimulation(scenarioGeometry!),
                    "infantry-transport" => InfantryTransportScenario.CreateSimulation(scenarioGeometry!),
                    "helicopter-playground" => HelicopterPlaygroundScenario.CreateSimulation(scenarioGeometry!),
                    "artillery-playground" => ArtilleryPlaygroundScenario.CreateSimulation(scenarioGeometry!),
                    "air-defense-playground" => AirDefensePlaygroundScenario.CreateSimulation(scenarioGeometry!),
                    "urban-destruction-playground" => UrbanDestructionPlaygroundScenario.CreateSimulation(scenarioGeometry!),
                    "tank-battle" => TankBattleScenario.CreateSimulation(scenarioGeometry!),
                    _ => PrototypeScenario.Create(_map)
                };
                // Test switch: the enemy holds fire, so own units can be tried out without losing them.
                if (enemyHoldFire) TestOptions.HoldFireForOthers(localSimulation, 1);
                _session = new LocalSession(localSimulation);
            }
        }


        string[] combinedArgs = allArgs.Concat(userArgs).ToArray();
        _benchmarkOverrides = DisplaySettings.ParseBenchmarkOverrides(combinedArgs);
        SettingsPanel.ActiveBenchmarkOverrides = _benchmarkOverrides;

        AddChild(_environmentRoot);
        _fogOfWar = new FogOfWarView(
            _map,
            scenarioGeometry,
            useLambertDiffuse: _benchmarkOverrides.TerrainShader != "burley");
        BuildEnvironment(scenarioGeometry);

        DisplaySettings loadedConfig = SettingsPanel.LoadConfig();
        bool cliOverridesVsync = DisplaySettings.HasVsyncOverride(combinedArgs);
        bool cliOverridesMaxFps = DisplaySettings.HasMaxFpsOverride(combinedArgs);
        bool cliOverridesProfile = DisplaySettings.HasEnvironmentOverride(combinedArgs);
        bool cliOverridesQuality = DisplaySettings.HasQualityOverride(combinedArgs);
        bool cliOverridesRenderScale = DisplaySettings.HasRenderScaleOverride(combinedArgs);
        bool cliOverridesAntiAliasing = DisplaySettings.HasAntiAliasingOverride(combinedArgs);
        bool cliOverridesSsao = DisplaySettings.HasSsaoOverride(combinedArgs);
        DisplaySettings activeSettings = DisplaySettings.ParseCommandLine(combinedArgs, loadedConfig);
        _benchmarkQuit = activeSettings.BenchmarkQuit;

        _environmentView = new EnvironmentView();
        AddChild(_environmentView);
        string scenarioDefault = _scenario switch
        {
            "ustye-ground" or "ustye-raid" => "overcast",
            _ => "overcast"
        };
        string profileName = scenarioDefault;
        if (SettingsPanel.TryLoadSavedEnvironmentProfile(out string savedProfile))
        {
            profileName = savedProfile;
        }
        if (cliOverridesProfile)
        {
            profileName = activeSettings.EnvironmentProfile;
        }
        EnvironmentProfile activeProfile = EnvironmentProfile.Load(profileName);
        _environmentView.ApplyProfile(activeProfile, activeSettings.Quality);
        _environmentView.SetDisplayOutputAndSsao(activeSettings.AmbientOcclusion, SettingsPanel.ResolveWindowPixelSize(GetViewport()));
        _environmentView.ApplyBenchmarkOverrides(_benchmarkOverrides);

        if (_ustyeMapView is not null)
        {
            _ustyeMapView.ApplyEnvironmentTints(activeProfile.MaterialTints);
        }

        _settingsPanel = new SettingsPanel();
        AddChild(_settingsPanel);
        _settingsPanel.Initialize(
            loadedConfig,
            activeSettings,
            cliOverridesVsync,
            cliOverridesMaxFps,
            cliOverridesProfile,
            cliOverridesQuality,
            cliOverridesRenderScale,
            cliOverridesAntiAliasing,
            cliOverridesSsao);

        _settingsPanel.SettingsChanged += newSettings =>
        {
            _benchmarkOverrides = SettingsPanel.ActiveBenchmarkOverrides ?? _benchmarkOverrides;
            _environmentView.ApplyBenchmarkOverrides(SettingsPanel.ActiveBenchmarkOverrides);
            SettingsPanel.ApplyWindowRenderPolicy(GetWindow(), GetViewport(), newSettings);
            RefreshCachedTerrainViewportHeight(GetViewport());
            EnvironmentProfile profile = EnvironmentProfile.Load(newSettings.EnvironmentProfile);
            _environmentView.SetDisplayOutputAndSsao(newSettings.AmbientOcclusion, SettingsPanel.ResolveWindowPixelSize(GetViewport()));
            _environmentView.ApplyProfile(profile, newSettings.Quality);
            _environmentView.UpdateShadowTierForCamera(_cameraRig.Size, _activeShadowMapLayers);
            if (_ustyeMapView is not null)
            {
                _ustyeMapView.ApplyEnvironmentTints(profile.MaterialTints);
            }
            _smokeView?.ApplyEnvironment(profile);
        };

        if (_benchmarkOverrides.ScreenIndex.HasValue && !SettingsPanel.IsHeadlessDisplay)
        {
            int screenCount = Math.Max(1, DisplayServer.GetScreenCount());
            DisplayServer.WindowSetCurrentScreen(Math.Clamp(_benchmarkOverrides.ScreenIndex.Value, 0, screenCount - 1));
        }

        if (_windowSizeCli.HasValue && !SettingsPanel.IsHeadlessDisplay)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetSize(_windowSizeCli.Value);
        }
        if (_fullscreenCli && !SettingsPanel.IsHeadlessDisplay)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
        }

        OnWindowMetricsChanged();
        GetViewport().SizeChanged += OnWindowMetricsChanged;
        if (GetWindow() is Window rootWin)
        {
            rootWin.DpiChanged += OnWindowMetricsChanged;
        }

        if (activeSettings.BenchmarkFrames > 0 || _benchmarkOverrides.BenchmarkSeconds > 0f)
        {
            _isTimedBenchmark = _benchmarkOverrides.BenchmarkSeconds > 0f;
            _benchmarkActive = true;
            _benchmarkWarmupCompleted = false;
            _benchmarkWarmupRemainingSec = _benchmarkOverrides.WarmupSeconds;
            _benchmarkElapsedSec = 0f;
            int initialCapacity = _isTimedBenchmark
                ? Math.Clamp((int)Math.Ceiling(_benchmarkOverrides.BenchmarkSeconds * 600f), 2048, 300_000)
                : activeSettings.BenchmarkFrames + 16;
            _benchmarkTargetFrames = _isTimedBenchmark ? 0 : activeSettings.BenchmarkFrames;
            _benchmarkMetrics = new FramePacingMetrics(initialCapacity);
            if (_benchmarkOverrides.BenchmarkSeconds >= 120f)
            {
                _minuteWindowMetrics = new FramePacingMetrics(65536);
            }
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        }

        float initialCameraSize = overrideCameraSize ?? _scenario switch
        {
            "combat" => 75,
            "navigation-large" => 450,
            "recon-raid" => 160,
            "battle" => 160,
            "bridge-crossing" => 120,
            "vehicle-traffic" => 150,
            "group-move" => 140,
            "ustye-ground" => 220,
            "ustye-raid" => 160,
            "forest-visibility" => 150,
            "infantry-playground" => 120,
            "infantry-transport" => 120,
            "helicopter-playground" => 180,
            "artillery-playground" => 220,
            "air-defense-playground" => 440,
            "urban-destruction-playground" => 155,
            "tank-battle" => 160,
            _ => 112
        };
        _camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = initialCameraSize, Far = 3500, Current = true };
        AddChild(_camera);
        Vector3 initialFocus = overrideCameraFocus ?? _scenario switch
        {
            "combat" => new Vector3(45, 0, 45),
            "navigation-large" => new Vector3(300, 0, 300),
            "recon-raid" => new Vector3(100, 0, 100),
            "battle" => new Vector3(BattleScenario.SovietStart.X, 0, BattleScenario.SovietStart.Y),
            "bridge-crossing" => new Vector3(100, 4, 100),
            "vehicle-traffic" => new Vector3(100, 0, 100),
            "group-move" => new Vector3(60, 0, 50),
            "ustye-ground" => new Vector3(2550f, 4.5f, 2450f),
            "ustye-raid" => new Vector3(2160f, 10f, 2325f),
            "forest-visibility" => new Vector3(100f, 0f, 110f),
            "infantry-playground" => new Vector3(130f, 0f, 180f),
            "infantry-transport" => new Vector3(128f, 0f, 195f),
            "helicopter-playground" => new Vector3(HelicopterPlaygroundScenario.ChinookAlphaPos.X + 20f, 2f, HelicopterPlaygroundScenario.ChinookAlphaPos.Y),
            "artillery-playground" => new Vector3(1024f, 4f, ArtilleryPlaygroundScenario.GradAlphaPos.Y),
            "air-defense-playground" => new Vector3(1024f, 12f, 920f),
            "urban-destruction-playground" => new Vector3(500f, 4f, 470f),
            "tank-battle" => new Vector3(TankBattleScenario.Center.X - TankBattleScenario.StartOffset, 4f, TankBattleScenario.Center.Y),
            _ => new Vector3(64, 0, 64)
        };
        _hasExplicitCameraFocus = overrideCameraFocus.HasValue;
        _initialSpawnFocus = initialFocus;
        _initialSpawnSize = initialCameraSize;
        _cameraRig = new CameraRig(_camera, initialFocus, initialCameraSize, _map.Width, _map.Depth);
        if (_scenario == "ustye-ground" || _scenario == "ustye-raid" || _scenario == "tank-battle" || _scenario == "artillery-playground" || _scenario == "air-defense-playground" || _scenario == "urban-destruction-playground")
        {
            _cameraRig.Model.SetSizeLimits(30f, 5500f);
            _cameraRig.FarClipMargin = 3000f;
            _cameraRig.GroundHeight = (x, z) => _map.GetHeight(x, z);
        }
        _cameraRig.SetDefaultView(initialFocus, initialCameraSize);
        _environmentView?.UpdateShadowTierForCamera(_cameraRig.Size, _activeShadowMapLayers);
        UpdateTerrainLodStep(_terrainView, _camera, _cachedTerrainViewportHeightPx);

        // Historical scenarios use the Cold War catalog; the small test maps keep prototype data.
        _catalog = ScenarioContent.LoadCatalog(_scenario);
        if (!noPrewarm)
        {
            VehicleVisualCatalog.PrewarmScenario(setup, _catalog);
        }
        if (suspendFogCalc && _fogOfWar is not null)
        {
            _fogOfWar.IsCalculationSuspended = true;
        }
        if (_session.Replica.OwnUnits.Length > 0)
        {
            _fogOfWar?.UpdateAllNow(_session.Replica, _catalog);
        }
        // Factions are part of the public match setup; the side is known once the session assigns it.
        MatchSetup matchSetup = _matchSetup;
        _unitViews = new UnitViewSet(this, _map, _catalog, () => matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _contactViews = new ContactViewSet(this, _map, _catalog, () => matchSetup.GetHostileFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _wreckViews = new WreckViewSet(this, _map, _catalog, () => matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _unitViews.UpdateLod(_cameraRig.Size);
        _contactViews.UpdateLod(_cameraRig.Size);
        _wreckViews.UpdateLod(_cameraRig.Size);
        _buildingDestructionView?.UpdateLod(_cameraRig.Size);
        _combatEffects = new CombatEffects(_map, _scenarioGeometry);
        _combatEffects.OnBlastImpulse = _cameraRig.AddBlastShake;
        AddChild(_combatEffects);
        bool enableAudio = !_smokeTest && !string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);
        _helicopterAudio = new HelicopterAudioController(this, enabled: enableAudio);
        _smokeView = new SmokeView(_map, _scenarioGeometry);
        if (_environmentView?.ActiveProfile is not null)
        {
            _smokeView.ApplyEnvironment(_environmentView.ActiveProfile);
        }
        AddChild(_smokeView);

        if (!_isMenuMode)
        {
            if (_session is LocalSession && _scenario == "prototype")
            {
                foreach (OwnUnitObservation unit in _session.Replica.OwnUnits)
                {
                    // Demo movement is sent as normal client commands through the byte transport.
                    _session.Move(unit.Id, unit.Position + new NumericsVector2(0, 60));
                }
                if (_session.Replica.OwnUnits.Length > 0) _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "combat" && _session.Replica.OwnUnits.Length > 1)
            {
                // Auto-select first tank for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[1].Id);
            }
            else if (_scenario == "navigation-large" && _session.Replica.OwnUnits.Length > 0)
            {
                // Auto-select first recon unit for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "recon-raid" && _session.Replica.OwnUnits.Length > 0)
            {
                // Auto-select player recon unit for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "bridge-crossing" && _session.Replica.OwnUnits.Length > 0)
            {
                // Auto-select player tank for immediate playability
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "vehicle-traffic" && _session.Replica.OwnUnits.Length > 1)
            {
                _unitViews.Selected.Add(_session.Replica.OwnUnits[1].Id);
            }
            else if (_scenario == "group-move" && _session.Replica.OwnUnits.Length >= 10)
            {
                for (int i = 0; i < 10; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-ground" && _session.Replica.OwnUnits.Length >= 6)
            {
                for (int i = 0; i < 6; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-raid" && _session.Replica.OwnUnits.Length >= 3)
            {
                for (int i = 0; i < 3; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "forest-visibility" && _session.Replica.OwnUnits.Length >= 1)
            {
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "infantry-playground" && _session.Replica.OwnUnits.Length >= 1)
            {
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
            else if (_scenario == "infantry-transport" && _session.Replica.OwnUnits.Length >= 1)
            {
                _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
            }
        }

        _destination = new OrderDestinationView();
        AddChild(_destination);
        _collisionDebugMeshInstance.Mesh = _collisionDebugMesh;
        AddChild(_collisionDebugMeshInstance);
        _vegetationDebugMeshInstance.Mesh = _vegetationDebugMesh;
        AddChild(_vegetationDebugMeshInstance);
        BuildHud();
        _previewController = new OrderPreviewController(
            _destinationEvaluator,
            _scenarioGeometry,
            _map,
            _catalog,
            _orderCursorOverlay,
            _destination,
            _orderNotification);
        _previewController.SetLineOfSightContext(_clientLosContext);
        _unitViews.UpdateLod(_cameraRig.Size);
        _contactViews.UpdateLod(_cameraRig.Size);
        _wreckViews.UpdateLod(_cameraRig.Size);
        _buildingDestructionView?.UpdateLod(_cameraRig.Size);
        UpdateViews();

        if (activeSettings.CaptureViewsDir is not null)
        {
            UstyePlanData plan = UstyePlanData.Load();
            _viewCapture = new EnvironmentViewCapture(this, activeSettings.CaptureViewsDir, plan, _map, _cameraRig, _combatEffects, _unitViews, _session);
        }

        _lifecycleController = new ClientLifecycleController();
        _lifecycleController.OnSessionReady += session => EnterGameSession(session);
        _lifecycleController.OnConnectionFailed += err => _sessionMenu.SetStatus(err, ClientLifecycleState.Failed);
        _lifecycleController.OnConnectionLost += err => OnNetworkSessionDisconnected(err);

        _sessionMenu = new SessionMenu();
        AddChild(_sessionMenu);
        _sessionMenu.LocalBattleRequested += StartLocalBattle;
        _sessionMenu.ConnectRequested += StartMultiplayerConnect;
        _sessionMenu.CancelRequested += CancelMultiplayerConnect;
        _sessionMenu.SettingsRequested += () => _settingsPanel.Toggle();
        _sessionMenu.ExitRequested += () => GetTree().Quit();

        _pauseMenu = new PauseMenu();
        AddChild(_pauseMenu);
        _pauseMenu.ResumeRequested += () => { };
        _pauseMenu.SettingsRequested += () => _settingsPanel.Toggle();
        _pauseMenu.LeaveRequested += LeaveCurrentMatch;
        _pauseMenu.ExitRequested += () => GetTree().Quit();

        if (_isMenuMode)
        {
            _hudCanvas.Visible = false;
            if (showMultiplayerMenu)
            {
                _sessionMenu.ShowMultiplayerView();
                if (menuErrorMessage is not null)
                {
                    _sessionMenu.SetStatus(menuErrorMessage, ClientLifecycleState.Failed);
                }
            }
            else if (menuConnectionLostReason is not null)
            {
                _sessionMenu.ShowConnectionLostModal(menuConnectionLostReason);
            }
            else
            {
                _sessionMenu.ShowMainMenuView();
            }
        }
        else
        {
            _hudCanvas.Visible = true;
            _sessionMenu.HideMenu();
        }

        if (minimapDemo && _scenario == "ustye-raid" && _session is LocalSession && _screenshotPath is not null)
        {
            SetupMinimapDemo();
        }

        if (_forestDemoCaptureDir is not null && _scenario == "forest-visibility")
        {
            _showVegetationDebug = true;
            _cameraRig.Model.CurrentSize = 130f;
            _cameraRig.Model.SetZoom(130f);
            _cameraRig.CenterOn(new Vector3(95f, 0f, 110f));
            UpdateVegetationDebugOverlay();
            _forestDemoStage = 0;
            _forestDemoTimer = 0.8;
        }

        if (_transportDemoCaptureDir is not null && _scenario == "infantry-transport")
        {
            _cameraRig.Model.CurrentSize = 80f;
            _cameraRig.Model.SetZoom(80f);
            _cameraRig.CenterOn(new Vector3(128f, 0f, 205f));
            _transportDemoStage = 0;
            _transportDemoTimer = 0.8;
        }

        if (_smokeTest && !ClientRegressionTests.Run(this))
        {
            SetProcess(false);
            GetTree().Quit(1);
            return;
        }

        GD.Print($"M0_CLIENT_READY runtime={System.Environment.Version} own_units={_session.Replica.OwnUnits.Length}");

        bool isPerfProbe = userArgs.Contains("--perf-probe") || allArgs.Contains("--perf-probe");
        if (isPerfProbe)
        {
            SetProcess(false);
            _ = ClientPerfProbe.Run(this, noPrewarm).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"CLIENT_PERF_PROBE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return;
        }

        if (effectsCaptureDir is not null)
        {
            _ = VehicleEffectsProbe.RunCapture(this, effectsCaptureDir).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"VEHICLE_EFFECTS_CAPTURE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        if (smokeCaptureDir is not null)
        {
            _ = SmokeVisualProbe.RunCapture(this, smokeCaptureDir).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"SMOKE_VISUAL_CAPTURE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        if (captureDir is not null)
        {
            _ = CombatFeedbackProbe.RunVisualCapture(this, captureDir).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"COMBAT_FEEDBACK_CAPTURE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }

        if (orderPreviewCaptureDir is not null)
        {
            _ = OrderPreviewProbe.RunCapture(this, orderPreviewCaptureDir).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError($"ORDER_PREVIEW_CAPTURE_FAULT: {task.Exception}");
                    GetTree().Quit(1);
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    private void BuildEnvironment(StaticGeometry? scenarioGeometry)
    {
        ClearEnvironment();

        if (_scenario == "ustye-ground" || _scenario == "ustye-raid")
        {
            UstyePlanData plan = UstyePlanData.Load();
            _ustyeMapView = new(plan, includeScaleVehicles: false, fogOverlay: _fogOfWar);
            _activeShadowMapLayers = _ustyeMapView;
            _environmentRoot.AddChild(_ustyeMapView);
            _terrainWindingValid = !_smokeTest || _ustyeMapView.HasUpwardFrontFaces();
            if (_environmentView is not null)
            {
                _ustyeMapView.ApplyEnvironmentTints(_environmentView.ActiveProfile.MaterialTints);
            }
        }
        else
        {
            TerrainView terrain = TerrainView.Create(_map, _fogOfWar, shadowStride: _terrainShadowStride);
            _terrainView = terrain;
            _activeShadowMapLayers = terrain;
            _environmentRoot.AddChild(terrain);
            _terrainWindingValid = !_smokeTest || terrain.HasUpwardFrontFaces();
            MeshInstance3D grid = TerrainView.CreateGrid(_map);
            terrain.AttachGrid(grid);
            _environmentRoot.AddChild(grid);

            if (scenarioGeometry is not null)
            {
                if (scenarioGeometry.DestructibleBuildings.Count > 0)
                {
                    _buildingDestructionView = new BuildingDestructionView(
                        _environmentRoot,
                        scenarioGeometry,
                        scenarioGeometry.DestructibleBuildings,
                        OnObservedStructureHeightChanged);
                    _buildingGarrisonView = new BuildingGarrisonView(
                        _environmentRoot,
                        scenarioGeometry,
                        scenarioGeometry.DestructibleBuildings,
                        _garrisonBadgeRoot,
                        OnGarrisonBadgeClicked);
                    if (_cameraRig is not null)
                    {
                        _buildingDestructionView.UpdateLod(_cameraRig.Size);
                    }
                    SyncClientLosOverrides();
                }
                SetupStructures(scenarioGeometry);
            }
        }
    }

    private void OnObservedStructureHeightChanged(StructureId structureId, float newMaxY)
    {
        _fogOfWar?.UpdateStructureHeight(structureId, newMaxY);
        SyncClientLosOverrides();
    }

    private void SyncClientLosOverrides()
    {
        if (_buildingDestructionView is not null)
        {
            float[] overrides = _buildingDestructionView.ObservedMaxYOverrides;
            _clientLosContext.SetStructureMaxYOverrides(overrides, overrides.Length);
        }
        else
        {
            _clientLosContext.SetStructureMaxYOverrides(null, 0);
        }
        _previewController?.SetLineOfSightContext(_clientLosContext);
    }

    internal void RefreshCachedTerrainViewportHeight(Viewport? viewport = null)
    {
        _cachedTerrainViewportHeightPx = TerrainView.GetEffectiveViewportPixelHeight(viewport ?? GetViewport());
    }

    internal void OnWindowMetricsChanged()
    {
        Window? win = GetWindow();
        Viewport? vp = GetViewport();
        DisplaySettings cur = _settingsPanel?.CurrentSettings ?? DisplaySettings.CreateDefault();
        SettingsPanel.ApplyWindowRenderPolicy(win, vp, cur);
        _environmentView?.SetDisplayOutputAndSsao(cur.AmbientOcclusion, SettingsPanel.ResolveWindowPixelSize(vp));
        RefreshCachedTerrainViewportHeight(vp);
    }

    private static bool TryParseWindowSize(string raw, out Vector2I size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        int sep = raw.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (sep <= 0 || sep >= raw.Length - 1) return false;
        if (int.TryParse(raw.AsSpan(0, sep), out int w) &&
            int.TryParse(raw.AsSpan(sep + 1), out int h) &&
            w >= 320 && h >= 200)
        {
            size = new Vector2I(w, h);
            return true;
        }
        return false;
    }

    internal static void UpdateTerrainLodStep(TerrainView? terrain, Camera3D? camera, float cachedViewportHeightPx)
    {
        terrain?.UpdateLod(camera, cachedViewportHeightPx);
    }

    private void ClearEnvironment()
    {
        _terrainView = null;
        _activeShadowMapLayers = null;
        _environmentView?.SetShadowTarget(null);
        _buildingDestructionView?.Dispose();
        _buildingDestructionView = null;
        _buildingGarrisonView?.Dispose();
        _buildingGarrisonView = null;
        _hoveredGarrisonStructure = default;
        _pendingAttackGroundMinSeq = 0;
        _pendingAttackGroundMaxSeq = 0;
        _lastHandledAttackGroundResultSeq = 0;
        _clientLosContext.SetStructureMaxYOverrides(null, 0);
        foreach (Node child in _environmentRoot.GetChildren())
        {
            _environmentRoot.RemoveChild(child);
            child.QueueFree();
        }
        _ustyeMapView = null;
    }

    private void SetupStructures(StaticGeometry geometry)
    {
        StandardMaterial3D buildingMat = new() { AlbedoColor = new Color("5c5346"), Roughness = 0.85f };
        StandardMaterial3D wallMat = new() { AlbedoColor = new Color("6b756c"), Roughness = 0.9f };

        foreach (OpaqueStructure structure in geometry.Structures)
        {
            if (_buildingDestructionView?.ContainsStructure(structure.Id) == true)
                continue;
            float sizeX = structure.Max.X - structure.Min.X;
            float sizeY = structure.Max.Y - structure.Min.Y;
            float sizeZ = structure.Max.Z - structure.Min.Z;
            float posX = (structure.Min.X + structure.Max.X) * 0.5f;
            float posY = (structure.Min.Y + structure.Max.Y) * 0.5f;
            float posZ = (structure.Min.Z + structure.Max.Z) * 0.5f;

            _environmentRoot.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(sizeX, sizeY, sizeZ) },
                Position = new Vector3(posX, posY, posZ),
                MaterialOverride = structure.Id.Value == 1 ? buildingMat : wallMat
            });
        }

        StandardMaterial3D bridgeMat = new() { AlbedoColor = new Color("454c4d"), Roughness = 0.85f };
        foreach (BridgeStructure bridge in geometry.Bridges)
        {
            System.Numerics.Vector2 delta = bridge.End - bridge.Start;
            float length = delta.Length();
            System.Numerics.Vector2 mid = (bridge.Start + bridge.End) * 0.5f;
            float midHeight = (bridge.StartHeight + bridge.EndHeight) * 0.5f - bridge.Thickness * 0.5f;
            float yaw = MathF.Atan2(delta.X, delta.Y);

            _environmentRoot.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(bridge.Width, bridge.Thickness, length) },
                Position = new Vector3(mid.X, midHeight, mid.Y),
                Rotation = new Vector3(0, yaw, 0),
                MaterialOverride = bridgeMat
            });
        }

        StandardMaterial3D forestMat = new() { AlbedoColor = new Color(0.12f, 0.35f, 0.15f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        foreach (TerrainObstacle obs in geometry.TerrainObstacles)
        {
            if ((obs.RequiredCapability & TraversabilityCapabilities.DenseForest) != 0)
            {
                float sizeX = obs.MaxX - obs.MinX;
                float sizeZ = obs.MaxZ - obs.MinZ;
                float posX = (obs.MinX + obs.MaxX) * 0.5f;
                float posZ = (obs.MinZ + obs.MaxZ) * 0.5f;
                float height = 4.0f;
                float groundY = geometry.HeightMap.GetHeight(posX, posZ);
                _environmentRoot.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(sizeX, height, sizeZ) },
                    Position = new Vector3(posX, groundY + height * 0.5f, posZ),
                    MaterialOverride = forestMat
                });
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_sessionMenu?.IsOpen == true)
        {
            if (_lifecycleController.IsConnecting)
            {
                _lifecycleController.Advance(delta);
                _sessionMenu.SetStatus(_lifecycleController.StatusDescription, _lifecycleController.State);
            }
            _cameraRig.Update(delta, allowInput: false);
            _environmentView?.UpdateHazeForCamera(_cameraRig.Size, _cameraRig.Focus, _cameraRig.ViewPitch);
            _environmentView?.UpdateShadowTierForCamera(_cameraRig.Size, _activeShadowMapLayers);
            _environmentView?.UpdateShadowFade(delta);
            ProcessScreenshot(delta);
            ProcessForestDemoCapture(delta);
            ProcessTransportDemoCapture(delta);
            _viewCapture?.Process(delta);
            return;
        }

        if (_session is null) return;

        long frameStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        float wallClockIntervalMs = 0f;
        if (_lastFrameTimestamp > 0)
        {
            wallClockIntervalMs = (float)((frameStartTimestamp - _lastFrameTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }
        _lastFrameTimestamp = frameStartTimestamp;

        bool collectBench = _benchmarkMetrics is not null;
        long sessionStart = collectBench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (_lifecycleController.NetworkSession is not null && ReferenceEquals(_lifecycleController.NetworkSession, _session))
        {
            _lifecycleController.Advance(delta);
            if (_lifecycleController.State == ClientLifecycleState.Disconnected)
            {
                return;
            }
        }
        else
        {
            _session.Advance(delta);
        }
        long fogStart = collectBench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _fogOfWar?.Update((float)delta, _session.Replica, _catalog);
        long viewsStart = collectBench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        UpdateViews(delta);
        _unitViews.FaceHealthBars(_camera.GlobalTransform.Basis);
        int lodInstantiationBudget = _lodBudgetPerFrame;
        int lodSwitches = 0;
        lodSwitches += _unitViews.UpdateLod(_cameraRig.Size, ref lodInstantiationBudget);
        lodSwitches += _contactViews.UpdateLod(_cameraRig.Size, ref lodInstantiationBudget);
        lodSwitches += _wreckViews.UpdateLod(_cameraRig.Size, ref lodInstantiationBudget);
        _buildingDestructionView?.UpdateLod(_cameraRig.Size);
        long effectsStart = collectBench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _wreckViews.UpdateEffects(delta, _cameraRig.Focus, _cameraRig.Size, _session.Tick);
        _unitViews.UpdateRecoil(delta);
        _unitViews.UpdateEffects(delta);
        _combatEffects.Update(delta);
        _combatEffects.SyncGuidedMissiles(_session.Replica, _session.RenderTick, (float)delta);
        _combatEffects.ProcessEvents(_session.Replica.Events, _unitViews, _session.Tick, _contactViews);
        _combatEffects.UpdateMissileWarningAudio(_session.Replica, (float)delta);
        _wreckViews.ProcessEvents(_session.Replica.Events, _session.Tick);
        _smokeView?.Update((float)delta, _session.Replica, _session.RenderTick);
        _buildingDestructionView?.Update(_session.Replica, (float)delta);
        if (_buildingGarrisonView is not null)
        {
            StructureId hoverStruct = default;
            if (_previewController is not null && !IsUiOccludingMouse())
            {
                Vector2 mPos = GetViewport().GetMousePosition();
                if (_buildingGarrisonView.TryGetBadgeAtScreenPoint(mPos, out StructureId badgeStruct))
                {
                    hoverStruct = badgeStruct;
                }
                else if (_previewController.TryRaycast(_camera, mPos, out _, out _, out _, out StructureId rayStruct) &&
                         _buildingGarrisonView.IsGarrisonable(rayStruct))
                {
                    hoverStruct = rayStruct;
                }
            }
            _hoveredGarrisonStructure = hoverStruct;
            bool showEntranceHint = _unitViews.Selected.Count > 0 &&
                (HasSelectedGarrisonableInfantry() || _contextActionPanel?.TargetingMode == ContextTargetingMode.EnterBuilding);
            _buildingGarrisonView.Update(_session.Replica, _unitViews.Selected, _hoveredGarrisonStructure, showEntranceHint, _camera);
        }
        if (_pendingAttackGroundMaxSeq != 0 &&
            _session.LastCommandResult is { } agResult &&
            agResult.Sequence != _lastHandledAttackGroundResultSeq &&
            agResult.Sequence >= _pendingAttackGroundMinSeq &&
            agResult.Sequence <= _pendingAttackGroundMaxSeq)
        {
            _lastHandledAttackGroundResultSeq = agResult.Sequence;
            string agMsg = agResult.Queued
                ? $"Attacking {_pendingAttackGroundTargetDesc}"
                : $"Attack {_pendingAttackGroundTargetDesc} order rejected by server";
            _orderNotification?.ShowNotification(agMsg);
            _status.Text = agMsg;
            if (agResult.Sequence >= _pendingAttackGroundMaxSeq)
            {
                _pendingAttackGroundMinSeq = 0;
                _pendingAttackGroundMaxSeq = 0;
            }
        }
        _combatEffects.UpdateListener(_cameraRig.Focus);
        _helicopterAudio?.Update(_session.Replica, _cameraRig.Focus, _cameraRig.Size, delta);
        _missionPanel?.UpdateMission(_session.Mission, _session.Tick);

        if (_debugHudVisible)
        {
            string sessionDescription = _session.StatusDescription;
            if (!string.Equals(_lastSubtitleText, sessionDescription, StringComparison.Ordinal))
            {
                _lastSubtitleText = sessionDescription;
                _subtitle.Text = sessionDescription;
            }
        }
        if (_title is not null && (_session.AssignedPlayerId != _lastTitlePlayerId || _scenario != _lastTitleScenario))
        {
            _lastTitlePlayerId = _session.AssignedPlayerId;
            _lastTitleScenario = _scenario;
            _title.Text = FormatTitle(_session.AssignedPlayerId, _scenario);
        }

        if (_session.AssignedPlayerId == 0)
        {
            _homeViewAssignedPlayerId = 0;
        }
        else if (_session.AssignedPlayerId != _homeViewAssignedPlayerId)
        {
            _homeViewAssignedPlayerId = _session.AssignedPlayerId;
            ApplyPlayerHomeView(_session.AssignedPlayerId);
        }

        _orderNotification?.Update(delta);
        UpdateSelectionPresentation();
        long previewStart = collectBench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _previewController?.UpdateOrders(_session, delta);

        if (!_settingsPanel.IsOpen && !_pauseMenu.IsOpen && !_helpPanel.IsOpen)
        {
            Vector2 mousePos = SettingsPanel.IsHeadlessDisplay && _headlessPointerPosition.HasValue
                ? _headlessPointerPosition.Value
                : GetViewport().GetMousePosition();
            Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
            bool isOccluded = !new Rect2(Vector2.Zero, viewportSize).HasPoint(mousePos);
            for (int i = 0; i < _inputOccludingPanels.Length; i++)
            {
                if (_inputOccludingPanels[i].HasPoint(mousePos))
                {
                    isOccluded = true;
                    break;
                }
            }

            if (isOccluded && _previewController?.IsFacingGestureActive != true)
            {
                _orderCursorOverlay?.HideTooltip();
                _destination.HideGhost();
                _destination.ClearGroupPreviewSlots();
            }
            else if (_hudTargetingMode is HudTargetingMode.Move or HudTargetingMode.Reverse)
            {
                _previewController?.UpdateHover(_camera, mousePos, viewportSize, isOccluded, false, _unitViews, _session.Replica,
                    _hudTargetingMode == HudTargetingMode.Reverse ? MoveMode.Reverse : MoveMode.Forward);
            }
            else if (_hudTargetingMode == HudTargetingMode.Attack)
            {
                _destination.HideGhost();
                _destination.ClearGroupPreviewSlots();
                if (isOccluded) _orderCursorOverlay?.HideTooltip();
                else
                {
                    UpdateAttackHoverTooltip(mousePos, viewportSize);
                }
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.BoardTransport)
            {
                _destination.HideZoneCircle();
                if (TryGetOwnUnitUnderMouse(mousePos, out EntityId hoverTransportId) &&
                    _session.Replica.TryGetOwnUnit(hoverTransportId, out OwnUnitObservation hoverTrans))
                {
                    if (hoverTrans.PassengerCapacity > 0)
                    {
                        string transName = GetUnitDisplayName(hoverTrans.Definition);
                        int seats = hoverTrans.PassengerCapacity - hoverTrans.PassengerCount;
                        if (seats > 0)
                        {
                            _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Valid, $"Board {transName} ({seats} seats open) [LMB]");
                        }
                        else
                        {
                            _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, $"{transName} is full [LMB blocked]");
                        }
                    }
                    else
                    {
                        _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Target is not a transport vehicle");
                    }
                }
                else
                {
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Adjusted, "Click friendly transport to board [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.Disembark)
            {
                _destination.HideZoneCircle();
                if (TryGetGroundPositionUnderMouse(mousePos, out NumericsVector2 groundPos))
                {
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Valid, $"Unload at ({groundPos.X:0.0}, {groundPos.Y:0.0}) [LMB] [Esc to cancel]");
                }
                else
                {
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Invalid ground location [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.LandAt)
            {
                if (TryGetGroundPositionUnderMouse(mousePos, out NumericsVector2 groundPos) && _scenarioGeometry is not null)
                {
                    float lzRadius = GetSelectedHelicopterLandingRadius();
                    float maxSlope = GetSelectedHelicopterMaxSlope();
                    LandingZoneStatus lzStatus = FlightSystem.EvaluateLandingZone(_scenarioGeometry, null, groundPos, lzRadius, maxSlope);
                    bool lzValid = lzStatus == LandingZoneStatus.Valid;
                    _destination.ShowZoneCircle(_map, groundPos.X, groundPos.Y, lzRadius, lzValid);
                    string lzTip = lzValid
                        ? $"Clear LZ ({groundPos.X:0.0}, {groundPos.Y:0.0}) [LMB]"
                        : $"LZ Blocked: {FormatLzBlockReason(lzStatus)} [Esc to cancel]";
                    _orderCursorOverlay?.SetCustomTooltip(true, lzValid ? DestinationValidity.Valid : DestinationValidity.Blocked, lzTip);
                }
                else
                {
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Invalid landing location [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.FastRope)
            {
                if (TryGetGroundPositionUnderMouse(mousePos, out NumericsVector2 groundPos) && _scenarioGeometry is not null)
                {
                    FastRopeZoneStatus frStatus = FlightSystem.EvaluateFastRopeZone(_scenarioGeometry, groundPos);
                    bool frValid = frStatus == FastRopeZoneStatus.Valid;
                    _destination.ShowZoneCircle(_map, groundPos.X, groundPos.Y, 3.5f, frValid);
                    string frTip = frValid
                        ? $"Fast-Rope Insertion ({groundPos.X:0.0}, {groundPos.Y:0.0}) [LMB]"
                        : $"Fast-Rope Blocked: {FormatFastRopeBlockReason(frStatus)} [Esc to cancel]";
                    _orderCursorOverlay?.SetCustomTooltip(true, frValid ? DestinationValidity.Valid : DestinationValidity.Blocked, frTip);
                }
                else
                {
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Invalid fast-rope location [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.SlingPickup)
            {
                _destination.HideZoneCircle();
                float maxSlingKg = GetSelectedHelicopterMaxSlingKg();
                if (TryGetOwnUnitUnderMouse(mousePos, out EntityId hoverCargoId) &&
                    _session.Replica.TryGetOwnUnit(hoverCargoId, out OwnUnitObservation hoverCargo))
                {
                    UnitDefinition? cDef = hoverCargo.Definition < _catalog.Units.Length ? _catalog.Units[hoverCargo.Definition] : null;
                    string cargoName = GetUnitDisplayName(hoverCargo.Definition);
                    if (cDef is not null && cDef.CanSlingLoad && cDef.CargoWeightKg > 0f && cDef.CargoWeightKg <= maxSlingKg && hoverCargo.SlungById == default)
                    {
                        _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Valid,
                            $"Sling Pickup: {cargoName} ({cDef.CargoWeightKg / 1000f:0.1}t / {maxSlingKg / 1000f:0.1}t) [LMB]");
                    }
                    else if (cDef is not null && cDef.CargoWeightKg > maxSlingKg)
                    {
                        _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked,
                            $"Too heavy for sling: {cargoName} ({cDef.CargoWeightKg / 1000f:0.1}t > {maxSlingKg / 1000f:0.1}t)");
                    }
                    else
                    {
                        _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked,
                            $"{cargoName} cannot be sling-loaded");
                    }
                }
                else
                {
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Adjusted, "Click friendly light/APC vehicle to sling-load [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.SlingDrop)
            {
                if (TryGetGroundPositionUnderMouse(mousePos, out NumericsVector2 groundPos) && _scenarioGeometry is not null)
                {
                    SlingDropZoneStatus sdStatus = FlightSystem.EvaluateSlingDropZone(_scenarioGeometry, null, groundPos, 3.0f);
                    bool sdValid = sdStatus == SlingDropZoneStatus.Valid;
                    _destination.ShowZoneCircle(_map, groundPos.X, groundPos.Y, 3.5f, sdValid);
                    string sdTip = sdValid
                        ? $"Release Sling Cargo ({groundPos.X:0.0}, {groundPos.Y:0.0}) [LMB]"
                        : $"Sling Drop Blocked: {FormatSlingDropBlockReason(sdStatus)} [Esc to cancel]";
                    _orderCursorOverlay?.SetCustomTooltip(true, sdValid ? DestinationValidity.Valid : DestinationValidity.Blocked, sdTip);
                }
                else
                {
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Invalid sling drop location [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.FireMission)
            {
                if (TryGetGroundPositionUnderMouse(mousePos, out NumericsVector2 groundPos) &&
                    TryGetSelectedArtilleryWeapon(out WeaponData? artyWeapon, out NumericsVector2 refLauncherPos, out int readyLaunchers))
                {
                    float dist = NumericsVector2.Distance(refLauncherPos, groundPos);
                    bool inRange = dist >= artyWeapon!.MinRange && dist <= artyWeapon.Range;
                    float radius = artyWeapon.DispersionRadius > 0f ? artyWeapon.DispersionRadius : 45f;
                    float outerBlastRadius = artyWeapon.SplashRadius > 0f ? radius + artyWeapon.SplashRadius : 0f;
                    _destination.ShowZoneCircle(_map, groundPos.X, groundPos.Y, radius, inRange, outerBlastRadius);

                    string tip;
                    if (dist < artyWeapon.MinRange)
                    {
                        tip = $"Too close: {dist:0}m (min {artyWeapon.MinRange:0}m) [Esc to cancel]";
                    }
                    else if (dist > artyWeapon.Range)
                    {
                        tip = $"Out of range: {dist:0}m (max {artyWeapon.Range:0}m) [Esc to cancel]";
                    }
                    else
                    {
                        float originY = _map.GetHeight(refLauncherPos.X, refLauncherPos.Y) + artyWeapon.MuzzleHeight;
                        float targetY = _map.GetHeight(groundPos.X, groundPos.Y);
                        ArtilleryBallistics.ComputeTrajectory(
                            new System.Numerics.Vector3(refLauncherPos.X, originY, refLauncherPos.Y),
                            new System.Numerics.Vector3(groundPos.X, targetY, groundPos.Y),
                            artyWeapon.ProjectileSpeed,
                            out int flightTicks,
                            out _);
                        float flightSec = flightTicks * Simulation.TickSeconds;
                        int salvoTotal = readyLaunchers * Math.Max(1, artyWeapon.SalvoCount);
                        tip = outerBlastRadius > radius
                            ? $"Barrage: {dist:0}m | {radius:0}m impact / {outerBlastRadius:0}m blast | ~{flightSec:0.0}s | {salvoTotal} rockets [LMB]"
                            : $"Barrage: {dist:0}m | Flight ~{flightSec:0.0}s | {salvoTotal} rockets [LMB]";
                    }

                    _orderCursorOverlay?.SetCustomTooltip(true, inRange ? DestinationValidity.Valid : DestinationValidity.Blocked, tip);
                }
                else
                {
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Invalid barrage target [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.AttackGround)
            {
                if (_previewController is not null &&
                    _previewController.TryRaycast(_camera, mousePos, out Vector3 hitPoint3D, out _, out _, out StructureId hitStructureId))
                {
                    _destination.ShowZoneCircle(_map, hitPoint3D.X, hitPoint3D.Z, 3.5f, true);
                    string tip;
                    if (hitStructureId != default &&
                        _buildingDestructionView is not null &&
                        _buildingDestructionView.TryGetSectionInfo(hitStructureId, out OpaqueStructure? stBounds, out StructureDestructionProfile stProfile, out byte standingFloors) &&
                        stBounds is not null &&
                        standingFloors > 0)
                    {
                        byte floorIdx = StructureDestructionMath.ResolveFloorIndex(hitPoint3D.Y, stBounds.MinY, in stProfile, standingFloors);
                        tip = $"Attack Building Floor {floorIdx + 1}/{standingFloors} [LMB | Esc to cancel]";
                    }
                    else
                    {
                        tip = "Attack Ground Point [LMB | Esc to cancel]";
                    }
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Valid, tip);
                }
                else
                {
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "No target under cursor [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else if (_contextActionPanel?.TargetingMode == ContextTargetingMode.EnterBuilding)
            {
                if (_buildingGarrisonView is not null &&
                    _hoveredGarrisonStructure != default &&
                    _buildingGarrisonView.TryGetBuildingInfo(_hoveredGarrisonStructure, out System.Numerics.Vector2 entPos, out int cap, out _))
                {
                    _destination.ShowZoneCircle(_map, entPos.X, entPos.Y, 2.5f, true);
                    int ownInside = CountOwnUnitsInBuilding(_hoveredGarrisonStructure);
                    bool full = ownInside >= cap;
                    string tip = full
                        ? $"Building full ({ownInside}/{cap}) [Esc to cancel]"
                        : $"Garrison building ({ownInside}/{cap}) [LMB | Esc to cancel]";
                    _orderCursorOverlay?.SetCustomTooltip(true, full ? DestinationValidity.Blocked : DestinationValidity.Valid, tip);
                }
                else
                {
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Click a building section to garrison [Esc to cancel]");
                }
                _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
            }
            else
            {
                _destination.HideZoneCircle();
                bool hasEnemyContact = TryGetTargetContactUnderMouse(mousePos, out _);
                if (!hasEnemyContact &&
                    HasSelectedBoardableUnits() &&
                    TryGetOwnUnitUnderMouse(mousePos, out EntityId hoverTransportId) &&
                    _session.Replica.TryGetOwnUnit(hoverTransportId, out OwnUnitObservation hoverTrans) &&
                    hoverTrans.PassengerCapacity > 0 &&
                    CanSelectedUnitsBoardTransport(hoverTrans))
                {
                    string transName = GetUnitDisplayName(hoverTrans.Definition);
                    int seats = hoverTrans.PassengerCapacity - hoverTrans.PassengerCount;
                    if (seats > 0)
                    {
                        _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Valid, $"Load into {transName} ({seats} seats open) [RMB]");
                    }
                    else
                    {
                        _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, $"{transName} is full [0 seats open]");
                    }
                    _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
                }
                else if (!hasEnemyContact &&
                         HasSelectedGarrisonableInfantry() &&
                         _buildingGarrisonView is not null &&
                         _hoveredGarrisonStructure != default &&
                         _buildingGarrisonView.TryGetBuildingInfo(_hoveredGarrisonStructure, out _, out int garrCap, out _))
                {
                    int ownInside = CountOwnUnitsInBuilding(_hoveredGarrisonStructure);
                    bool full = ownInside >= garrCap;
                    string tip = full
                        ? $"Building full ({ownInside}/{garrCap})"
                        : $"Garrison building ({ownInside}/{garrCap}) [RMB]";
                    _orderCursorOverlay?.SetCustomTooltip(true, full ? DestinationValidity.Blocked : DestinationValidity.Valid, tip);
                    _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
                }
                else
                {
                    _previewController?.UpdateHover(_camera, mousePos, viewportSize, isOccluded, hasEnemyContact, _unitViews, _session.Replica);
                }
            }
        }
        else
        {
            _destination.HideZoneCircle();
            _orderCursorOverlay?.HideTooltip();
        }

        long previewEnd = collectBench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (_benchmarkActive && _benchmarkOverrides.BenchmarkPan)
        {
            _panAnchor ??= _cameraRig.Focus;
            _panElapsed += delta;
            float offsetX = (float)Math.Sin(_panElapsed * 0.7) * 35f;
            float offsetZ = (float)Math.Cos(_panElapsed * 0.5) * 18f;
            _cameraRig.CenterOn(_panAnchor.Value + new Vector3(offsetX, 0f, offsetZ));
        }
        _cameraRig.Update(delta, allowInput: !_benchmarkActive && !_settingsPanel.IsOpen && !_pauseMenu.IsOpen && !_helpPanel.IsOpen);
        _environmentView?.UpdateHazeForCamera(_cameraRig.Size, _cameraRig.Focus, _cameraRig.ViewPitch);
        _environmentView?.UpdateShadowTierForCamera(_cameraRig.Size, _activeShadowMapLayers);
        _environmentView?.UpdateShadowFade(delta);
        UpdateTerrainLodStep(_terrainView, _camera, _cachedTerrainViewportHeightPx);

        if (_roleBadgeOverlay is not null && _topHud is not null && _utilityHud is not null && _selectionCard is not null)
        {
            RefreshOccludingPanels();
            _roleBadgeOverlay.UpdateBadges(_camera, _camera.Size, _unitViews, _contactViews, _session.Replica, _session.Tick, _badgeOccludingPanels);
        }

        long minimapStart = collectBench ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _minimap?.UpdateMinimap(delta);
        long frameEndTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        float cpuWorkMs = (float)((frameEndTimestamp - frameStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        if (_benchmarkActive && _benchmarkMetrics is not null)
        {
            if (!_benchmarkWarmupCompleted)
            {
                if (AdvanceBenchmarkWarmup((float)delta))
                {
                    _panElapsed = 0.0;
                    if (_panAnchor.HasValue)
                    {
                        _cameraRig.CenterOn(_panAnchor.Value);
                    }
                }
            }
            else if (wallClockIntervalMs > 0f && !IsBenchmarkSampleLimitReached())
            {
                _benchmarkElapsedSec += wallClockIntervalMs * 0.001;
                double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Rid vpRid = GetViewport().GetViewportRid();
                double measuredGpu = RenderingServer.ViewportGetMeasuredRenderTimeGpu(vpRid);
                float gpuMs = measuredGpu > 0.0 && double.IsFinite(measuredGpu) ? (float)measuredGpu : -1f;
                FramePacingMetrics.FramePhaseSample phaseSample = new(
                    SessionMs: (float)((fogStart - sessionStart) * toMs),
                    FogOfWarMs: (float)((viewsStart - fogStart) * toMs),
                    ViewsMs: (float)((effectsStart - viewsStart) * toMs),
                    OrderPreviewMs: (float)((previewEnd - previewStart) * toMs),
                    MinimapMs: (float)((frameEndTimestamp - minimapStart) * toMs),
                    EffectsAndHudMs: (float)(((previewStart - effectsStart) + (minimapStart - previewEnd)) * toMs),
                    RenderDrawCalls: (int)Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
                    RenderPrimitives: (int)Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
                    ActiveSceneNodes: (int)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
                    LodSwitches: lodSwitches,
                    VisibleDrawCalls: RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),
                    VisiblePrimitives: RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame),
                    ShadowDrawCalls: RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),
                    ShadowPrimitives: RenderingServer.ViewportGetRenderInfo(vpRid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.PrimitivesInFrame));
                _benchmarkMetrics.Record(wallClockIntervalMs, cpuWorkMs, gpuMs, in phaseSample);
                if (_minuteWindowMetrics is not null)
                {
                    _minuteWindowMetrics.Record(wallClockIntervalMs, cpuWorkMs, gpuMs);
                    _minuteElapsedSec += wallClockIntervalMs * 0.001;
                    if (_minuteElapsedSec >= 60.0)
                    {
                        Vector2I mWin = !SettingsPanel.IsHeadlessDisplay
                            ? DisplayServer.WindowGetSize()
                            : (GetWindow() is Window mw ? mw.Size : default);
                        FramePacingMetrics.PercentileResults mr = _minuteWindowMetrics.CalculateResults();
                        GD.Print(string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"[BENCH_MINUTE {_minuteIndex}] frames={mr.SampleCount} win={mWin.X}x{mWin.Y} P50={mr.P50Ms:F2} ms ({1000f / MathF.Max(0.01f, mr.P50Ms):F1} FPS) P95={mr.P95Ms:F2} ms ({1000f / MathF.Max(0.01f, mr.P95Ms):F1} FPS) MeanCPU={mr.MeanCpuMs:F2} ms"));
                        _minuteWindowMetrics.Reset();
                        _minuteElapsedSec = 0.0;
                        _minuteIndex++;
                    }
                }

                if (IsBenchmarkFinished())
                {
                    if (_minuteWindowMetrics is not null && _minuteWindowMetrics.Count > 0)
                    {
                        Vector2I mWin = !SettingsPanel.IsHeadlessDisplay
                            ? DisplayServer.WindowGetSize()
                            : (GetWindow() is Window mw ? mw.Size : default);
                        FramePacingMetrics.PercentileResults mr = _minuteWindowMetrics.CalculateResults();
                        GD.Print(string.Create(
                            System.Globalization.CultureInfo.InvariantCulture,
                            $"[BENCH_MINUTE {_minuteIndex}] frames={mr.SampleCount} win={mWin.X}x{mWin.Y} P50={mr.P50Ms:F2} ms ({1000f / MathF.Max(0.01f, mr.P50Ms):F1} FPS) P95={mr.P95Ms:F2} ms ({1000f / MathF.Max(0.01f, mr.P95Ms):F1} FPS) MeanCPU={mr.MeanCpuMs:F2} ms"));
                    }
                    string hardware = $"{OS.GetName()} / {OS.GetProcessorName()} / Cores: {OS.GetProcessorCount()}";
                    Vector2 viewSize = GetViewport().GetVisibleRect().Size;
                    Vector2I winSize = !SettingsPanel.IsHeadlessDisplay
                        ? DisplayServer.WindowGetSize()
                        : GetWindow() is Window hw && hw.Size.X > 0
                            ? hw.Size
                            : new Vector2I((int)viewSize.X, (int)viewSize.Y);
                    int dpi = !SettingsPanel.IsHeadlessDisplay ? DisplayServer.ScreenGetDpi() : 96;
                    float screenScale = !SettingsPanel.IsHeadlessDisplay ? DisplayServer.ScreenGetScale() : 1.0f;
                    int curScreen = !SettingsPanel.IsHeadlessDisplay ? DisplayServer.WindowGetCurrentScreen() : 0;
                    Vector2I screenRes = !SettingsPanel.IsHeadlessDisplay ? DisplayServer.ScreenGetSize(curScreen) : winSize;
                    float scale3D = GetViewport().Scaling3DScale;
                    Vector2I activeWinPx = SettingsPanel.ResolveWindowPixelSize(GetViewport());
                    int internalW = (int)MathF.Round(activeWinPx.X * scale3D);
                    int internalH = (int)MathF.Round(activeWinPx.Y * scale3D);
                    string msaaLabel = GetViewport().Msaa3D switch
                    {
                        Viewport.Msaa.Msaa4X => "4x",
                        Viewport.Msaa.Msaa2X => "2x",
                        _ => "off"
                    };
                    string aaSummary = GetViewport().ScreenSpaceAA == Viewport.ScreenSpaceAAEnum.Fxaa
                        ? "fxaa"
                        : msaaLabel;
                    bool ssaoOn = _environmentView?.GodotEnvironment?.SsaoEnabled == true;
                    string ssaoSummary = ssaoOn
                        ? (_benchmarkOverrides.SsaoMode ?? "half")
                        : "off";
                    bool glowOn = _environmentView?.GodotEnvironment?.GlowEnabled == true;
                    string shadowSplits = _environmentView?.Sun.DirectionalShadowMode.ToString() ?? "Unknown";
                    string shadowSize = _benchmarkOverrides.ShadowSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "4096";
                    string shadowFilter = _benchmarkOverrides.ShadowFilter ?? "default";
                    string terrainDiffuse = _benchmarkOverrides.TerrainShader ?? "lambert";
                    string resolution = string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"{viewSize.X:F0}x{viewSize.Y:F0} (screen[{curScreen}]={screenRes.X}x{screenRes.Y}, window={winSize.X}x{winSize.Y}, internal={internalW}x{internalH}, dpi={dpi}, screenScale={screenScale:F2}, renderScale={scale3D:F2}, scaling={GetViewport().Scaling3DMode}, aa={aaSummary}, ssao={ssaoSummary}, shadow={shadowSplits}@{shadowSize}/{shadowFilter}, glow={glowOn}, diffuse={terrainDiffuse}, pan={_benchmarkOverrides.BenchmarkPan})");
                    string report = _benchmarkMetrics.FormatReport(
                        _scenario, hardware, resolution, _settingsPanel.CurrentSettings.VSyncEnabled, _settingsPanel.CurrentSettings.MaxFps, warmupFrames: 5);
                    GD.Print(report);
                    _benchmarkActive = false;
                    if (_benchmarkQuit)
                    {
                        GetTree().Quit(0);
                    }
                }
            }
        }

        _hudTimer += delta;
        if (_hudTimer >= 0.5)
        {
            double rate = (_session.BytesReceived - _lastBytes) / _hudTimer / 1024;
            _lastBytes = _session.BytesReceived;
            if (_debugHudVisible)
            {
                string simInfo = _session is LocalSession local
                    ? $"{local.LastTickMilliseconds:F3} ms per tick / {local.LastTickAllocatedBytes} B tick allocations"
                    : _session.StatusDescription;
                _stats.Text = $"20 Hz SIM / 10 Hz STATE / TICK {_session.Tick:00000} / OWN: {_session.Replica.OwnUnits.Length} / CONTACTS: {_session.Replica.ContactCount}\n{simInfo} / {rate:F1} KiB/s";
            }
            _hudTimer = 0;
        }

        ProcessScreenshot(delta);
        ProcessForestDemoCapture(delta);
        ProcessTransportDemoCapture(delta);
        _viewCapture?.Process(delta);

        if (_screenshotOnFinishPath is not null && _missionPanel?.IsMissionFinished == true)
        {
            _screenshotOnFinishTimer -= delta;
            if (_screenshotOnFinishTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    img.SavePng(_screenshotOnFinishPath);
                    GD.Print($"SCREENSHOT_SAVED {_screenshotOnFinishPath}");
                }
                _screenshotOnFinishPath = null;
                if (_screenshotQuit)
                {
                    GetTree().Quit(0);
                }
            }
        }

        if (_smokeTest)
        {
            // In headless smoke tests with the Dummy audio driver, active playbacks stopped during Clear()
            // require a few frames of main loop processing to retire from AudioServer before quitting.
            // Quitting immediately leaves 4 ObjectDB instances (AudioStreamWAV and AudioStreamPlaybackWAV)
            // leaked at exit.
            if (_smokeQuitFrames >= 0)
            {
                _smokeQuitFrames++;
                if (_smokeQuitFrames >= 12)
                {
                    GetTree().Quit(_smokeExitCode);
                }
                return;
            }

            _smokeTimer += delta;

            if (_scenario == "vehicle-traffic")
            {
                var ownUnits = _session.Replica.OwnUnits;
                for (int u = 0; u < ownUnits.Length; u++)
                {
                    if (ownUnits[u].NavStatus == PathRequestStatus.WaitingForClearance)
                    {
                        _trafficObservedWaiting = true;
                    }
                }

                if (_trafficObservedWaiting && !_trafficLeaderCleared && ownUnits.Length > 0)
                {
                    // Move the parked leader (unit 0 at 60,60) North to (60, 100) to free the path for the follower immediately
                    _session.Move(ownUnits[0].Id, new NumericsVector2(60f, 100f));
                    _trafficLeaderCleared = true;
                }

                if (_trafficLeaderCleared && ownUnits.Length > 1)
                {
                    if (ownUnits[1].NavStatus == PathRequestStatus.PathFound)
                    {
                        _trafficObservedResumed = true;
                    }
                }
            }

            float smokeTimeout = _scenario == "vehicle-traffic" ? 5.0f : 1.0f;
            if (_smokeTimer >= smokeTimeout)
            {
                bool ok = _scenario switch
                {
                    "visibility" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 2,
                    "navigation" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "navigation-large" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "combat" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 4,
                    "recon-raid" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "battle" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == BattleScenario.UnitsPerSide,
                    "bridge-crossing" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "vehicle-traffic" => _terrainWindingValid && _session.Tick >= 60 && _trafficObservedWaiting && _trafficObservedResumed && _session.Replica.OwnUnits.Length >= 6,
                    "group-move" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length >= 10,
                    "ustye-ground" => (_terrainWindingValid || (_ustyeMapView?.HasUpwardFrontFaces() ?? false)) && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 6,
                    "ustye-raid" => (_terrainWindingValid || (_ustyeMapView?.HasUpwardFrontFaces() ?? false)) && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
                    "forest-visibility" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 5 && CheckForestVisibilityCleanPositions(_session.Replica.OwnUnits),
                    "infantry-playground" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 4 && CheckInfantryPlaygroundCleanPositions(_session.Replica.OwnUnits),
                    "infantry-transport" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 5,
                    "helicopter-playground" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 6,
                    "artillery-playground" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 4,
                    "air-defense-playground" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length >= 3,
                    "urban-destruction-playground" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 4,
                    _ => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 12 &&
                        _session.Replica.OwnUnits[0].Position.Y > 24
                };
                string prefix = _scenario switch
                {
                    "combat" => "M1_CLIENT_COMBAT_SMOKE",
                    "navigation-large" => "M1_CLIENT_NAV_LARGE_SMOKE",
                    "recon-raid" => "M1_CLIENT_RECON_RAID_SMOKE",
                    "battle" => "M1_CLIENT_BATTLE_SMOKE",
                    "bridge-crossing" => "M1_CLIENT_BRIDGE_CROSSING_SMOKE",
                    "vehicle-traffic" => "M1_CLIENT_VEHICLE_TRAFFIC_SMOKE",
                    "group-move" => "M1_CLIENT_GROUP_MOVE_SMOKE",
                    "ustye-ground" => "USTYE_GROUND_SMOKE",
                    "ustye-raid" => "USTYE_RAID_SMOKE",
                    "forest-visibility" => "FOREST_VISIBILITY_SMOKE",
                    "infantry-playground" => "INFANTRY_PLAYGROUND_SMOKE",
                    "infantry-transport" => "INFANTRY_TRANSPORT_SMOKE",
                    "helicopter-playground" => "HELICOPTER_PLAYGROUND_SMOKE",
                    "artillery-playground" => "ARTILLERY_PLAYGROUND_SMOKE",
                    "air-defense-playground" => "AIR_DEFENSE_PLAYGROUND_SMOKE",
                    "urban-destruction-playground" => "URBAN_DESTRUCTION_PLAYGROUND_SMOKE",
                    _ => "M0_CLIENT_SMOKE"
                };
                string token = prefix + (ok ? "_OK" : "_FAILED");
                GD.Print(token);
                _combatEffects?.Clear();
                _smokeView?.Clear();
                _smokeQuitFrames = 0;
                _smokeExitCode = ok ? 0 : 1;
            }
        }
    }

    private static bool CheckForestVisibilityCleanPositions(ReadOnlySpan<OwnUnitObservation> ownUnits)
    {
        if (ownUnits.Length != 5) return false;
        NumericsVector2[] expectedPositions =
        [
            ForestVisibilityScenario.ObserverGroundStart,
            ForestVisibilityScenario.ObserverRidgeStart,
            ForestVisibilityScenario.ObserverBeltsStart,
            ForestVisibilityScenario.ObserverSparseStart,
            ForestVisibilityScenario.ObserverOpenStart,
        ];
        for (int i = 0; i < ownUnits.Length; i++)
        {
            if (ownUnits[i].NavStatus != PathRequestStatus.None) return false;
            if ((ownUnits[i].Position - expectedPositions[i]).LengthSquared() > 0.01f) return false;
        }
        return true;
    }

    private static bool CheckInfantryPlaygroundCleanPositions(ReadOnlySpan<OwnUnitObservation> ownUnits)
    {
        if (ownUnits.Length != 4) return false;
        NumericsVector2[] expectedPositions =
        [
            InfantryPlaygroundScenario.UsRiflemanOpen,
            InfantryPlaygroundScenario.UsRiflemanForest,
            InfantryPlaygroundScenario.UsRiflemanBridge,
            InfantryPlaygroundScenario.UsRiflemanBuilding,
        ];
        for (int i = 0; i < ownUnits.Length; i++)
        {
            if (ownUnits[i].NavStatus != PathRequestStatus.None) return false;
            if ((ownUnits[i].Position - expectedPositions[i]).LengthSquared() > 0.01f) return false;
        }
        return true;
    }

    private void ProcessScreenshot(double delta)
    {
        if (_screenshotPath is not null)
        {
            _screenshotTimer -= delta;
            if (_screenshotTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    img.SavePng(_screenshotPath);
                    GD.Print($"SCREENSHOT_SAVED {_screenshotPath}");
                }
                _screenshotPath = null;
                if (_screenshotQuit)
                {
                    GetTree().Quit(0);
                }
            }
        }
    }

    private void ProcessForestDemoCapture(double delta)
    {
        if (_forestDemoCaptureDir is null || _forestDemoStage < 0)
            return;

        _forestDemoTimer -= delta;

        if (_forestDemoStage == 0)
        {
            if (_forestDemoTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    string path1 = System.IO.Path.Combine(_forestDemoCaptureDir, "forest_visibility_1_before_detection.png");
                    img.SavePng(path1);
                    GD.Print($"FOREST_DEMO_SCREENSHOT 1: {path1}");
                }

                if (_session is LocalSession local)
                {
                    local.Move(new EntityId(1), new NumericsVector2(115f, 110f));
                }
                _forestDemoStage = 1;
                _forestDemoTimer = 0.0;
            }
        }
        else if (_forestDemoStage == 1)
        {
            UpdateVegetationDebugOverlay();
            bool spotted = false;
            foreach (ContactObservation c in _session.Replica.Contacts)
            {
                if (c.State == ContactState.Current && NumericsVector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 5.0f)
                {
                    spotted = true;
                    break;
                }
            }

            if (spotted)
            {
                if (!_forestDemoTargetSpotted)
                {
                    _forestDemoTargetSpotted = true;
                    _forestDemoTimer = 0.5;
                }
                else if (_forestDemoTimer <= 0)
                {
                    Image? img = GetViewport().GetTexture().GetImage();
                    if (img is not null)
                    {
                        string path2 = System.IO.Path.Combine(_forestDemoCaptureDir, "forest_visibility_2_after_detection.png");
                        img.SavePng(path2);
                        GD.Print($"FOREST_DEMO_SCREENSHOT 2: {path2}");
                    }

                    if (_session is LocalSession local)
                    {
                        local.Move(new EntityId(1), new NumericsVector2(60f, 110f));
                    }
                    _forestDemoStage = 2;
                    _forestDemoTimer = 0.0;
                }
            }
        }
        else if (_forestDemoStage == 2)
        {
            UpdateVegetationDebugOverlay();
            bool transitioned = false;
            foreach (ContactObservation c in _session.Replica.Contacts)
            {
                if (c.State == ContactState.LastKnown && NumericsVector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 5.0f)
                {
                    transitioned = true;
                    break;
                }
            }

            if (transitioned)
            {
                if (!_forestDemoContactLost)
                {
                    _forestDemoContactLost = true;
                    _forestDemoTimer = 0.6;
                }
                else if (_forestDemoTimer <= 0)
                {
                    Image? img = GetViewport().GetTexture().GetImage();
                    if (img is not null)
                    {
                        string path3 = System.IO.Path.Combine(_forestDemoCaptureDir, "forest_visibility_3_after_contact_loss.png");
                        img.SavePng(path3);
                        GD.Print($"FOREST_DEMO_SCREENSHOT 3: {path3}");
                    }
                    _forestDemoStage = -1;
                    GetTree().Quit(0);
                }
            }
        }
    }

    private void ProcessTransportDemoCapture(double delta)
    {
        if (_transportDemoCaptureDir is null || _transportDemoStage < 0)
            return;

        _transportDemoTimer -= delta;

        if (_transportDemoStage == 0)
        {
            _unitViews.Selected.Clear();
            _unitViews.Selected.Add(new EntityId(2));
            _unitViews.Selected.Add(new EntityId(3));
            _unitViews.Selected.Add(new EntityId(4));
            _unitViews.Selected.Add(new EntityId(5));
            UpdateSelectionPresentation();

            if (_transportDemoTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    string p1 = System.IO.Path.Combine(_transportDemoCaptureDir, "transport_cycle_1_staging.png");
                    img.SavePng(p1);
                    GD.Print($"TRANSPORT_DEMO_SCREENSHOT 1: {p1}");
                }

                if (_session is LocalSession local)
                {
                    local.BoardTransport(new EntityId(2), new EntityId(1));
                    local.BoardTransport(new EntityId(3), new EntityId(1));
                    local.BoardTransport(new EntityId(4), new EntityId(1));
                    local.BoardTransport(new EntityId(5), new EntityId(1));
                }
                _transportDemoStage = 1;
                _transportDemoTimer = 2.0;
            }
        }
        else if (_transportDemoStage == 1)
        {
            bool allBoarded = true;
            for (uint u = 2; u <= 5; u++)
            {
                if (_session.Replica.TryGetOwnUnit(new EntityId(u), out OwnUnitObservation obs))
                {
                    if (obs.ContainerId.Value != 1)
                    {
                        allBoarded = false;
                        break;
                    }
                }
            }

            if (allBoarded)
            {
                _unitViews.Selected.Clear();
                _unitViews.Selected.Add(new EntityId(1));
                UpdateSelectionPresentation();

                if (_transportDemoTimer <= 0)
                {
                    Image? img = GetViewport().GetTexture().GetImage();
                    if (img is not null)
                    {
                        string p2 = System.IO.Path.Combine(_transportDemoCaptureDir, "transport_cycle_2_boarded.png");
                        img.SavePng(p2);
                        GD.Print($"TRANSPORT_DEMO_SCREENSHOT 2: {p2}");
                    }

                    if (_session is LocalSession local)
                    {
                        local.Move(new EntityId(1), new NumericsVector2(128f, 165f));
                    }
                    _transportDemoStage = 2;
                    _transportDemoStage2Arrived = false;
                }
            }
        }
        else if (_transportDemoStage == 2)
        {
            if (_session.Replica.TryGetOwnUnit(new EntityId(1), out OwnUnitObservation transObs))
            {
                float dist = NumericsVector2.Distance(transObs.Position, new NumericsVector2(128f, 165f));
                _cameraRig.CenterOn(new Vector3(transObs.Position.X, 0f, transObs.Position.Y));

                if (!_transportDemoStage2Arrived)
                {
                    if (dist < 2.0f)
                    {
                        _transportDemoStage2Arrived = true;
                        _transportDemoTimer = 0.5;
                    }
                }
                else if (_transportDemoTimer <= 0)
                {
                    Image? img = GetViewport().GetTexture().GetImage();
                    if (img is not null)
                    {
                        string p3 = System.IO.Path.Combine(_transportDemoCaptureDir, "transport_cycle_3_conveyed.png");
                        img.SavePng(p3);
                        GD.Print($"TRANSPORT_DEMO_SCREENSHOT 3: {p3}");
                    }

                    if (_session is LocalSession local)
                    {
                        local.Disembark(new EntityId(1), new NumericsVector2(128f, 165f));
                    }
                    _transportDemoStage = 3;
                    _transportDemoTimer = 0.8;
                }
            }
        }
        else if (_transportDemoStage == 3)
        {
            bool allDisembarked = true;
            for (uint u = 2; u <= 5; u++)
            {
                if (_session.Replica.TryGetOwnUnit(new EntityId(u), out OwnUnitObservation obs))
                {
                    if (obs.ContainerId.Value != 0)
                    {
                        allDisembarked = false;
                        break;
                    }
                }
            }

            if (allDisembarked)
            {
                _unitViews.Selected.Clear();
                _unitViews.Selected.Add(new EntityId(2));
                _unitViews.Selected.Add(new EntityId(3));
                _unitViews.Selected.Add(new EntityId(4));
                _unitViews.Selected.Add(new EntityId(5));
                UpdateSelectionPresentation();

                if (_transportDemoTimer <= 0)
                {
                    Image? img = GetViewport().GetTexture().GetImage();
                    if (img is not null)
                    {
                        string p4 = System.IO.Path.Combine(_transportDemoCaptureDir, "transport_cycle_4_disembarked.png");
                        img.SavePng(p4);
                        GD.Print($"TRANSPORT_DEMO_SCREENSHOT 4: {p4}");
                    }

                    if (_session is LocalSession local)
                    {
                        local.Move(new EntityId(2), new NumericsVector2(85f, 165f));
                        local.Move(new EntityId(3), new NumericsVector2(85f, 168f));
                        local.Move(new EntityId(4), new NumericsVector2(85f, 162f));
                        local.Move(new EntityId(5), new NumericsVector2(85f, 171f));
                    }
                    _transportDemoStage = 4;
                    _transportDemoTimer = 3.0;
                }
            }
        }
        else if (_transportDemoStage == 4)
        {
            _cameraRig.CenterOn(new Vector3(105f, 0f, 165f));
            if (_transportDemoTimer <= 0)
            {
                Image? img = GetViewport().GetTexture().GetImage();
                if (img is not null)
                {
                    string p5 = System.IO.Path.Combine(_transportDemoCaptureDir, "transport_cycle_5_forest_advance.png");
                    img.SavePng(p5);
                    GD.Print($"TRANSPORT_DEMO_SCREENSHOT 5: {p5}");
                }

                _transportDemoStage = -1;
                GetTree().Quit(0);
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (SettingsPanel.IsHeadlessDisplay && @event is InputEventMouseMotion headlessMotion)
        {
            _headlessPointerPosition = headlessMotion.Position;
        }

        if (_benchmarkActive)
        {
            return;
        }

        if (_settingsPanel?.IsOpen == true || _sessionMenu?.IsOpen == true || _pauseMenu?.IsOpen == true || _helpPanel?.IsOpen == true)
        {
            CancelActiveDrag();
            return;
        }

        if (_selectionController.IsPointerDown)
        {
            if (_settingsPanel?.IsOpen == true)
            {
                CancelActiveDrag();
                return;
            }

            if (@event is InputEventMouseMotion motion)
            {
                HandleLeftMouseMotion(motion.Position);
            }
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release)
            {
                HandleLeftMouseRelease(release.Position, release.ShiftPressed);
                GetViewport().SetInputAsHandled();
            }
        }
        else if (_previewController?.IsFacingGestureActive == true)
        {
            if (_settingsPanel?.IsOpen == true || _pauseMenu?.IsOpen == true || _helpPanel?.IsOpen == true)
            {
                CancelActiveDrag();
                return;
            }

            if (@event is InputEventMouseMotion motion)
            {
                _previewController.UpdateFacingGesture(_camera, motion.Position, _unitViews);
            }
            else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } release)
            {
                HandleRightMouseRelease(release.Position);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_benchmarkActive)
        {
            return;
        }

        if (_helpPanel?.IsOpen == true)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape or Key.F1 }) _helpPanel.Close();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_settingsPanel?.IsOpen == true)
        {
            CancelActiveDrag();
            if (@event is InputEventKey { Pressed: true, Echo: false } settingsKey)
            {
                if (settingsKey.Keycode == Key.Escape)
                {
                    _settingsPanel?.Close();
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (settingsKey.Keycode is Key.F10 or Key.O)
                {
                    _settingsPanel?.Toggle();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }

            // Centrally block all gameplay input while settings overlay is open
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_pauseMenu?.IsOpen == true)
        {
            CancelActiveDrag();
            if (@event is InputEventKey { Pressed: true, Echo: false } pauseKey)
            {
                if (pauseKey.Keycode == Key.Escape)
                {
                    _pauseMenu.Close();
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (pauseKey.Keycode is Key.F10 or Key.O)
                {
                    _settingsPanel?.Toggle();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (_sessionMenu?.IsOpen == true)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false } menuKey)
            {
                if (menuKey.Keycode is Key.F10 or Key.O)
                {
                    _settingsPanel?.Toggle();
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (menuKey.Keycode == Key.Escape)
                {
                    if (_sessionMenu.HandleEscape())
                    {
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }
            }
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } hudKey)
        {
            if (hudKey.Keycode == Key.F1)
            {
                OpenControlsHelp();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (hudKey.Keycode == Key.F9)
            {
                SetDebugHudVisible(!_debugHudVisible);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (hudKey.Keycode == Key.F7)
            {
                ToggleRangeLimits();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (_missionPanel?.IsMissionFinished == true)
        {
            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && _session is LocalSession)
            {
                OnRetryMission();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
            {
                _status.Text = _scenario == "battle" ? "Battle ended. Orders are disabled." : "Mission ended. Orders are disabled.";
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F })
            {
                _status.Text = _scenario == "battle" ? "Battle ended. Orders are disabled." : "Mission ended. Orders are disabled.";
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode == Key.F7 || ((key.Keycode is Key.F9 or Key.V) && key.ShiftPressed))
            {
                if (_fogOfWar is not null)
                {
                    _fogOfWar.IsCalculationSuspended = !_fogOfWar.IsCalculationSuspended;
                    _status.Text = _fogOfWar.IsCalculationSuspended
                        ? "Diagnostic: Fog of war CPU calculation SUSPENDED"
                        : "Diagnostic: Fog of war CPU calculation RESUMED";
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F9 or Key.V)
            {
                _fogOfWar?.Toggle();
                _status.Text = _fogOfWar?.IsEnabled == true ? "Fog of war enabled" : "Fog of war disabled";
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F10 or Key.O)
            {
                CancelActiveDrag();
                _settingsPanel?.Toggle();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F6)
            {
                _showVegetationDebug = !_showVegetationDebug;
                _status.Text = _showVegetationDebug ? "Vegetation overlay: ON" : "Vegetation overlay: OFF";
                UpdateVegetationDebugOverlay();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode is Key.F8)
            {
                _showVehicleCollisionBounds = !_showVehicleCollisionBounds;
                _status.Text = _showVehicleCollisionBounds ? "Collision bounds overlay: ON" : "Collision bounds overlay: OFF";
                UpdateCollisionDebugOverlay();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_scenario == "ustye-ground" && _session is LocalSession && key.Keycode is Key.F1 or Key.F2 or Key.F3 or Key.F4)
            {
                int newSetup = (int)(key.Keycode - Key.F1);
                SwitchUstyeSetup(newSetup);
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.F5 || (key.Keycode == Key.R && (key.CtrlPressed || key.MetaPressed))) && _session is LocalSession)
            {
                if (_scenario == "ustye-ground")
                {
                    SwitchUstyeSetup((_ustyeSetupIndex + 1) % 4);
                }
                else
                {
                    ResetLocalScenario();
                    _status.Text = "Scenario reset to initial state";
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode == Key.Escape)
            {
                if (_hudTargetingMode != HudTargetingMode.None)
                {
                    CancelHudTargeting();
                    GetViewport().SetInputAsHandled();
                    return;
                }
                if (_contextActionPanel?.TargetingMode != ContextTargetingMode.None)
                {
                    _contextActionPanel?.CancelTargeting();
                    _orderCursorOverlay?.HideTooltip();
                    _status.Text = "Targeting cancelled";
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (_previewController?.IsFacingGestureActive == true)
                {
                    _previewController.CancelFacingGesture();
                    _status.Text = "Move order cancelled";
                    GetViewport().SetInputAsHandled();
                    return;
                }

                if (_unitViews.Selected.Count > 0 || _contactViews.Selected.Count > 0)
                {
                    CancelActiveDrag();
                    _unitViews.Selected.Clear();
                    _contactViews.Selected.Clear();
                    _unitViews.UpdateSelectionVisuals();
                    _status.Text = "No units selected";
                    GetViewport().SetInputAsHandled();
                    return;
                }

                CancelActiveDrag();
                _pauseMenu?.Open();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.Keycode == Key.Space)
            {
                _cameraRig.ResetToDefault();
                return;
            }

            if (_scenario == "navigation-large" && _unitViews.Selected.Count > 0 &&
                TryGetNavigationLargeDebugTarget(key.Keycode, key.AltPressed, key.CtrlPressed || key.MetaPressed, key.ShiftPressed, out NumericsVector2 testTarget))
            {
                foreach (EntityId id in _unitViews.Selected)
                {
                    _session.Move(id, testTarget);
                }
                _status.Text = $"Route target set to {testTarget}";
                return;
            }

            GroupKeyAction groupAction = SelectionController.ClassifyGroupKey(
                key.Keycode, key.CtrlPressed, key.MetaPressed, key.ShiftPressed, key.AltPressed, out int groupNumber);
            if (groupAction != GroupKeyAction.None)
            {
                switch (groupAction)
                {
                    case GroupKeyAction.Store:
                        _selectionController.StoreControlGroup(_unitViews, groupNumber);
                        UpdateSelectionPresentation();
                        break;
                    case GroupKeyAction.Add:
                        _selectionController.AddToControlGroup(_unitViews, groupNumber);
                        UpdateSelectionPresentation();
                        _previewController?.OnUnitSelectionChanged();
                        break;
                    case GroupKeyAction.Recall:
                        double nowSeconds = Time.GetTicksUsec() / 1_000_000.0;
                        if (_selectionController.RecallControlGroup(_unitViews, _contactViews, _cameraRig, groupNumber, nowSeconds, out _))
                        {
                            _unitViews.UpdateSelectionVisuals();
                            UpdateSelectionPresentation();
                            _previewController?.OnUnitSelectionChanged();
                        }
                        break;
                }
                return;
            }

            if (key.Keycode == Key.F && _unitViews.Selected.Count > 0 && TryGetFirstSelectedContact(out ContactId target))
            {
                IssueAttackOrBarrageOnContact(target);
            }

            if (key.Keycode is Key.Z or Key.X or Key.C && _unitViews.Selected.Count > 0)
            {
                FireStance stance = key.Keycode switch
                {
                    Key.Z => FireStance.FireAtWill,
                    Key.X => FireStance.ReturnFire,
                    Key.C => FireStance.HoldFire,
                    _ => FireStance.FireAtWill
                };
                string stanceLabel = stance switch
                {
                    FireStance.FireAtWill => "Fire at will",
                    FireStance.ReturnFire => "Return fire",
                    FireStance.HoldFire => "Hold fire",
                    _ => "Fire at will"
                };

                int totalUnits = _unitViews.Selected.Count;
                int sent = 0;
                foreach (EntityId id in _unitViews.Selected)
                {
                    if (_session.SetFireStance(id, stance)) sent++;
                }

                string stanceMsg = sent == 1
                    ? $"Fire stance set to {stanceLabel} for 1 unit"
                    : $"Fire stance set to {stanceLabel} for {sent} units";
                _orderNotification?.ShowNotification(stanceMsg);
                _status.Text = stanceMsg;
                UpdateSelectionPresentation();
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.E || key.PhysicalKeycode == Key.E) && _unitViews.Selected.Count > 0)
            {
                OnStopRequested();
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.G || key.PhysicalKeycode == Key.G) && _unitViews.Selected.Count > 0)
            {
                bool hasFlaresSelected = false;
                foreach (EntityId id in _unitViews.Selected)
                {
                    if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                        (obs.CountermeasureFlags & CountermeasureFlags.HasFlares) != 0)
                    {
                        hasFlaresSelected = true;
                        break;
                    }
                }
                if (hasFlaresSelected)
                {
                    OnDeployFlaresRequested();
                }
                else
                {
                    OnDeploySmokeRequested();
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.L || key.PhysicalKeycode == Key.L) && _unitViews.Selected.Count > 0)
            {
                OnHelicopterToggleFlightRequested();
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.H || key.PhysicalKeycode == Key.H) && _unitViews.Selected.Count > 0)
            {
                OnHelicopterToggleAltitudeRequested();
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.F || key.PhysicalKeycode == Key.F) && _unitViews.Selected.Count > 0 && _contextActionPanel?.FastRopeButton.Visible == true && !_contextActionPanel.FastRopeButton.Disabled)
            {
                _contextActionPanel.SetTargetingMode(_contextActionPanel.TargetingMode == ContextTargetingMode.FastRope
                    ? ContextTargetingMode.None
                    : ContextTargetingMode.FastRope);
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.K || key.PhysicalKeycode == Key.K) && _unitViews.Selected.Count > 0 && _contextActionPanel?.SlingButton.Visible == true && !_contextActionPanel.SlingButton.Disabled)
            {
                bool hasLoad = false;
                foreach (EntityId id in _unitViews.Selected)
                {
                    if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) && obs.SlingLoadId != default)
                    {
                        hasLoad = true;
                        break;
                    }
                }
                ContextTargetingMode nextMode = hasLoad ? ContextTargetingMode.SlingDrop : ContextTargetingMode.SlingPickup;
                _contextActionPanel.SetTargetingMode(_contextActionPanel.TargetingMode == nextMode ? ContextTargetingMode.None : nextMode);
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.B || key.PhysicalKeycode == Key.B) && _unitViews.Selected.Count > 0 && _contextActionPanel?.FireMissionButton.Visible == true && !_contextActionPanel.FireMissionButton.Disabled)
            {
                bool hasActiveBarrage = false;
                foreach (EntityId id in _unitViews.Selected)
                {
                    if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) && obs.ArtilleryFlags != ArtilleryFlags.None)
                    {
                        hasActiveBarrage = true;
                        break;
                    }
                }
                if (hasActiveBarrage && _contextActionPanel.TargetingMode != ContextTargetingMode.FireMission)
                {
                    OnCancelFireMissionRequested();
                }
                else
                {
                    _contextActionPanel.SetTargetingMode(_contextActionPanel.TargetingMode == ContextTargetingMode.FireMission
                        ? ContextTargetingMode.None
                        : ContextTargetingMode.FireMission);
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.T || key.PhysicalKeycode == Key.T) && _unitViews.Selected.Count > 0 && _contextActionPanel?.AttackGroundButton.Visible == true && !_contextActionPanel.AttackGroundButton.Disabled)
            {
                _contextActionPanel.SetTargetingMode(_contextActionPanel.TargetingMode == ContextTargetingMode.AttackGround
                    ? ContextTargetingMode.None
                    : ContextTargetingMode.AttackGround);
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.N || key.PhysicalKeycode == Key.N) && _unitViews.Selected.Count > 0 && _contextActionPanel?.EnterBuildingButton.Visible == true && !_contextActionPanel.EnterBuildingButton.Disabled)
            {
                _contextActionPanel.SetTargetingMode(_contextActionPanel.TargetingMode == ContextTargetingMode.EnterBuilding
                    ? ContextTargetingMode.None
                    : ContextTargetingMode.EnterBuilding);
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.U || key.PhysicalKeycode == Key.U) && _unitViews.Selected.Count > 0 && _contextActionPanel?.ExitBuildingButton.Visible == true && !_contextActionPanel.ExitBuildingButton.Disabled)
            {
                OnExitBuildingRequested();
                GetViewport().SetInputAsHandled();
                return;
            }

            if ((key.Keycode == Key.Y || key.PhysicalKeycode == Key.Y) && _unitViews.Selected.Count > 0 && _contextActionPanel?.GarrisonRoofButton.Visible == true && !_contextActionPanel.GarrisonRoofButton.Disabled)
            {
                OnToggleGarrisonRoofRequested();
                GetViewport().SetInputAsHandled();
                return;
            }
            return;
        }

        if (_cameraRig.HandleZoomInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventMouseButton mouse) return;

        if (mouse.ButtonIndex == MouseButton.Left && !mouse.Pressed)
        {
            if (_selectionController.IsPointerDown)
            {
                HandleLeftMouseRelease(mouse.Position, mouse.ShiftPressed);
            }
            return;
        }

        if (mouse.ButtonIndex == MouseButton.Right && !mouse.Pressed)
        {
            if (_previewController?.IsFacingGestureActive == true)
            {
                HandleRightMouseRelease(mouse.Position);
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        if (!mouse.Pressed) return;

        if (mouse.ButtonIndex == MouseButton.Left)
        {
            RefreshOccludingPanels();
            if (SelectionController.IsPointOccluded(mouse.Position, _inputOccludingPanels))
            {
                return;
            }

            if (_hudTargetingMode != HudTargetingMode.None)
            {
                ExecuteHudTarget(mouse.Position);
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.BoardTransport)
            {
                if (TryGetOwnUnitUnderMouse(mouse.Position, out EntityId targetTransportId))
                {
                    if (_session.Replica.TryGetOwnUnit(targetTransportId, out OwnUnitObservation transObs))
                    {
                        if (transObs.PassengerCapacity == 0)
                        {
                            _orderNotification?.ShowNotification("Target is not a transport vehicle");
                            _status.Text = "Target is not a transport vehicle";
                        }
                        else if (transObs.PassengerCount >= transObs.PassengerCapacity)
                        {
                            _orderNotification?.ShowNotification("Transport is full (0 seats available)");
                            _status.Text = "Transport is full (0 seats available)";
                        }
                        else
                        {
                            int available = transObs.PassengerCapacity - transObs.PassengerCount;
                            int total = _unitViews.Selected.Count;
                            int sent = 0;
                            foreach (EntityId soldierId in _unitViews.Selected)
                            {
                                if (sent < available)
                                {
                                    if (_session.BoardTransport(soldierId, targetTransportId))
                                        sent++;
                                }
                            }
                            string msg = sent < total
                                ? $"Boarding transport #{targetTransportId.Value}: {sent} ordered to board ({total - sent} capacity exceeded)"
                                : $"Boarding transport #{targetTransportId.Value}: {sent} infantry ordered to board";
                            _orderNotification?.ShowNotification(msg);
                            _status.Text = msg;
                            _contextActionPanel.CancelTargeting();
                            _orderCursorOverlay?.HideTooltip();
                        }
                    }
                }
                else
                {
                    _orderNotification?.ShowNotification("Click a friendly transport to board");
                    _status.Text = "Click a friendly transport to board";
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.Disembark)
            {
                if (TryGetGroundPositionUnderMouse(mouse.Position, out NumericsVector2 groundPos))
                {
                    if (TryGetFirstSelectedUnit(out EntityId transportId))
                    {
                        _session.Disembark(transportId, groundPos);
                        string transName = _session.Replica.TryGetOwnUnit(transportId, out OwnUnitObservation tObs) ? GetUnitDisplayName(tObs.Definition) : "Transport";
                        string msg = $"Disembark order submitted for {transName}";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                        _contextActionPanel.CancelTargeting();
                        _orderCursorOverlay?.HideTooltip();
                    }
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.LandAt)
            {
                if (TryGetGroundPositionUnderMouse(mouse.Position, out NumericsVector2 groundPos))
                {
                    int ordered = 0;
                    foreach (EntityId unitId in _unitViews.Selected)
                    {
                        if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                            obs.HasFlightCapability &&
                            obs.FlightPhase != FlightPhase.Crashing)
                        {
                            if (_session.OrderHelicopter(unitId, HelicopterOrderKind.Land, obs.AltitudeBand, hasTarget: true, target: groundPos))
                                ordered++;
                        }
                    }
                    if (ordered > 0)
                    {
                        string msg = ordered == 1
                            ? "Landing order submitted (1 helicopter)"
                            : $"Landing order submitted ({ordered} helicopters)";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                    }
                    _contextActionPanel.CancelTargeting();
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.HideTooltip();
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.FastRope)
            {
                if (TryGetGroundPositionUnderMouse(mouse.Position, out NumericsVector2 groundPos))
                {
                    int ordered = 0;
                    foreach (EntityId unitId in _unitViews.Selected)
                    {
                        if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                            obs.HasFlightCapability &&
                            obs.FlightPhase != FlightPhase.Crashing)
                        {
                            if (_session.OrderHelicopter(unitId, HelicopterOrderKind.FastRope, obs.AltitudeBand, hasTarget: true, target: groundPos))
                                ordered++;
                        }
                    }
                    if (ordered > 0)
                    {
                        string msg = ordered == 1
                            ? "Fast-rope insertion ordered (1 helicopter)"
                            : $"Fast-rope insertion ordered ({ordered} helicopters)";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                    }
                    else
                    {
                        _orderNotification?.ShowNotification("Fast-rope requires embarked infantry and clear land/forest");
                        _status.Text = "Fast-rope requires embarked infantry and clear land/forest";
                    }
                    _contextActionPanel.CancelTargeting();
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.HideTooltip();
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.SlingPickup)
            {
                if (TryGetOwnUnitUnderMouse(mouse.Position, out EntityId targetCargoId) &&
                    _session.Replica.TryGetOwnUnit(targetCargoId, out OwnUnitObservation cargoObs))
                {
                    float maxSlingKg = GetSelectedHelicopterMaxSlingKg();
                    UnitDefinition? cDef = cargoObs.Definition < _catalog.Units.Length ? _catalog.Units[cargoObs.Definition] : null;
                    string cargoName = GetUnitDisplayName(cargoObs.Definition);
                    if (cDef is null || !cDef.CanSlingLoad || cDef.CargoWeightKg <= 0f)
                    {
                        string msg = $"{cargoName} cannot be sling-loaded";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                    }
                    else if (cDef.CargoWeightKg > maxSlingKg)
                    {
                        string msg = $"{cargoName} is too heavy for sling ({cDef.CargoWeightKg / 1000f:0.1}t > {maxSlingKg / 1000f:0.1}t)";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                    }
                    else
                    {
                        int ordered = 0;
                        foreach (EntityId unitId in _unitViews.Selected)
                        {
                            if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                                obs.HasFlightCapability &&
                                obs.FlightPhase != FlightPhase.Crashing &&
                                obs.SlingLoadId == default)
                            {
                                if (_session.OrderHelicopter(unitId, HelicopterOrderKind.SlingPickup, obs.AltitudeBand, targetUnit: targetCargoId))
                                {
                                    ordered++;
                                    break; // One helicopter picks up one target vehicle
                                }
                            }
                        }
                        if (ordered > 0)
                        {
                            string msg = $"Sling pickup ordered for {cargoName} ({cDef.CargoWeightKg / 1000f:0.1}t)";
                            _orderNotification?.ShowNotification(msg);
                            _status.Text = msg;
                            _contextActionPanel.CancelTargeting();
                            _orderCursorOverlay?.HideTooltip();
                        }
                    }
                }
                else
                {
                    _orderNotification?.ShowNotification("Click a friendly light or APC vehicle to sling-load");
                    _status.Text = "Click a friendly light or APC vehicle to sling-load";
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.SlingDrop)
            {
                if (TryGetGroundPositionUnderMouse(mouse.Position, out NumericsVector2 groundPos))
                {
                    int ordered = 0;
                    foreach (EntityId unitId in _unitViews.Selected)
                    {
                        if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                            obs.HasFlightCapability &&
                            obs.FlightPhase != FlightPhase.Crashing &&
                            obs.SlingLoadId != default)
                        {
                            if (_session.OrderHelicopter(unitId, HelicopterOrderKind.SlingDrop, obs.AltitudeBand, hasTarget: true, target: groundPos))
                                ordered++;
                        }
                    }
                    if (ordered > 0)
                    {
                        string msg = "Sling cargo drop ordered";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                    }
                    _contextActionPanel.CancelTargeting();
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.HideTooltip();
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.FireMission)
            {
                if (TryGetGroundPositionUnderMouse(mouse.Position, out NumericsVector2 groundPos))
                {
                    int ordered = 0;
                    int outOfRange = 0;
                    foreach (EntityId unitId in _unitViews.Selected)
                    {
                        if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                            obs.Ammunition > 0 &&
                            TryGetUnitIndirectWeapon(obs.Definition, out WeaponData? weapon))
                        {
                            float dist = NumericsVector2.Distance(obs.Position, groundPos);
                            if (dist >= weapon!.MinRange - 0.5f && dist <= weapon.Range + 0.5f)
                            {
                                if (_session.FireMission(unitId, groundPos))
                                    ordered++;
                            }
                            else
                            {
                                outOfRange++;
                            }
                        }
                    }

                    if (ordered > 0)
                    {
                        string msg = ordered == 1
                            ? "Rocket barrage ordered (1 launcher)"
                            : $"Rocket barrage ordered ({ordered} launchers)";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                        _contextActionPanel.CancelTargeting();
                        _destination.HideZoneCircle();
                        _orderCursorOverlay?.HideTooltip();
                    }
                    else if (outOfRange > 0)
                    {
                        string msg = "Target is outside artillery firing range (350m - 4000m)";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                    }
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.AttackGround)
            {
                if (_previewController is not null &&
                    _previewController.TryRaycast(_camera, mouse.Position, out Vector3 hitPoint3D, out _, out _, out StructureId hitStructureId))
                {
                    StructureId targetStructureId = hitStructureId != default &&
                        _buildingDestructionView is not null &&
                        _buildingDestructionView.ContainsStructure(hitStructureId)
                            ? hitStructureId
                            : default;
                    System.Numerics.Vector3 targetPoint = new(hitPoint3D.X, hitPoint3D.Y, hitPoint3D.Z);
                    int ordered = 0;
                    uint minSeq = 0;
                    uint maxSeq = 0;
                    foreach (EntityId unitId in _unitViews.Selected)
                    {
                        if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                            obs.Ammunition > 0 &&
                            obs.ContainerId == default &&
                            obs.Definition < _catalog.Units.Length &&
                            _catalog.Weapons is not null)
                        {
                            string? wId = _catalog.Units[obs.Definition].Weapon;
                            WeaponData? weapon = null;
                            if (wId is not null)
                            {
                                for (int wi = 0; wi < _catalog.Weapons.Length; wi++)
                                {
                                    if (string.Equals(_catalog.Weapons[wi].Id, wId, StringComparison.Ordinal))
                                    {
                                        weapon = _catalog.Weapons[wi];
                                        break;
                                    }
                                }
                            }
                            if (weapon is not null && !weapon.IsIndirectFire && !weapon.AirTargetsOnly && weapon.StructuralDamage > 0)
                            {
                                if (_session.AttackGround(unitId, targetPoint, targetStructureId))
                                {
                                    ordered++;
                                    uint seq = _session.LastSubmittedCommandSequence;
                                    if (minSeq == 0 || seq < minSeq) minSeq = seq;
                                    if (seq > maxSeq) maxSeq = seq;
                                }
                            }
                        }
                    }

                    if (ordered > 0)
                    {
                        string targetDesc = targetStructureId != default ? "building" : "ground point";
                        _pendingAttackGroundMinSeq = minSeq;
                        _pendingAttackGroundMaxSeq = maxSeq;
                        _pendingAttackGroundTargetDesc = targetDesc;
                        string msg = ordered == 1
                            ? $"Sending attack {targetDesc} order (1 unit)"
                            : $"Sending attack {targetDesc} order ({ordered} units)";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                        _contextActionPanel.CancelTargeting();
                        _destination.HideZoneCircle();
                        _orderCursorOverlay?.HideTooltip();
                    }
                    else
                    {
                        string msg = "No selected direct-fire unit with structural ammo available";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                    }
                }
                else
                {
                    string msg = "No valid attack ground target under cursor";
                    _orderNotification?.ShowNotification(msg);
                    _status.Text = msg;
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode == ContextTargetingMode.EnterBuilding)
            {
                StructureId targetBuilding = default;
                if (_buildingGarrisonView is not null)
                {
                    if (_buildingGarrisonView.TryGetBadgeAtScreenPoint(mouse.Position, out StructureId badgeStruct))
                    {
                        targetBuilding = badgeStruct;
                    }
                    else if (_previewController is not null &&
                             _previewController.TryRaycast(_camera, mouse.Position, out _, out _, out _, out StructureId hitStruct) &&
                             _buildingGarrisonView.IsGarrisonable(hitStruct))
                    {
                        targetBuilding = hitStruct;
                    }
                }

                if (targetBuilding != default)
                {
                    IssueEnterBuildingForSelection(targetBuilding);
                    _contextActionPanel.CancelTargeting();
                    _destination.HideZoneCircle();
                    _orderCursorOverlay?.HideTooltip();
                }
                else
                {
                    _orderNotification?.ShowNotification("Click a building section to garrison");
                    _status.Text = "Click a building section to garrison";
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_selectionController.BeginPress(mouse.Position, mouse.ShiftPressed, mouse.DoubleClick, _inputOccludingPanels))
            {
                _selectionBoxOverlay?.HideBox();
            }
            return;
        }

        if (mouse.ButtonIndex == MouseButton.Right)
        {
            if (mouse.AltPressed) return;
            RefreshOccludingPanels();
            if (SelectionController.IsPointOccluded(mouse.Position, _inputOccludingPanels))
                return;

            if (_hudTargetingMode != HudTargetingMode.None)
            {
                CancelHudTargeting();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_contextActionPanel?.TargetingMode != ContextTargetingMode.None)
            {
                _contextActionPanel?.CancelTargeting();
                _orderCursorOverlay?.HideTooltip();
                _status.Text = "Targeting cancelled";
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_unitViews.Selected.Count > 0)
            {
                if (TryGetTargetContactUnderMouse(mouse.Position, out ContactId targetContact))
                {
                    _destination.Clear();
                    IssueAttackOrBarrageOnContact(targetContact);
                    return;
                }

                if (HasSelectedBoardableUnits() &&
                    TryGetOwnUnitUnderMouse(mouse.Position, out EntityId targetTransportId) &&
                    _session.Replica.TryGetOwnUnit(targetTransportId, out OwnUnitObservation transObs) &&
                    transObs.PassengerCapacity > 0 &&
                    CanSelectedUnitsBoardTransport(transObs))
                {
                    int available = transObs.PassengerCapacity - transObs.PassengerCount;
                    if (available <= 0)
                    {
                        _orderNotification?.ShowNotification("Transport is full (0 seats available)");
                        _status.Text = "Transport is full (0 seats available)";
                        GetViewport().SetInputAsHandled();
                        return;
                    }

                    int sent = 0;
                    int total = 0;
                    UnitDefinition? transDef = transObs.Definition < _catalog.Units.Length ? _catalog.Units[transObs.Definition] : null;
                    bool transAcceptsVehicles = transDef?.Flight?.CanLoadInternalVehicles == true;
                    foreach (EntityId unitId in _unitViews.Selected)
                    {
                        if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                            obs.Definition < _catalog.Units.Length &&
                            obs.SlungById == default)
                        {
                            UnitDefinition uDef = _catalog.Units[obs.Definition];
                            bool isInf = string.Equals(uDef.Classification, "infantry", StringComparison.OrdinalIgnoreCase);
                            bool canBoard = isInf || (uDef.CanInternalLoad && transAcceptsVehicles);
                            if (canBoard)
                            {
                                total++;
                                if (_session.BoardTransport(unitId, targetTransportId))
                                    sent++;
                            }
                        }
                    }

                    if (sent > 0)
                    {
                        string msg = sent < total
                            ? $"Loading transport #{targetTransportId.Value}: {sent} ordered ({total - sent} capacity exceeded)"
                            : $"Loading transport #{targetTransportId.Value}: {sent} ordered to board";
                        _orderNotification?.ShowNotification(msg);
                        _status.Text = msg;
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }

                if (HasSelectedGarrisonableInfantry() &&
                    _buildingGarrisonView is not null &&
                    TryPickGarrisonableBuilding(mouse.Position, out StructureId rmbBuildingId))
                {
                    if (IssueEnterBuildingForSelection(rmbBuildingId))
                    {
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                }

                bool isReverse = Input.IsKeyPressed(Key.R);
                MoveMode moveMode = isReverse ? MoveMode.Reverse : MoveMode.Forward;
                if (_previewController.BeginFacingGesture(_camera, mouse.Position, _unitViews, _session, out string failMsg, moveMode))
                {
                    GetViewport().SetInputAsHandled();
                }
                else if (!string.IsNullOrEmpty(failMsg))
                {
                    _status.Text = failMsg;
                }
            }
            else if (_contactViews.Selected.Count > 0)
            {
                _orderNotification?.ShowNotification("Enemy contacts cannot receive orders");
                _status.Text = "Enemy contacts cannot receive orders";
            }
        }
    }

    private bool TryGetTargetContactUnderMouse(Vector2 mousePos, out ContactId targetContact)
    {
        targetContact = default;
        if (_roleBadgeOverlay is not null &&
            _roleBadgeOverlay.TryHitBadge(mousePos, out _, out ContactId? badgeEnemy) &&
            badgeEnemy is not null)
        {
            targetContact = badgeEnemy.Value;
            return true;
        }

        if (_contactViews is null) return false;

        float bestScoreSq = float.MaxValue;
        bool found = false;
        foreach ((ContactId id, ContactView view) in _contactViews.Views)
        {
            if (!view.Root.Visible) continue;
            bool isAirborne = view.Altitude > 2.0f;
            float maxPickSq = isAirborne ? (42f * 42f) : (28f * 28f);

            Vector3 center = view.Root.Position + Vector3.Up * (1.5f + view.Altitude);
            float distSq = float.MaxValue;
            if (!_camera.IsPositionBehind(center))
            {
                distSq = _camera.UnprojectPosition(center).DistanceSquaredTo(mousePos);
            }
            if (isAirborne)
            {
                // Also allow clicking an airborne contact's ground ring/shadow marker
                Vector3 groundMarker = view.Root.Position + Vector3.Up * 0.5f;
                if (!_camera.IsPositionBehind(groundMarker))
                {
                    float groundDistSq = _camera.UnprojectPosition(groundMarker).DistanceSquaredTo(mousePos);
                    if (groundDistSq < distSq)
                        distSq = groundDistSq;
                }
            }

            if (distSq < maxPickSq && distSq < bestScoreSq)
            {
                bestScoreSq = distSq;
                targetContact = id;
                found = true;
            }
        }
        if (found) return true;

        // Also allow targeting spotted enemy garrisons by hovering/clicking their building or tactical badge
        if (_buildingGarrisonView is not null &&
            TryPickGarrisonableBuilding(mousePos, out StructureId hitBuilding) &&
            TryGetCurrentHostileContactInBuilding(hitBuilding, out targetContact))
        {
            return true;
        }
        return false;
    }

    private bool TryGetOwnUnitUnderMouse(Vector2 mousePos, out EntityId unitId)
    {
        unitId = default;
        if (_roleBadgeOverlay is not null &&
            _roleBadgeOverlay.TryHitBadge(mousePos, out EntityId? badgeUnit, out _) &&
            badgeUnit is not null)
        {
            unitId = badgeUnit.Value;
            return true;
        }

        if (_unitViews is null) return false;

        float nearestDistance = 28 * 28;
        bool found = false;
        foreach ((EntityId id, UnitView view) in _unitViews.Views)
        {
            if (!view.Root.Visible) continue;
            Vector3 center = view.Root.Position + Vector3.Up;
            if (_camera.IsPositionBehind(center)) continue;
            float distance = _camera.UnprojectPosition(center).DistanceSquaredTo(mousePos);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                unitId = id;
                found = true;
            }
        }
        return found;
    }

    private bool TryGetGroundPositionUnderMouse(Vector2 mousePos, out NumericsVector2 groundPos)
    {
        groundPos = default;
        Vector3 origin = _camera.ProjectRayOrigin(mousePos);
        Vector3 normal = _camera.ProjectRayNormal(mousePos);

        if (MathF.Abs(normal.Y) < 1e-5f) return false;
        float t = -origin.Y / normal.Y;
        if (t < 0f) return false;
        Vector3 hit = origin + normal * t;
        if (hit.X < 0f || hit.X > _map.Width || hit.Z < 0f || hit.Z > _map.Depth) return false;
        groundPos = new NumericsVector2(hit.X, hit.Z);
        return true;
    }

    private void HandleLeftMouseMotion(Vector2 position)
    {
        if (_selectionController.UpdateDrag(position))
        {
            _selectionBoxOverlay?.ShowBox(_selectionController.CurrentBoxRect);
        }
        else
        {
            _selectionBoxOverlay?.HideBox();
        }
    }

    private void HandleLeftMouseRelease(Vector2 position, bool shiftPressed)
    {
        _selectionBoxOverlay?.HideBox();
        Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
        SelectionReleaseKind kind = _selectionController.CompleteRelease(
            _camera,
            _unitViews,
            _contactViews,
            _roleBadgeOverlay,
            position,
            viewportSize,
            shiftPressed,
            out _,
            out _);

        if (kind is SelectionReleaseKind.BoxSelect or SelectionReleaseKind.ClickSelect or SelectionReleaseKind.DoubleClickSelect)
        {
            if (kind == SelectionReleaseKind.ClickSelect &&
                _unitViews.Selected.Count == 0 &&
                _contactViews.Selected.Count == 0 &&
                _buildingGarrisonView is not null &&
                TryPickGarrisonableBuilding(position, out StructureId clickedBuilding) &&
                CountOwnUnitsInBuilding(clickedBuilding) > 0)
            {
                SelectOwnUnitsInBuilding(clickedBuilding, shiftPressed);
            }
            _unitViews.UpdateSelectionVisuals();
            UpdateSelectionPresentation();
            _previewController?.OnUnitSelectionChanged();
        }
    }

    private void HandleRightMouseRelease(Vector2 position)
    {
        if (_previewController.EndFacingGesture(_camera, position, _unitViews, _session, out string moveMsg))
        {
            _status.Text = moveMsg;
        }
        else if (!string.IsNullOrEmpty(moveMsg))
        {
            _status.Text = moveMsg;
        }
    }

    private bool IsCommandSessionFinished => _session.Mission.Status is MissionStatus.Victory or MissionStatus.Defeat or MissionStatus.Draw;

    internal void BeginHudTargeting(HudTargetingMode mode)
    {
        if (mode == HudTargetingMode.None)
        {
            CancelHudTargeting();
            return;
        }
        if (IsCommandSessionFinished || _unitViews.Selected.Count == 0) return;
        _commandPanel.UpdateSelection(_unitViews.Selected, _session.Replica, false);
        bool disabled = mode switch
        {
            HudTargetingMode.Move => _commandPanel.MoveButton.Disabled,
            HudTargetingMode.Reverse => _commandPanel.ReverseButton.Disabled,
            HudTargetingMode.Attack => _commandPanel.AttackButton.Disabled,
            _ => true
        };
        if (disabled) return;
        CancelActiveDrag();
        _contextActionPanel?.CancelTargeting();
        _hudTargetSelectionHash = ComputeSelectionHash(_unitViews.Selected);
        _hudTargetSelectionCount = _unitViews.Selected.Count;
        _hudTargetingMode = mode;
        _commandPanel?.SetTargetingMode(mode);
    }

    private void UpdateAttackHoverTooltip(Vector2 mousePos, Vector2 viewportSize)
    {
        if (TryGetTargetContactUnderMouse(mousePos, out ContactId hoverContact) &&
            _session.Replica.TryGetContact(hoverContact, out ContactObservation contact))
        {
            if (contact.State != ContactState.Current)
            {
                _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Blocked, "Last-known marker cannot be attacked");
            }
            else
            {
                float nearestDist = float.MaxValue;
                bool anyInRange = false;
                foreach (EntityId unitId in _unitViews.Selected)
                {
                    if (!_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation own) || own.Ammunition == 0) continue;
                    float dist = NumericsVector2.Distance(own.Position, contact.Position);
                    if (dist < nearestDist) nearestDist = dist;
                    if (own.Definition < _catalog.Units.Length)
                    {
                        string? weaponId = _catalog.Units[own.Definition].Weapon;
                        if (weaponId is not null && _catalog.Weapons is not null)
                        {
                            for (int i = 0; i < _catalog.Weapons.Length; i++)
                            {
                                if (string.Equals(_catalog.Weapons[i].Id, weaponId, StringComparison.Ordinal))
                                {
                                    if (dist <= _catalog.Weapons[i].Range) anyInRange = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                string rangeTag = anyInRange ? "IN RANGE" : "OUT OF RANGE";
                string distText = nearestDist < float.MaxValue ? $"{nearestDist:F0}m" : "-";
                _orderCursorOverlay?.SetCustomTooltip(
                    true,
                    anyInRange ? DestinationValidity.Valid : DestinationValidity.Adjusted,
                    $"Attack Contact #{contact.Id.Value} ({distText} / {rangeTag}) [LMB]");
            }
        }
        else
        {
            _orderCursorOverlay?.SetCustomTooltip(true, DestinationValidity.Adjusted, "Choose observed enemy contact [LMB] / [Esc] cancel");
        }
        _orderCursorOverlay?.UpdatePosition(mousePos, viewportSize);
    }

    private void CancelHudTargeting()
    {
        if (_hudTargetingMode == HudTargetingMode.None) return;
        _hudTargetingMode = HudTargetingMode.None;
        _commandPanel?.SetTargetingMode(HudTargetingMode.None);
        _orderCursorOverlay?.HideTooltip();
        _destination?.HideGhost();
        _destination?.ClearGroupPreviewSlots();
    }

    private void OnContextTargetingChanged(ContextTargetingMode mode)
    {
        if (mode != ContextTargetingMode.None) CancelHudTargeting();
    }

    internal void ExecuteHudTarget(Vector2 position)
    {
        UpdateSelectionPresentation();
        if (_hudTargetingMode == HudTargetingMode.None || IsCommandSessionFinished) return;
        if (_hudTargetingMode == HudTargetingMode.Attack)
        {
            if (!TryGetTargetContactUnderMouse(position, out ContactId contact))
            {
                _orderNotification?.ShowNotification("Choose an enemy contact. Attack ground is unavailable.");
                return;
            }
            ExecuteHudAttackTarget(contact);
        }
        else
        {
            MoveMode mode = _hudTargetingMode == HudTargetingMode.Reverse ? MoveMode.Reverse : MoveMode.Forward;
            bool submitted = _previewController.ExecuteMoveClick(_camera, position, _unitViews, _session, out string message, null, mode);
            if (message.Length > 0) _status.Text = message;
            if (submitted) CancelHudTargeting();
        }
    }

    internal void ExecuteHudAttackTarget(ContactId contact)
    {
        if (_hudTargetingMode != HudTargetingMode.Attack || IsCommandSessionFinished) return;
        if (!_session.Replica.TryGetContact(contact, out ContactObservation observation) || observation.State != ContactState.Current)
        {
            _orderNotification?.ShowNotification("Target is no longer observed. Last-known markers cannot be attacked.");
            return;
        }
        int sent = 0;
        foreach (EntityId id in _unitViews.Selected)
            if (_session.Attack(id, contact)) sent++;
        string message = sent == 0 ? "Attack order could not be submitted. Choose another target."
            : $"Attack order submitted for {sent} of {_unitViews.Selected.Count} units";
        _orderNotification?.ShowNotification(message);
        _status.Text = message;
        if (sent == 0) return;
        _destination.Clear();
        CancelHudTargeting();
    }

    private void OnHudStanceRequested(FireStance stance)
    {
        if (IsCommandSessionFinished) return;
        CancelHudTargeting();
        int sent = 0;
        foreach (EntityId id in _unitViews.Selected)
            if (_session.SetFireStance(id, stance)) sent++;
        string label = stance switch
        {
            FireStance.HoldFire => "Hold fire",
            FireStance.ReturnFire => "Return fire",
            _ => "Fire at will"
        };
        string message = $"Fire stance set to {label} for {sent} units";
        _orderNotification?.ShowNotification(message);
        _status.Text = message;
    }

    internal void CancelActiveDrag()
    {
        _selectionController.CancelDrag();
        _selectionBoxOverlay?.HideBox();
        _previewController?.CancelFacingGesture();
        _minimap?.CancelDrag();
        CancelHudTargeting();
        if (_contextActionPanel?.TargetingMode != ContextTargetingMode.None)
        {
            _contextActionPanel?.CancelTargeting();
            _orderCursorOverlay?.HideTooltip();
        }
    }

    internal void RefreshOccludingPanels()
    {
        if (_topHud is null || _utilityHud is null || _selectionCard is null)
            return;

        Rect2 topRect = _topHud.GetGlobalRect();
        if (_missionPanel is not null)
        {
            _missionPanel.SetBriefingTopOffset(topRect.End.Y + 10f);
        }
        Rect2 utilityRect = _utilityHud.GetGlobalRect();
        Rect2 cardRect = _selectionCard.Visible ? _selectionCard.GetGlobalRect() : default;
        Rect2 settingsRect = _settingsPanel.IsOpen ? _settingsPanel.GetContainerRect() : default;
        Rect2 missionRect = _missionPanel is not null ? (_missionPanel.IsMissionFinished ? _missionPanel.GetResultRect() : _missionPanel.GetBriefingRect()) : default;
        Rect2 minimapRect = _minimap is not null ? _minimap.GetGlobalRect() : default;

        _inputOccludingPanels[0] = topRect;
        _inputOccludingPanels[1] = utilityRect;
        _inputOccludingPanels[2] = cardRect;
        _inputOccludingPanels[3] = settingsRect;
        _inputOccludingPanels[4] = missionRect;
        _inputOccludingPanels[5] = minimapRect;
        _inputOccludingPanels[6] = _contextActionPanel?.Visible == true ? _contextActionPanel.GetGlobalRect() : default;
        _inputOccludingPanels[7] = _commandPanel?.Visible == true ? _commandPanel.GetGlobalRect() : default;

        _badgeOccludingPanels[0] = topRect;
        _badgeOccludingPanels[1] = utilityRect;
        _badgeOccludingPanels[2] = cardRect;
        _badgeOccludingPanels[3] = _orderNotification is { Visible: true } ? _orderNotification.GetGlobalRect() : default;
        _badgeOccludingPanels[4] = settingsRect;
        _badgeOccludingPanels[5] = missionRect;
        _badgeOccludingPanels[6] = minimapRect;
        _badgeOccludingPanels[7] = _contextActionPanel?.Visible == true ? _contextActionPanel.GetGlobalRect() : default;
        _badgeOccludingPanels[8] = _commandPanel?.Visible == true ? _commandPanel.GetGlobalRect() : default;
    }

    internal static bool TryGetNavigationLargeDebugTarget(
        Key keycode,
        bool altPressed,
        bool ctrlOrCmdPressed,
        bool shiftPressed,
        out NumericsVector2 target)
    {
        if (!altPressed || ctrlOrCmdPressed || shiftPressed)
        {
            target = default;
            return false;
        }

        target = keycode switch
        {
            Key.Key1 => new NumericsVector2(300, 480),
            Key.Key2 => new NumericsVector2(450, 480),
            Key.Key3 => new NumericsVector2(150, 320),
            Key.Key4 => new NumericsVector2(500, 150),
            Key.Key5 => new NumericsVector2(560, 560),
            Key.Key6 => new NumericsVector2(80, 520),
            _ => default
        };
        return target.X > 0f;
    }

    private static readonly string[] ControlGroupSuffixes =
    [
        string.Empty,
        " (Group 1)",
        " (Group 2)",
        " (Group 3)",
        " (Group 4)",
        " (Group 5)",
        " (Group 6)",
        " (Group 7)",
        " (Group 8)",
        " (Group 9)"
    ];

    internal static string GetControlGroupSuffix(int groupNumber) =>
        groupNumber >= 1 && groupNumber <= 9 ? ControlGroupSuffixes[groupNumber] : string.Empty;

    private int _lastTitlePlayerId = -1;
    private string? _lastTitleScenario;
    private string? _lastSubtitleText;

    private enum SelectionPresentationKind { None, SingleUnit, MultipleUnits, Contact }
    private SelectionPresentationKind _presentedKind = SelectionPresentationKind.None;
    private EntityId _presentedSingleUnitId;
    private int _presentedActiveGroup = -1;
    private uint _presentedMultiHash;
    private int _presentedMultiCount = -1;
    private ContactId _presentedContactId;
    private string _cachedStatusText = string.Empty;

    internal void UpdateSelectionPresentation()
    {
        if (_selectionCard is null || _commandPanel is null || _contextActionPanel is null || _orderNotification is null) return;
        bool finished = IsCommandSessionFinished;
        if (_hudTargetingMode != HudTargetingMode.None &&
            (finished || _unitViews.Selected.Count != _hudTargetSelectionCount ||
             ComputeSelectionHash(_unitViews.Selected) != _hudTargetSelectionHash))
            CancelHudTargeting();
        _commandPanel.UpdateSelection(_unitViews.Selected, _session.Replica, finished);
        int activeGroup = _unitViews.GetSelectedControlGroup();

        if (_unitViews.Selected.Count == 1 && TryGetFirstSelectedUnit(out EntityId selId))
        {
            if (_session.Replica.TryGetOwnUnit(selId, out OwnUnitObservation ownUnit))
            {
                _selectionCard.UpdateSingleOwnUnit(ownUnit, activeGroup);
                if (_presentedKind != SelectionPresentationKind.SingleUnit ||
                    _presentedSingleUnitId != selId ||
                    _presentedActiveGroup != activeGroup)
                {
                    _presentedKind = SelectionPresentationKind.SingleUnit;
                    _presentedSingleUnitId = selId;
                    _presentedActiveGroup = activeGroup;
                    string groupSuffix = GetControlGroupSuffix(activeGroup);
                    _cachedStatusText = $"1 unit selected{groupSuffix} / Unit #{ownUnit.Id.Value} / RMB terrain to move, RMB enemy to attack";
                    _status.Text = _cachedStatusText;
                }
            }
        }
        else if (_unitViews.Selected.Count > 1)
        {
            _selectionCard.UpdateMultipleOwnUnits(_unitViews.Selected, _session.Replica, activeGroup);
            uint hash = ComputeSelectionHash(_unitViews.Selected);
            if (_presentedKind != SelectionPresentationKind.MultipleUnits ||
                _presentedMultiCount != _unitViews.Selected.Count ||
                _presentedActiveGroup != activeGroup ||
                _presentedMultiHash != hash)
            {
                _presentedKind = SelectionPresentationKind.MultipleUnits;
                _presentedMultiCount = _unitViews.Selected.Count;
                _presentedActiveGroup = activeGroup;
                _presentedMultiHash = hash;

                string groupSuffix = GetControlGroupSuffix(activeGroup);
                _cachedStatusText = $"{_unitViews.Selected.Count} units selected{groupSuffix} / RMB terrain to move, RMB enemy to attack";
                _status.Text = _cachedStatusText;
            }
        }
        else if (_contactViews.Selected.Count > 0 && TryGetFirstSelectedContact(out ContactId firstSelected))
        {
            if (_contactViews.Views.TryGetValue(firstSelected, out ContactView? cv))
            {
                _selectionCard.UpdateContact(cv.Observation, _session.Tick);
                if (_presentedKind != SelectionPresentationKind.Contact ||
                    _presentedContactId != firstSelected)
                {
                    _presentedKind = SelectionPresentationKind.Contact;
                    _presentedContactId = firstSelected;
                    _cachedStatusText = $"Contact #{cv.Observation.Id.Value} selected / Right-click with armed tank to attack";
                    _status.Text = _cachedStatusText;
                }
            }
        }
        else
        {
            _selectionCard.UpdateEmpty();
            if (_presentedKind != SelectionPresentationKind.None)
            {
                _presentedKind = SelectionPresentationKind.None;
                _cachedStatusText = "No units selected / Left-click unit or contact to inspect";
                _status.Text = _cachedStatusText;
            }
        }

        _contextActionPanel.UpdateContext(_unitViews.Selected, _session.Replica);
        _selectionCard.Visible = _unitViews.Selected.Count > 0 || _contactViews.Selected.Count > 0;
        _commandPanel.Visible = _unitViews.Selected.Count > 0;
        float cardHeight = _selectionCard.PreferredHeight;
        if (_lastSelectionCardHeight != cardHeight)
        {
            _lastSelectionCardHeight = cardHeight;
            _selectionCard.OffsetTop = -HudLayout.Margin - cardHeight;
            _contextActionPanel.OffsetBottom = -HudLayout.Margin - cardHeight - HudLayout.Gap;
            _contextActionPanel.OffsetTop = _contextActionPanel.OffsetBottom - HudLayout.ContextHeight;
            _orderNotification.OffsetBottom = -HudLayout.Margin - Math.Max(cardHeight, HudLayout.CommandHeight) - HudLayout.Gap;
            _orderNotification.OffsetTop = _orderNotification.OffsetBottom - HudLayout.NotificationHeight;
        }
    }

    private void OnRosterUnitClicked(EntityId unitId, bool removeFromSelection)
    {
        if (!_unitViews.Views.ContainsKey(unitId)) return;
        if (removeFromSelection)
        {
            _unitViews.Selected.Remove(unitId);
        }
        else
        {
            _unitViews.Selected.Clear();
            _contactViews.Selected.Clear();
            _unitViews.Selected.Add(unitId);
        }
        _unitViews.UpdateSelectionVisuals();
        UpdateSelectionPresentation();
    }

    private static uint ComputeSelectionHash(HashSet<EntityId> set)
    {
        uint hash = 17;
        foreach (EntityId id in set)
        {
            hash = hash * 31 + id.Value;
        }
        return hash;
    }

    private bool TryGetFirstSelectedUnit(out EntityId first)
    {
        foreach (EntityId id in _unitViews.Selected)
        {
            first = id;
            return true;
        }
        first = default;
        return false;
    }

    private bool TryGetFirstSelectedContact(out ContactId first)
    {
        foreach (ContactId id in _contactViews.Selected)
        {
            first = id;
            return true;
        }
        first = default;
        return false;
    }

    private bool HasSelectedBoardableUnits()
    {
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.Definition < _catalog.Units.Length &&
                obs.SlungById == default)
            {
                UnitDefinition def = _catalog.Units[obs.Definition];
                if (string.Equals(def.Classification, "infantry", StringComparison.OrdinalIgnoreCase) || def.CanInternalLoad)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private bool CanSelectedUnitsBoardTransport(in OwnUnitObservation transObs)
    {
        UnitDefinition? transDef = transObs.Definition < _catalog.Units.Length ? _catalog.Units[transObs.Definition] : null;
        bool acceptsVehicles = transDef?.Flight?.CanLoadInternalVehicles == true;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.Definition < _catalog.Units.Length &&
                obs.SlungById == default)
            {
                UnitDefinition def = _catalog.Units[obs.Definition];
                bool isInf = string.Equals(def.Classification, "infantry", StringComparison.OrdinalIgnoreCase);
                if (isInf || (def.CanInternalLoad && acceptsVehicles))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private float GetSelectedHelicopterLandingRadius()
    {
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.Definition < _catalog.Units.Length &&
                _catalog.Units[obs.Definition].Flight is { } flight)
            {
                return flight.LandingClearanceRadius;
            }
        }
        return 10.0f;
    }

    private float GetSelectedHelicopterMaxSlope()
    {
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.Definition < _catalog.Units.Length &&
                _catalog.Units[obs.Definition].Flight is { } flight)
            {
                return flight.MaxLandingSlopeDegrees;
            }
        }
        return 12.0f;
    }

    private float GetSelectedHelicopterMaxSlingKg()
    {
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.Definition < _catalog.Units.Length &&
                _catalog.Units[obs.Definition].Flight is { } flight)
            {
                return flight.MaxSlingWeightKg;
            }
        }
        return 0f;
    }

    private static string FormatLzBlockReason(LandingZoneStatus status) => status switch
    {
        LandingZoneStatus.BlockedByWater => "Water",
        LandingZoneStatus.BlockedByStructure => "Structure",
        LandingZoneStatus.BlockedByForest => "Dense Forest",
        LandingZoneStatus.SlopeTooSteep => "Slope Too Steep",
        LandingZoneStatus.BlockedByUnit => "Occupied by Unit",
        LandingZoneStatus.BlockedByWreck => "Blocked by Wreck",
        _ => "Out of Bounds"
    };

    private static string FormatFastRopeBlockReason(FastRopeZoneStatus status) => status switch
    {
        FastRopeZoneStatus.BlockedByWater => "Water",
        FastRopeZoneStatus.BlockedByStructure => "Structure",
        FastRopeZoneStatus.NoInfantryAboard => "No Infantry Aboard",
        _ => "Out of Bounds"
    };

    private static string FormatSlingDropBlockReason(SlingDropZoneStatus status) => status switch
    {
        SlingDropZoneStatus.BlockedByWater => "Water",
        SlingDropZoneStatus.BlockedByStructure => "Structure",
        SlingDropZoneStatus.BlockedByForest => "Dense Forest",
        SlingDropZoneStatus.BlockedByUnit => "Occupied by Unit",
        SlingDropZoneStatus.BlockedByWreck => "Blocked by Wreck",
        _ => "Out of Bounds"
    };

    private string GetUnitDisplayName(ushort definition)
    {
        if (_catalog is not null && definition < _catalog.Units.Length)
        {
            UnitDefinition def = _catalog.Units[definition];
            if (!string.IsNullOrWhiteSpace(def.Name)) return def.Name;
            if (!string.IsNullOrWhiteSpace(def.Id)) return def.Id;
        }
        return "Transport";
    }

    private void OnDisembarkHereRequested()
    {
        if (TryGetFirstSelectedUnit(out EntityId transportId) &&
            _session.Replica.TryGetOwnUnit(transportId, out OwnUnitObservation transObs) &&
            transObs.PassengerCount > 0)
        {
            _session.Disembark(transportId, transObs.Position);
            string transName = GetUnitDisplayName(transObs.Definition);
            string msg = $"Disembarking passengers here from {transName}";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
            _contextActionPanel?.CancelTargeting();
            _orderCursorOverlay?.HideTooltip();
        }
    }

    private bool IsUiOccludingMouse()
    {
        RefreshOccludingPanels();
        return SelectionController.IsPointOccluded(GetViewport().GetMousePosition(), _inputOccludingPanels);
    }

    private bool HasSelectedGarrisonableInfantry()
    {
        if (_catalog is null || _unitViews is null) return false;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.ContainerId == default &&
                obs.GarrisonStructureId == default &&
                obs.SlungById == default &&
                obs.Definition < _catalog.Units.Length &&
                string.Equals(_catalog.Units[obs.Definition].Classification, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private bool TryPickGarrisonableBuilding(Vector2 screenPos, out StructureId structureId)
    {
        structureId = default;
        if (_buildingGarrisonView is null) return false;
        if (_buildingGarrisonView.TryGetBadgeAtScreenPoint(screenPos, out StructureId badgeStruct) &&
            _buildingGarrisonView.IsGarrisonable(badgeStruct))
        {
            structureId = badgeStruct;
            return true;
        }
        if (_camera is not null &&
            _previewController is not null &&
            _previewController.TryRaycast(_camera, screenPos, out _, out _, out _, out StructureId hitStruct) &&
            _buildingGarrisonView.IsGarrisonable(hitStruct))
        {
            structureId = hitStruct;
            return true;
        }
        return false;
    }

    private int CountOwnUnitsInBuilding(StructureId structureId)
    {
        int count = 0;
        ReadOnlySpan<OwnUnitObservation> units = _session.Replica.OwnUnits;
        for (int i = 0; i < units.Length; i++)
        {
            if (units[i].GarrisonStructureId == structureId)
                count++;
        }
        return count;
    }

    private bool TryGetCurrentHostileContactInBuilding(StructureId structureId, out ContactId contactId)
    {
        ReadOnlySpan<ContactObservation> contacts = _session.Replica.Contacts;
        for (int i = 0; i < contacts.Length; i++)
        {
            if (contacts[i].State == ContactState.Current && contacts[i].GarrisonStructureId == structureId)
            {
                contactId = contacts[i].Id;
                return true;
            }
        }
        contactId = default;
        return false;
    }

    private void SelectOwnUnitsInBuilding(StructureId structureId, bool additive)
    {
        if (!additive)
        {
            _unitViews.Selected.Clear();
            _contactViews.Selected.Clear();
        }
        ReadOnlySpan<OwnUnitObservation> units = _session.Replica.OwnUnits;
        for (int i = 0; i < units.Length; i++)
        {
            if (units[i].GarrisonStructureId == structureId)
            {
                _unitViews.Selected.Add(units[i].Id);
            }
        }
    }

    internal void OnGarrisonBadgeClicked(StructureId structureId)
    {
        if (CountOwnUnitsInBuilding(structureId) > 0)
        {
            SelectOwnUnitsInBuilding(structureId, Input.IsKeyPressed(Key.Shift));
            _unitViews.UpdateSelectionVisuals();
            UpdateSelectionPresentation();
            _previewController?.OnUnitSelectionChanged();
        }
        else if (TryGetCurrentHostileContactInBuilding(structureId, out ContactId hostileContact))
        {
            if (_unitViews.Selected.Count > 0)
            {
                IssueAttackOrBarrageOnContact(hostileContact);
            }
            else
            {
                _contactViews.Selected.Clear();
                _contactViews.Selected.Add(hostileContact);
                UpdateSelectionPresentation();
            }
        }
    }

    internal bool IssueEnterBuildingForSelection(StructureId structureId, GarrisonSlotKind slotKind = GarrisonSlotKind.Interior)
    {
        if (_catalog is null || _unitViews is null) return false;
        int sent = 0;
        foreach (EntityId unitId in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                obs.ContainerId == default &&
                obs.GarrisonStructureId == default &&
                obs.SlungById == default &&
                obs.Definition < _catalog.Units.Length &&
                string.Equals(_catalog.Units[obs.Definition].Classification, "infantry", StringComparison.OrdinalIgnoreCase))
            {
                if (_session.EnterBuilding(unitId, structureId, slotKind))
                    sent++;
            }
        }
        if (sent > 0)
        {
            string msg = sent == 1
                ? "Ordering 1 infantry unit to garrison building"
                : $"Ordering {sent} infantry units to garrison building";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
            return true;
        }
        return false;
    }

    internal void OnExitBuildingRequested()
    {
        if (_unitViews is null) return;
        int sent = 0;
        foreach (EntityId unitId in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                obs.GarrisonStructureId != default)
            {
                if (_session.ExitBuilding(unitId))
                    sent++;
            }
        }
        if (sent > 0)
        {
            string msg = sent == 1
                ? "Exiting building (1 unit)"
                : $"Exiting building ({sent} units)";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
            _contextActionPanel?.CancelTargeting();
            _orderCursorOverlay?.HideTooltip();
        }
    }

    internal void OnToggleGarrisonRoofRequested()
    {
        if (_unitViews is null) return;
        int garrisoned = 0;
        int onRoof = 0;
        foreach (EntityId unitId in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                obs.GarrisonStructureId != default)
            {
                garrisoned++;
                if (obs.GarrisonSlotKind == GarrisonSlotKind.Roof)
                    onRoof++;
            }
        }
        if (garrisoned == 0) return;

        GarrisonSlotKind targetSlot = onRoof >= garrisoned ? GarrisonSlotKind.Interior : GarrisonSlotKind.Roof;
        int sent = 0;
        foreach (EntityId unitId in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(unitId, out OwnUnitObservation obs) &&
                obs.GarrisonStructureId != default &&
                obs.GarrisonSlotKind != targetSlot)
            {
                if (_session.EnterBuilding(unitId, obs.GarrisonStructureId, targetSlot))
                    sent++;
            }
        }
        if (sent > 0)
        {
            string slotText = targetSlot == GarrisonSlotKind.Roof ? "rooftop" : "interior floors";
            string msg = sent == 1
                ? $"Moving 1 garrisoned unit to {slotText}"
                : $"Moving {sent} garrisoned units to {slotText}";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
        }
    }

    internal void OnHelicopterToggleFlightRequested()
    {
        int helis = 0;
        int airborne = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.HasFlightCapability &&
                obs.FlightPhase != FlightPhase.Crashing)
            {
                helis++;
                if (obs.FlightPhase != FlightPhase.Ground)
                    airborne++;
            }
        }
        if (helis == 0) return;

        HelicopterOrderKind kind = airborne > 0 ? HelicopterOrderKind.Land : HelicopterOrderKind.TakeOff;
        int sent = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.HasFlightCapability &&
                obs.FlightPhase != FlightPhase.Crashing)
            {
                if (_session.OrderHelicopter(id, kind, obs.AltitudeBand))
                    sent++;
            }
        }
        if (sent > 0)
        {
            string action = kind == HelicopterOrderKind.TakeOff ? "Takeoff" : "Landing";
            string msg = sent == 1 ? $"{action} order submitted (1 helicopter)" : $"{action} order submitted ({sent} helicopters)";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
        }
    }

    internal void OnHelicopterToggleAltitudeRequested()
    {
        int helis = 0;
        int lowCount = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.HasFlightCapability &&
                obs.FlightPhase != FlightPhase.Crashing)
            {
                helis++;
                if (obs.AltitudeBand == AltitudeBand.Low)
                    lowCount++;
            }
        }
        if (helis == 0) return;

        AltitudeBand nextBand = lowCount > 0 ? AltitudeBand.High : AltitudeBand.Low;
        int sent = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.HasFlightCapability &&
                obs.FlightPhase != FlightPhase.Crashing)
            {
                if (_session.OrderHelicopter(id, HelicopterOrderKind.SetAltitudeBand, nextBand))
                    sent++;
            }
        }
        if (sent > 0)
        {
            string label = nextBand == AltitudeBand.Low ? "NOE Low" : "Cruise High";
            string msg = $"Altitude set to {label} ({sent} {(sent == 1 ? "helicopter" : "helicopters")})";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
        }
    }

    private void IssueAttackOrBarrageOnContact(ContactId targetContact)
    {
        int totalUnits = _unitViews.Selected.Count;
        int directSent = 0;
        int barrageSent = 0;
        int barrageOutOfRange = 0;
        int barrageNoAmmo = 0;

        bool hasContactPos = _session.Replica.TryGetContact(targetContact, out ContactObservation contactObs);

        foreach (EntityId id in _unitViews.Selected)
        {
            if (hasContactPos &&
                _session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                TryGetUnitIndirectWeapon(obs.Definition, out WeaponData? artyWeapon))
            {
                if (obs.Ammunition <= 0)
                {
                    barrageNoAmmo++;
                    continue;
                }
                float dist = NumericsVector2.Distance(obs.Position, contactObs.Position);
                if (dist >= artyWeapon!.MinRange - 0.5f && dist <= artyWeapon.Range + 0.5f)
                {
                    if (_session.FireMission(id, contactObs.Position))
                        barrageSent++;
                }
                else
                {
                    barrageOutOfRange++;
                }
            }
            else
            {
                if (_session.Attack(id, targetContact))
                    directSent++;
            }
        }

        int totalSent = directSent + barrageSent;
        string msg;
        if (barrageSent > 0 && directSent == 0)
        {
            msg = barrageSent == 1
                ? $"Rocket barrage ordered -> Contact #{targetContact.Value}"
                : $"Rocket barrage ordered ({barrageSent} launchers) -> Contact #{targetContact.Value}";
        }
        else if (totalSent == 0 && barrageOutOfRange > 0)
        {
            msg = $"Target Contact #{targetContact.Value} is outside artillery range (350m - 4000m)";
        }
        else if (totalSent == 0 && barrageNoAmmo > 0)
        {
            msg = "No rockets remaining for barrage";
        }
        else if (totalSent == 0)
        {
            msg = $"Failed to submit attack order / 0 of {totalUnits} units -> Contact #{targetContact.Value}";
        }
        else if (totalSent < totalUnits)
        {
            msg = $"Attack order submitted for {totalSent} of {totalUnits} units -> Contact #{targetContact.Value}";
        }
        else
        {
            msg = $"Attack order submitted / {totalSent} units -> Contact #{targetContact.Value}";
        }

        _orderNotification?.ShowNotification(msg);
        _status.Text = msg;
    }

    internal void OnCancelFireMissionRequested()
    {
        int cancelled = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.ArtilleryFlags != ArtilleryFlags.None)
            {
                if (_session.FireMission(id, default, cancel: true))
                    cancelled++;
            }
        }
        if (cancelled > 0)
        {
            string msg = cancelled == 1 ? "Rocket barrage cancelled" : $"Rocket barrage cancelled ({cancelled} launchers)";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
        }
    }

    internal void OnDeployFlaresRequested()
    {
        int deployed = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                (obs.CountermeasureFlags & CountermeasureFlags.HasFlares) != 0 &&
                obs.FlareCharges > 0 &&
                obs.FlareCooldownTicks == 0 &&
                obs.FlightPhase is FlightPhase.Airborne or FlightPhase.TakingOff or FlightPhase.Landing)
            {
                if (_session.DeployFlares(id, toggleAuto: false))
                    deployed++;
            }
        }
        if (deployed > 0)
        {
            string msg = deployed == 1 ? "Flares deployed!" : $"Flares deployed ({deployed} aircraft)";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
        }
    }

    internal void OnToggleAutoFlaresRequested()
    {
        int toggled = 0;
        bool turningOn = false;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                (obs.CountermeasureFlags & CountermeasureFlags.HasFlares) != 0)
            {
                if (_session.DeployFlares(id, toggleAuto: true))
                {
                    if ((obs.CountermeasureFlags & CountermeasureFlags.AutoFlares) == 0)
                        turningOn = true;
                    toggled++;
                }
            }
        }
        if (toggled > 0)
        {
            string msg = turningOn ? "Auto-flares enabled" : "Auto-flares disabled (manual [G])";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
        }
    }

    internal void OnStopRequested()
    {
        if (IsCommandSessionFinished || _unitViews.Selected.Count == 0) return;
        CancelHudTargeting();
        _contextActionPanel?.CancelTargeting();
        int stopped = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.ContainerId.Value == 0 && obs.Health > 0 && obs.FlightPhase != FlightPhase.Crashing && obs.SlungById.Value == 0)
            {
                if (_session.Stop(id))
                    stopped++;
            }
        }

        if (stopped > 0)
        {
            _previewController?.ClearTrackedOrdersForUnits(_unitViews.Selected);
            string msg = stopped == 1 ? "Unit stopped" : $"Stopped {stopped} units";
            _orderNotification?.ShowNotification(msg);
            _status.Text = msg;
            UpdateSelectionPresentation();
        }
    }

    private bool TryGetUnitIndirectWeapon(ushort definition, out WeaponData? weapon)
    {
        weapon = null;
        if (definition >= _catalog.Units.Length || _catalog.Weapons is null)
            return false;
        string? weaponId = _catalog.Units[definition].Weapon;
        if (weaponId is null)
            return false;
        for (int i = 0; i < _catalog.Weapons.Length; i++)
        {
            if (string.Equals(_catalog.Weapons[i].Id, weaponId, StringComparison.Ordinal) &&
                _catalog.Weapons[i].IsIndirectFire)
            {
                weapon = _catalog.Weapons[i];
                return true;
            }
        }
        return false;
    }

    private bool TryGetSelectedArtilleryWeapon(out WeaponData? weapon, out NumericsVector2 refPosition, out int readyLaunchers)
    {
        weapon = null;
        refPosition = default;
        readyLaunchers = 0;
        foreach (EntityId id in _unitViews.Selected)
        {
            if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) &&
                obs.Ammunition > 0 &&
                TryGetUnitIndirectWeapon(obs.Definition, out WeaponData? w))
            {
                if (weapon is null)
                {
                    weapon = w;
                    refPosition = obs.Position;
                }
                readyLaunchers++;
            }
        }
        return weapon is not null && readyLaunchers > 0;
    }

    internal void OnDeploySmokeRequested()
    {
        int capable = 0;
        int deployed = 0;
        int active = 0;
        int recharging = 0;
        int minCooldownTicks = int.MaxValue;

        foreach (EntityId id in _unitViews.Selected)
        {
            if (!_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation obs) || !obs.HasSmokeCapability)
            {
                continue;
            }

            capable++;
            if (obs.SmokeActive)
            {
                active++;
            }
            else if (obs.SmokeCharges > 0 && obs.SmokeCooldownTicks == 0)
            {
                if (_session.DeploySmoke(id))
                {
                    deployed++;
                }
            }
            else if (obs.SmokeCharges > 0 && obs.SmokeCooldownTicks > 0)
            {
                recharging++;
                if (obs.SmokeCooldownTicks < minCooldownTicks)
                {
                    minCooldownTicks = obs.SmokeCooldownTicks;
                }
            }
        }

        if (capable == 0)
        {
            return;
        }

        string msg;
        if (deployed > 0)
        {
            msg = deployed == 1
                ? "Tactical smoke screen deployed (1 unit)"
                : $"Tactical smoke screen deployed ({deployed} units)";
        }
        else if (active > 0)
        {
            msg = "Smoke screen already active";
        }
        else if (recharging > 0)
        {
            int sec = (int)MathF.Ceiling(minCooldownTicks * Simulation.TickSeconds);
            msg = $"Smoke recharging ({sec}s)";
        }
        else
        {
            msg = "No smoke charges remaining";
        }

        _orderNotification?.ShowNotification(msg);
        _status.Text = msg;
        UpdateSelectionPresentation();
    }

    internal void UpdateViews(double deltaSeconds = Simulation.TickSeconds)
    {
        _unitViews.SetRangeIndicatorsVisible(_showRangeRings);
        int selectedUnits = _unitViews.Selected.Count;
        int selectedContacts = _contactViews.Selected.Count;
        _unitViews.Sync(_session.Replica, _session.RenderTick);
        _contactViews.Sync(_session.Replica, _session.RenderTick, (float)deltaSeconds);
        _wreckViews.Sync(_session.Replica);
        if (selectedUnits != _unitViews.Selected.Count || selectedContacts != _contactViews.Selected.Count)
        {
            if (_unitViews.Selected.Count > 0)
            {
                int groupNum = _unitViews.GetSelectedControlGroup();
                string groupSuffix = groupNum > 0 ? $" (Group {groupNum})" : string.Empty;
                _status.Text = $"{_unitViews.Selected.Count} selected{groupSuffix} / right-click terrain to move";
            }
            else if (_contactViews.Selected.Count > 0)
                _status.Text = $"{_contactViews.Selected.Count} contacts selected / cannot receive orders";
            else
                _status.Text = "No selection / unit or contact removed from view";
        }
        UpdateCollisionDebugOverlay();
        UpdateVegetationDebugOverlay();
    }

    private void UpdateCollisionDebugOverlay()
    {
        _collisionDebugMesh.ClearSurfaces();
        if (!_showVehicleCollisionBounds || _map is null)
        {
            return;
        }

        const int circleSegments = 36;
        const float angleStep = Mathf.Tau / circleSegments;

        var ownUnits = _session.Replica.OwnUnits;
        if (ownUnits.Length == 0) return;

        int totalCircleVertices = ownUnits.Length * circleSegments * 2;
        if (totalCircleVertices > 0)
        {
            _collisionDebugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, CollisionBoundsMaterial);
            for (int u = 0; u < ownUnits.Length; u++)
            {
                var unit = ownUnits[u];
                float colRadius = 0f;
                float nominalSpeed = 8.0f;
                if (unit.Definition < _catalog.Units.Length)
                {
                    colRadius = _catalog.Units[unit.Definition].CollisionRadius;
                    if (colRadius <= 0f) colRadius = _catalog.Units[unit.Definition].Radius;
                    nominalSpeed = _catalog.Units[unit.Definition].Speed;
                }
                if (colRadius <= 0f) colRadius = 2.0f;

                Color ringColor;
                if (unit.NavStatus == PathRequestStatus.WaitingForClearance)
                {
                    ringColor = new Color(0.95f, 0.2f, 0.2f, 0.9f); // Red: stopped and waiting for clearance
                }
                else if (unit.NavStatus == PathRequestStatus.PathFound)
                {
                    float dtSeconds = 0.2f;
                    float dtTicks = dtSeconds * Simulation.TickRate;
                    NumericsVector2 pNow = _session.Replica.SamplePosition(unit.Id, _session.RenderTick);
                    NumericsVector2 pPast = _session.Replica.SamplePosition(unit.Id, MathF.Max(0f, (float)_session.RenderTick - dtTicks));
                    float curSpeed = (pNow - pPast).Length() / dtSeconds;
                    if (curSpeed < nominalSpeed * 0.85f)
                    {
                        ringColor = new Color(0.95f, 0.85f, 0.2f, 0.9f); // Yellow: following distance deceleration / braking
                    }
                    else
                    {
                        ringColor = new Color(0.2f, 0.95f, 0.3f, 0.9f); // Green: free movement
                    }
                }
                else
                {
                    ringColor = new Color(0.2f, 0.95f, 0.3f, 0.9f); // Green: free movement / idle
                }

                _collisionDebugMesh.SurfaceSetColor(ringColor);
                System.Numerics.Vector2 pos = _session.Replica.SamplePosition(unit.Id, _session.RenderTick);
                for (int s = 0; s < circleSegments; s++)
                {
                    float t0 = s * angleStep;
                    float t1 = (s + 1) * angleStep;
                    float x0 = pos.X + colRadius * Mathf.Cos(t0);
                    float z0 = pos.Y + colRadius * Mathf.Sin(t0);
                    float x1 = pos.X + colRadius * Mathf.Cos(t1);
                    float z1 = pos.Y + colRadius * Mathf.Sin(t1);

                    float y0 = _map.GetHeight(x0, z0) + 0.15f;
                    float y1 = _map.GetHeight(x1, z1) + 0.15f;
                    _collisionDebugMesh.SurfaceAddVertex(new Vector3(x0, y0, z0));
                    _collisionDebugMesh.SurfaceAddVertex(new Vector3(x1, y1, z1));
                }
            }
            _collisionDebugMesh.SurfaceEnd();
        }

        // Velocity vectors
        int movingCount = 0;
        for (int u = 0; u < ownUnits.Length; u++)
        {
            if (ownUnits[u].NavStatus == PathRequestStatus.PathFound) movingCount++;
        }

        if (movingCount > 0)
        {
            _collisionDebugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, VelocityDebugMaterial);
            for (int u = 0; u < ownUnits.Length; u++)
            {
                var unit = ownUnits[u];
                if (unit.NavStatus != PathRequestStatus.PathFound) continue;
                System.Numerics.Vector2 pos = _session.Replica.SamplePosition(unit.Id, _session.RenderTick);
                _session.Replica.SampleYaws(unit.Id, _session.RenderTick, out _, out float hullHeading);
                float arrowLen = 3.0f;
                if (unit.Definition < _catalog.Units.Length)
                {
                    float cr = _catalog.Units[unit.Definition].CollisionRadius;
                    if (cr > 0f) arrowLen = cr * 1.2f;
                }
                NumericsVector2 dir = new(-MathF.Sin(hullHeading), -MathF.Cos(hullHeading));
                NumericsVector2 end = pos + dir * arrowLen;
                float y0 = _map.GetHeight(pos.X, pos.Y) + 0.3f;
                float y1 = _map.GetHeight(end.X, end.Y) + 0.3f;
                _collisionDebugMesh.SurfaceAddVertex(new Vector3(pos.X, y0, pos.Y));
                _collisionDebugMesh.SurfaceAddVertex(new Vector3(end.X, y1, end.Y));
            }
            _collisionDebugMesh.SurfaceEnd();
        }
    }

    private void UpdateVegetationDebugOverlay()
    {
        _vegetationDebugMesh.ClearSurfaces();
        if (!_showVegetationDebug || _scenarioGeometry is null || _map is null)
        {
            return;
        }

        IReadOnlyList<VegetationZone> zones = _scenarioGeometry.VegetationZones;
        if (zones.Count == 0)
        {
            return;
        }

        _vegetationDebugMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, VegetationDebugMaterial);

        // 1. Draw vegetation zone wireframes
        for (int z = 0; z < zones.Count; z++)
        {
            VegetationZone zone = zones[z];
            ReadOnlySpan<System.Numerics.Vector2> vertices = zone.Polygon;
            if (vertices.Length < 3) continue;

            Color zoneColor = zone.Kind switch
            {
                VegetationKind.DenseForest => new Color(0.2f, 0.85f, 0.3f, 0.75f),
                VegetationKind.SparseForest => new Color(0.95f, 0.8f, 0.2f, 0.75f),
                _ => new Color(0.2f, 0.8f, 0.95f, 0.75f)
            };

            for (int i = 0; i < vertices.Length; i++)
            {
                System.Numerics.Vector2 p1 = vertices[i];
                System.Numerics.Vector2 p2 = vertices[(i + 1) % vertices.Length];

                float y1 = _map.GetHeight(p1.X, p1.Y);
                float y2 = _map.GetHeight(p2.X, p2.Y);
                float h = zone.CanopyHeight;

                Vector3 b1 = new(p1.X, y1 + 0.2f, p1.Y);
                Vector3 b2 = new(p2.X, y2 + 0.2f, p2.Y);
                Vector3 t1 = new(p1.X, y1 + h, p1.Y);
                Vector3 t2 = new(p2.X, y2 + h, p2.Y);

                // Ground edge
                _vegetationDebugMesh.SurfaceSetColor(zoneColor);
                _vegetationDebugMesh.SurfaceAddVertex(b1);
                _vegetationDebugMesh.SurfaceAddVertex(b2);

                // Canopy edge
                _vegetationDebugMesh.SurfaceSetColor(zoneColor);
                _vegetationDebugMesh.SurfaceAddVertex(t1);
                _vegetationDebugMesh.SurfaceAddVertex(t2);

                // Vertical corner edge
                _vegetationDebugMesh.SurfaceSetColor(zoneColor);
                _vegetationDebugMesh.SurfaceAddVertex(b1);
                _vegetationDebugMesh.SurfaceAddVertex(t1);
            }
        }

        // 2. Draw observation rays
        ReadOnlySpan<OwnUnitObservation> ownUnits = _session.Replica.OwnUnits;
        if (ownUnits.Length > 0)
        {
            OwnUnitObservation observer = ownUnits[0];
            for (int u = 0; u < ownUnits.Length; u++)
            {
                if (_unitViews.Selected.Contains(ownUnits[u].Id))
                {
                    observer = ownUnits[u];
                    break;
                }
            }

            float obsGround = _map.GetHeight(observer.Position.X, observer.Position.Y);
            System.Numerics.Vector3 eye = new(observer.Position.X, obsGround + 2.0f, observer.Position.Y);

            Span<System.Numerics.Vector2> targets = stackalloc System.Numerics.Vector2[5];
            int targetCount = 0;

            if (_scenario == "forest-visibility")
            {
                targets[0] = ForestVisibilityScenario.TargetEdgePos;
                targets[1] = ForestVisibilityScenario.TargetClearingPos;
                targets[2] = ForestVisibilityScenario.TargetBehindBeltsPos;
                targets[3] = ForestVisibilityScenario.TargetSparsePos;
                targets[4] = ForestVisibilityScenario.TargetOpenPos;
                targetCount = 5;
            }
            else if (_scenario == "infantry-playground")
            {
                targets[0] = InfantryPlaygroundScenario.OpforRiflemanClearing;
                targets[1] = InfantryPlaygroundScenario.OpforRiflemanBridge;
                targets[2] = InfantryPlaygroundScenario.OpforRiflemanBuilding;
                targets[3] = InfantryPlaygroundScenario.OpforVehiclePos;
                targetCount = 4;
            }

            for (int t = 0; t < targetCount; t++)
            {
                System.Numerics.Vector2 targetPos = targets[t];
                float tgtGround = _map.GetHeight(targetPos.X, targetPos.Y);
                System.Numerics.Vector3 targetEye = new(targetPos.X, tgtGround + 2.0f, targetPos.Y);

                LineOfSightResult los = _scenarioGeometry.CheckLineOfSight(eye, targetEye, _clientLosContext);

                Vector3 startGodot = new(eye.X, eye.Y, eye.Z);
                Vector3 endGodot = new(targetEye.X, targetEye.Y, targetEye.Z);

                if (los.IsClear)
                {
                    Color clearColor = new(0.2f, 0.95f, 0.3f, 0.9f);
                    _vegetationDebugMesh.SurfaceSetColor(clearColor);
                    _vegetationDebugMesh.SurfaceAddVertex(startGodot);
                    _vegetationDebugMesh.SurfaceAddVertex(endGodot);
                }
                else
                {
                    Vector3 hitGodot = new(los.HitPoint.X, los.HitPoint.Y, los.HitPoint.Z);

                    Color blockedColor = new(0.95f, 0.2f, 0.2f, 0.9f);
                    _vegetationDebugMesh.SurfaceSetColor(blockedColor);
                    _vegetationDebugMesh.SurfaceAddVertex(startGodot);
                    _vegetationDebugMesh.SurfaceAddVertex(hitGodot);

                    Color shadowColor = new(0.5f, 0.1f, 0.1f, 0.35f);
                    _vegetationDebugMesh.SurfaceSetColor(shadowColor);
                    _vegetationDebugMesh.SurfaceAddVertex(hitGodot);
                    _vegetationDebugMesh.SurfaceAddVertex(endGodot);

                    Color hitColor = los.ObstructionKind == ObstructionKind.Vegetation
                        ? new Color(0.95f, 0.85f, 0.2f, 0.95f)
                        : new Color(0.95f, 0.2f, 0.2f, 0.95f);

                    float mSize = 0.5f;
                    _vegetationDebugMesh.SurfaceSetColor(hitColor);
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X - mSize, hitGodot.Y, hitGodot.Z));
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X + mSize, hitGodot.Y, hitGodot.Z));

                    _vegetationDebugMesh.SurfaceSetColor(hitColor);
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y - mSize, hitGodot.Z));
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y + mSize, hitGodot.Z));

                    _vegetationDebugMesh.SurfaceSetColor(hitColor);
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y, hitGodot.Z - mSize));
                    _vegetationDebugMesh.SurfaceAddVertex(new Vector3(hitGodot.X, hitGodot.Y, hitGodot.Z + mSize));
                }
            }
        }

        _vegetationDebugMesh.SurfaceEnd();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what is (int)NotificationApplicationFocusOut or (int)NotificationWMWindowFocusOut)
        {
            CancelActiveDrag();
            _cameraRig?.ClearInput();
            _orderCursorOverlay?.HideTooltip();
            _destination?.HideGhost();
            Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
            if (_benchmarkMetrics is null)
            {
                Godot.Engine.MaxFps = 30;
            }
        }
        else if (what is (int)NotificationApplicationFocusIn or (int)NotificationWMWindowFocusIn)
        {
            if (_settingsPanel is not null)
            {
                SettingsPanel.ApplyToEngine(_settingsPanel.CurrentSettings);
            }
        }
        else if (what == (int)NotificationWMMouseEnter)
        {
            if ((_selectionController.IsPointerDown || _minimap?.IsDraggingCamera == true) && !Input.IsMouseButtonPressed(MouseButton.Left))
            {
                CancelActiveDrag();
            }
        }
    }

    private void OnSettingsButtonPressed()
    {
        CancelActiveDrag();
        _settingsPanel.Toggle();
    }

    internal void SetDebugHudVisible(bool visible)
    {
        _debugHudVisible = visible;
        _subtitle.Visible = visible;
        _stats.Visible = visible;
        _topHud.ResetSize();
        RefreshOccludingPanels();
    }

    internal void ToggleRangeLimits()
    {
        _showRangeRings = !_showRangeRings;
        _unitViews.SetRangeIndicatorsVisible(_showRangeRings);
        _commandPanel?.SetRangesVisible(_showRangeRings);
    }

    internal void OpenControlsHelp()
    {
        CancelActiveDrag();
        _cameraRig.ClearInput();
        _helpPanel.Open();
    }

    private void BuildHud()
    {
        CanvasLayer canvas = new();
        AddChild(canvas);
        _hudCanvas = canvas;

        _roleBadgeOverlay = new RoleBadgeOverlay(_catalog);
        canvas.AddChild(_roleBadgeOverlay);
        _garrisonBadgeRoot = new Control
        {
            Name = "GarrisonBadgeRoot",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _garrisonBadgeRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        canvas.AddChild(_garrisonBadgeRoot);
        _buildingGarrisonView?.AttachBadgeOverlay(_garrisonBadgeRoot);

        _selectionBoxOverlay = new SelectionBoxOverlay();
        canvas.AddChild(_selectionBoxOverlay);

        PanelContainer top = new()
        {
            Theme = HudTheme.Shared,
            Position = new Vector2(HudLayout.Margin, HudLayout.Margin),
            CustomMinimumSize = new Vector2(HudLayout.CardWidth, HudLayout.ToolbarHeight),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        top.AddThemeStyleboxOverride("panel", PanelStyle());
        MarginContainer margin = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 3);
        margin.AddThemeConstantOverride("margin_bottom", 3);
        VBoxContainer stack = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        stack.AddThemeConstantOverride("separation", 9);
        _title = new Label
        {
            Text = FormatTitle(_session.AssignedPlayerId, _scenario),
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        HudTheme.ApplyMono(_title);
        _title.AddThemeFontSizeOverride("font_size", 12);
        _title.AddThemeColorOverride("font_color", HudTheme.Muted);
        stack.AddChild(_title);
        _subtitle = new Label
        {
            Text = _session.StatusDescription,
            CustomMinimumSize = new Vector2(0, 20),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _subtitle.AddThemeColorOverride("font_color", HudTheme.Muted);
        stack.AddChild(_subtitle);
        _stats = new Label
        {
            Text = "Starting session...",
            CustomMinimumSize = new Vector2(0, 44),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _stats.AddThemeFontSizeOverride("font_size", 14);
        stack.AddChild(_stats);
        margin.AddChild(stack); top.AddChild(margin); canvas.AddChild(top);
        _topHud = top;

        PanelContainer utility = new() { Theme = HudTheme.Shared, MouseFilter = Control.MouseFilterEnum.Stop };
        canvas.AddChild(utility);
        utility.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        utility.OffsetLeft = -HudLayout.Margin - HudLayout.ToolbarWidth;
        utility.OffsetRight = -HudLayout.Margin;
        utility.OffsetTop = HudLayout.Margin;
        utility.OffsetBottom = HudLayout.Margin + HudLayout.ToolbarHeight;
        utility.AddThemeStyleboxOverride("panel", PanelStyle());
        HBoxContainer toolbar = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        toolbar.AddThemeConstantOverride("separation", 6);
        Button menuBtn = new()
        {
            Text = "MENU",
            ThemeTypeVariation = "CompactButton",
            TooltipText = "Battle menu [Esc]",
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        menuBtn.Pressed += OnMenuButtonPressed;
        Button settingsBtn = new()
        {
            Text = "SETTINGS",
            ThemeTypeVariation = "CompactButton",
            TooltipText = "Display and environment settings [F10]",
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        settingsBtn.Pressed += OnSettingsButtonPressed;
        Button helpBtn = new()
        {
            Text = "HELP",
            ThemeTypeVariation = "CompactButton",
            TooltipText = "Controls and range legend [F1]",
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        helpBtn.Pressed += OpenControlsHelp;
        toolbar.AddChild(menuBtn);
        toolbar.AddChild(settingsBtn);
        toolbar.AddChild(helpBtn);
        utility.AddChild(toolbar);
        _utilityHud = utility;
        _helpPanel = new ControlsHelpPanel();
        AddChild(_helpPanel);

        // Retain command status for diagnostics and test observations, without a permanent footer.
        _status = new Label { Text = "Select a unit to issue orders", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        stack.AddChild(_status);
        SetDebugHudVisible(_debugHudVisible);

        _selectionCard = new SelectionCard(_catalog);
        _selectionCard.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        _selectionCard.OffsetLeft = 24;
        _selectionCard.OffsetBottom = -HudLayout.Margin;
        _selectionCard.OffsetTop = -HudLayout.Margin - HudLayout.CardHeight;
        _selectionCard.OffsetRight = HudLayout.Margin + HudLayout.CardWidth;
        _selectionCard.RosterUnitClicked += OnRosterUnitClicked;
        canvas.AddChild(_selectionCard);

        _contextActionPanel = new ContextActionPanel(_catalog);
        _contextActionPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        _contextActionPanel.OffsetLeft = 24;
        _contextActionPanel.OffsetBottom = -HudLayout.Margin - HudLayout.CardHeight - HudLayout.Gap;
        _contextActionPanel.OffsetTop = _contextActionPanel.OffsetBottom - HudLayout.ContextHeight;
        _contextActionPanel.OffsetRight = HudLayout.Margin + HudLayout.CardWidth;
        _contextActionPanel.DisembarkHereRequested += OnDisembarkHereRequested;
        _contextActionPanel.TargetingModeChanged += OnContextTargetingChanged;
        _contextActionPanel.DeploySmokeRequested += OnDeploySmokeRequested;
        _contextActionPanel.HelicopterToggleFlightRequested += OnHelicopterToggleFlightRequested;
        _contextActionPanel.HelicopterToggleAltitudeRequested += OnHelicopterToggleAltitudeRequested;
        _contextActionPanel.CancelFireMissionRequested += OnCancelFireMissionRequested;
        _contextActionPanel.DeployFlaresRequested += OnDeployFlaresRequested;
        _contextActionPanel.ToggleAutoFlaresRequested += OnToggleAutoFlaresRequested;
        _contextActionPanel.ExitBuildingRequested += OnExitBuildingRequested;
        _contextActionPanel.ToggleGarrisonRoofRequested += OnToggleGarrisonRoofRequested;
        canvas.AddChild(_contextActionPanel);

        _commandPanel = new UnitCommandPanel(_catalog);
        canvas.AddChild(_commandPanel);
        _commandPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        _commandPanel.OffsetLeft = HudLayout.Margin + HudLayout.CardWidth + HudLayout.Gap;
        _commandPanel.OffsetRight = _commandPanel.OffsetLeft + HudLayout.CommandWidth;
        _commandPanel.OffsetTop = -HudLayout.Margin - HudLayout.CommandHeight;
        _commandPanel.OffsetBottom = -HudLayout.Margin;
        _commandPanel.TargetRequested += BeginHudTargeting;
        _commandPanel.StanceRequested += OnHudStanceRequested;
        _commandPanel.StopRequested += OnStopRequested;
        _commandPanel.RangeToggleRequested += ToggleRangeLimits;
        _commandPanel.SetRangesVisible(_showRangeRings);

        _orderNotification = new OrderNotification();
        _orderNotification.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _orderNotification.OffsetBottom = -HudLayout.Margin - HudLayout.CardHeight - HudLayout.Gap;
        _orderNotification.OffsetTop = _orderNotification.OffsetBottom - HudLayout.NotificationHeight;
        _orderNotification.OffsetLeft = -180;
        _orderNotification.OffsetRight = 180;
        canvas.AddChild(_orderNotification);

        _minimap = new TacticalMinimap();
        _minimap.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
        _minimap.OffsetRight = -24;
        _minimap.OffsetLeft = -24 - TacticalMinimap.DefaultWidgetSize;
        _minimap.OffsetBottom = -HudLayout.Margin;
        _minimap.OffsetTop = -HudLayout.Margin - TacticalMinimap.DefaultWidgetSize;
        canvas.AddChild(_minimap);

        _orderCursorOverlay = new OrderCursorOverlay();
        canvas.AddChild(_orderCursorOverlay);

        UstyePlanData? plan = (_scenario == "ustye-ground" || _scenario == "ustye-raid") ? UstyePlanData.Load() : null;
        _minimap.Initialize(_scenarioGeometry!, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, plan);

        if (_scenario == "recon-raid" || _scenario == "ustye-raid" || _scenario == "battle")
        {
            ConfigureMissionPanel(_scenario);
        }
        _unitViews.SetRangeIndicatorsVisible(_showRangeRings);
        UpdateSelectionPresentation();
    }

    internal MissionPanel ConfigureMissionPanel(string scenario)
    {
        if (_missionPanel is null)
        {
            _missionPanel = new MissionPanel(this, _map);
            _hudCanvas.AddChild(_missionPanel);
            _missionPanel.RetryRequested += OnRetryMission;
            _missionPanel.LeaveRequested += LeaveCurrentMatch;
        }

        _missionPanel.SetScenario(scenario);
        if (scenario == "ustye-raid")
        {
            _missionPanel.SetBriefing(
                "OPERATION: USTYE RAID",
                "Primary: Destroy enemy command vehicle. Preserve recon vehicle.",
                "Roster: 1x BRDM-2 (3000m sensor), 2x T-62 (115mm gun)");
        }
        else if (scenario == "battle")
        {
            _missionPanel.SetShowBriefing(false);
        }

        _missionPanel.SetLocalMode(_session is LocalSession);
        return _missionPanel;
    }

    internal void ClearMissionPanel()
    {
        if (_missionPanel is null) return;
        _hudCanvas.RemoveChild(_missionPanel);
        _missionPanel.Cleanup();
        _missionPanel.QueueFree();
        _missionPanel = null;
    }

    private void OnRetryMission()
    {
        ResetLocalScenario();
        if (_scenario == "battle")
        {
            _status.Text = "Battle restarted.";
            _orderNotification?.ShowNotification("Battle restarted.");
        }
        else
        {
            _status.Text = "Mission restarted. Objective: Destroy enemy command vehicle. Preserve recon vehicle.";
            _orderNotification?.ShowNotification("Mission restarted. Destroy enemy command vehicle.");
        }
    }

    internal void ResetLocalScenario()
    {
        if (_session is not LocalSession || _scenarioGeometry is null) return;

        _helpPanel?.Close();
        CancelActiveDrag();
        _selectionController.Reset();
        _unitViews.Clear();
        _contactViews.Clear();
        _wreckViews.Clear();
        _roleBadgeOverlay?.Clear();
        _selectionCard?.UpdateEmpty();
        _orderNotification?.Clear();
        _destination.Clear();
        _previewController?.Reset();
        _combatEffects.Clear();
        _smokeView?.Clear();
        _session.Dispose();

        _presentedKind = SelectionPresentationKind.None;
        _presentedActiveGroup = -1;
        _presentedSingleUnitId = default;
        _presentedMultiCount = -1;
        _presentedMultiHash = 0;
        _presentedContactId = default;

        Simulation newSim = _scenario switch
        {
            "recon-raid" => ReconRaidScenario.CreateSimulation(_scenarioGeometry),
            "bridge-crossing" => BridgeCrossingScenario.CreateSimulation(_scenarioGeometry),
            "vehicle-traffic" => VehicleTrafficScenario.CreateSimulation(_scenarioGeometry),
            "group-move" => GroupMoveScenario.CreateSimulation(_scenarioGeometry),
            "ustye-ground" => UstyeGroundScenario.CreateSimulation(_scenarioGeometry, setupIndex: _ustyeSetupIndex),
            "ustye-raid" => UstyeRaidScenario.CreateSimulation(_scenarioGeometry),
            "battle" => BattleScenario.CreateSimulation(_scenarioGeometry),
            "combat" => CombatScenario.CreateSimulation(_scenarioGeometry),
            "visibility" => VisibilityScenario.CreateSimulation(_scenarioGeometry),
            "navigation" => NavigationScenario.CreateSimulation(_scenarioGeometry),
            "navigation-large" => LargeNavigationScenario.CreateSimulation(_scenarioGeometry),
            _ => PrototypeScenario.Create(_map)
        };
        _session = new LocalSession(newSim);
        _lastCommandResult = 0;

        _cameraRig.ResetToDefault();
        _missionPanel?.ResetUi();
        UstyePlanData? plan = (_scenario == "ustye-ground" || _scenario == "ustye-raid") ? UstyePlanData.Load() : null;
        _minimap?.Initialize(_scenarioGeometry, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, plan);
        _minimap?.Reset();
        _unitViews.UpdateLod(_cameraRig.Size);
        _contactViews.UpdateLod(_cameraRig.Size);
        _wreckViews.UpdateLod(_cameraRig.Size);
        _buildingDestructionView?.UpdateLod(_cameraRig.Size);
        UpdateViews();

        if (_session.Replica.OwnUnits.Length > 0)
        {
            _fogOfWar?.UpdateAllNow(_session.Replica, _catalog);
            if (_scenario == "group-move" && _session.Replica.OwnUnits.Length >= 10)
            {
                for (int i = 0; i < 10; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-ground" && _session.Replica.OwnUnits.Length >= 6)
            {
                for (int i = 0; i < 6; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else if (_scenario == "ustye-raid" && _session.Replica.OwnUnits.Length >= 3)
            {
                for (int i = 0; i < 3; i++)
                {
                    _unitViews.Selected.Add(_session.Replica.OwnUnits[i].Id);
                }
            }
            else
            {
                int selectIdx = _scenario == "vehicle-traffic" && _session.Replica.OwnUnits.Length > 1 ? 1 : 0;
                _unitViews.Selected.Add(_session.Replica.OwnUnits[selectIdx].Id);
            }
        }
    }

    internal void ResetGameplayState()
    {
        CancelActiveDrag();
        _previewController?.CancelFacingGesture();
        _previewController?.Reset();
        _selectionController.Reset();
        _unitViews?.Clear();
        _contactViews?.Clear();
        _wreckViews?.Clear();
        _roleBadgeOverlay?.Clear();
        _selectionCard?.UpdateEmpty();
        _orderNotification?.Clear();
        _destination?.Clear();
        _combatEffects?.Clear();
        _smokeView?.Clear();
        _fogOfWar?.Clear();
        _minimap?.Reset();
        _missionPanel?.ResetUi();

        _presentedKind = SelectionPresentationKind.None;
        _presentedActiveGroup = -1;
        _presentedSingleUnitId = default;
        _presentedMultiCount = -1;
        _presentedMultiHash = 0;
        _presentedContactId = default;
        _homeViewAssignedPlayerId = 0;
    }

    private void OnMenuButtonPressed()
    {
        CancelActiveDrag();
        _pauseMenu.Toggle();
    }

    internal void EnsureBattleScenarioContext()
    {
        if (_scenario == "battle" && _scenarioGeometry is not null && _map == _scenarioGeometry.HeightMap && MathF.Abs(_map.Width - BattleScenario.MapSize) < 0.1f)
        {
            return;
        }

        _helicopterAudio?.Stop();
        ResetGameplayState();

        _scenario = "battle";
        _scenarioGeometry = BattleScenario.CreateStaticGeometry();
        _map = _scenarioGeometry.HeightMap;
        _matchSetup = BattleScenario.CreateMatchSetup(_scenarioGeometry);
        _catalog = ScenarioContent.LoadCatalog("battle");
        RebindHudCatalog();

        _fogOfWar?.Dispose();
        _fogOfWar = new FogOfWarView(_map, _scenarioGeometry);
        BuildEnvironment(_scenarioGeometry);

        Vector3 initialFocus = new(BattleScenario.SovietStart.X, 0, BattleScenario.SovietStart.Y);
        float initialCameraSize = 160f;
        _cameraRig = new CameraRig(_camera, initialFocus, initialCameraSize, _map.Width, _map.Depth);
        _cameraRig.SetDefaultView(initialFocus, initialCameraSize);
        _environmentView?.UpdateShadowTierForCamera(_cameraRig.Size, _activeShadowMapLayers);
        UpdateTerrainLodStep(_terrainView, _camera, _cachedTerrainViewportHeightPx);
        _cameraRig.ResetToDefault();

        _unitViews?.Clear();
        _unitViews = new UnitViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _unitViews.UpdateLod(_cameraRig.Size);

        _contactViews?.Clear();
        _contactViews = new ContactViewSet(this, _map, _catalog, () => _matchSetup.GetHostileFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _contactViews.UpdateLod(_cameraRig.Size);

        _wreckViews?.Clear();
        _wreckViews = new WreckViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _wreckViews.UpdateLod(_cameraRig.Size);

        _combatEffects?.Clear();
        _combatEffects?.QueueFree();
        _combatEffects = new CombatEffects(_map, _scenarioGeometry);
        _combatEffects.OnBlastImpulse = _cameraRig.AddBlastShake;
        AddChild(_combatEffects);

        _smokeView?.Clear();
        _smokeView?.QueueFree();
        _smokeView = new SmokeView(_map, _scenarioGeometry);
        if (_environmentView?.ActiveProfile is not null)
        {
            _smokeView.ApplyEnvironment(_environmentView.ActiveProfile);
        }
        AddChild(_smokeView);

        _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);
        _previewController = new OrderPreviewController(
            _destinationEvaluator,
            _scenarioGeometry,
            _map,
            _catalog,
            _orderCursorOverlay,
            _destination,
            _orderNotification);
        _previewController.SetLineOfSightContext(_clientLosContext);

        if (_missionPanel is not null)
        {
            _missionPanel.SetScenario("battle");
            _missionPanel.ResetUi();
        }

        if (_minimap is not null)
        {
            _minimap.Reset();
            _minimap.Initialize(_scenarioGeometry, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, null);
        }
    }

    internal void SetScenarioForTest(string scenarioName)
    {
        _helicopterAudio?.Stop();
        ResetGameplayState();
        _scenario = scenarioName;
        StaticGeometry? scenarioGeometry = null;
        MatchSetup setup;

        if (scenarioName == "prototype")
        {
            _map = HeightMap.CreatePrototype();
            setup = PrototypeScenario.CreateMatchSetup(_map);
            scenarioGeometry = new StaticGeometry(_map, ReadOnlySpan<OpaqueStructure>.Empty);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = PrototypeScenario.LoadCatalog();
            Simulation localSimulation = PrototypeScenario.Create(_map);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "recon-raid")
        {
            scenarioGeometry = ReconRaidScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = ReconRaidScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("recon-raid");
            Simulation localSimulation = ReconRaidScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "battle")
        {
            scenarioGeometry = BattleScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = BattleScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("battle");
            _contextActionPanel?.SetCatalog(_catalog);
            Simulation localSimulation = BattleScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "ustye-raid")
        {
            scenarioGeometry = UstyeRaidScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = UstyeRaidScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("ustye-raid");
            Simulation localSimulation = UstyeRaidScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "forest-visibility")
        {
            scenarioGeometry = ForestVisibilityScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = ForestVisibilityScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("forest-visibility");
            Simulation localSimulation = ForestVisibilityScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "infantry-playground")
        {
            scenarioGeometry = InfantryPlaygroundScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = InfantryPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("infantry-playground");
            Simulation localSimulation = InfantryPlaygroundScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "infantry-transport")
        {
            scenarioGeometry = InfantryTransportScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = InfantryTransportScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("infantry-transport");
            Simulation localSimulation = InfantryTransportScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "helicopter-playground")
        {
            scenarioGeometry = HelicopterPlaygroundScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = HelicopterPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("helicopter-playground");
            Simulation localSimulation = HelicopterPlaygroundScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "artillery-playground")
        {
            scenarioGeometry = ArtilleryPlaygroundScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = ArtilleryPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("artillery-playground");
            Simulation localSimulation = ArtilleryPlaygroundScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "air-defense-playground")
        {
            scenarioGeometry = AirDefensePlaygroundScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = AirDefensePlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("air-defense-playground");
            Simulation localSimulation = AirDefensePlaygroundScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "urban-destruction-playground")
        {
            scenarioGeometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = UrbanDestructionPlaygroundScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("urban-destruction-playground");
            Simulation localSimulation = UrbanDestructionPlaygroundScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else if (scenarioName == "ustye-ground")
        {
            scenarioGeometry = UstyeGroundScenario.CreateStaticGeometry();
            _map = scenarioGeometry.HeightMap;
            setup = UstyeGroundScenario.CreateMatchSetup(scenarioGeometry);
            _matchSetup = setup;
            _scenarioGeometry = scenarioGeometry;
            _catalog = ScenarioContent.LoadCatalog("ustye-ground");
            Simulation localSimulation = UstyeGroundScenario.CreateSimulation(scenarioGeometry);
            _session = new LocalSession(localSimulation, player: 1);
        }
        else
        {
            throw new ArgumentException($"Unsupported test scenario: {scenarioName}");
        }

        RebindHudCatalog();
        _fogOfWar?.Dispose();
        _fogOfWar = new FogOfWarView(_map, scenarioGeometry);
        _contextActionPanel?.SetCatalog(_catalog);
        BuildEnvironment(scenarioGeometry);

        Vector3 initialFocus = scenarioName switch
        {
            "battle" => new Vector3(BattleScenario.SovietStart.X, 0, BattleScenario.SovietStart.Y),
            "recon-raid" => new Vector3(100, 0, 100),
            "ustye-ground" => new Vector3(2550f, 4.5f, 2450f),
            "ustye-raid" => new Vector3(2160f, 10f, 2325f),
            "forest-visibility" => new Vector3(100f, 0f, 110f),
            "infantry-playground" => new Vector3(130f, 0f, 180f),
            "infantry-transport" => new Vector3(128f, 0f, 195f),
            "helicopter-playground" => new Vector3(HelicopterPlaygroundScenario.ChinookAlphaPos.X + 20f, 2f, HelicopterPlaygroundScenario.ChinookAlphaPos.Y),
            "artillery-playground" => new Vector3(1024f, 4f, ArtilleryPlaygroundScenario.GradAlphaPos.Y),
            "air-defense-playground" => new Vector3(1024f, 12f, 920f),
            "urban-destruction-playground" => new Vector3(500f, 4f, 470f),
            _ => new Vector3(64, 0, 64)
        };
        float initialCameraSize = scenarioName switch
        {
            "battle" => 160f,
            "recon-raid" => 160f,
            "ustye-ground" => 220f,
            "ustye-raid" => 160f,
            "forest-visibility" => 150f,
            "infantry-playground" => 120f,
            "infantry-transport" => 120f,
            "helicopter-playground" => 180f,
            "artillery-playground" => 220f,
            "air-defense-playground" => 440f,
            "urban-destruction-playground" => 155f,
            _ => 112f
        };
        _cameraRig = new CameraRig(_camera, initialFocus, initialCameraSize, _map.Width, _map.Depth);
        _cameraRig.SetDefaultView(initialFocus, initialCameraSize);
        _environmentView?.UpdateShadowTierForCamera(_cameraRig.Size, _activeShadowMapLayers);
        UpdateTerrainLodStep(_terrainView, _camera, _cachedTerrainViewportHeightPx);
        _cameraRig.ResetToDefault();

        _unitViews?.Clear();
        _unitViews = new UnitViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _unitViews.UpdateLod(_cameraRig.Size);

        _contactViews?.Clear();
        _contactViews = new ContactViewSet(this, _map, _catalog, () => _matchSetup.GetHostileFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _contactViews.UpdateLod(_cameraRig.Size);

        _wreckViews?.Clear();
        _wreckViews = new WreckViewSet(this, _map, _catalog, () => _matchSetup.GetPlayerFaction(_session.AssignedPlayerId), _scenarioGeometry);
        _wreckViews.UpdateLod(_cameraRig.Size);

        _combatEffects?.Clear();
        _combatEffects?.QueueFree();
        _combatEffects = new CombatEffects(_map, _scenarioGeometry);
        _combatEffects.OnBlastImpulse = _cameraRig.AddBlastShake;
        AddChild(_combatEffects);

        _smokeView?.Clear();
        _smokeView?.QueueFree();
        _smokeView = new SmokeView(_map, _scenarioGeometry);
        if (_environmentView?.ActiveProfile is not null)
        {
            _smokeView.ApplyEnvironment(_environmentView.ActiveProfile);
        }
        AddChild(_smokeView);

        _destinationEvaluator = new DestinationEvaluator(_scenarioGeometry);
        _previewController = new OrderPreviewController(
            _destinationEvaluator,
            _scenarioGeometry,
            _map,
            _catalog,
            _orderCursorOverlay,
            _destination,
            _orderNotification);
        _previewController.SetLineOfSightContext(_clientLosContext);

        EnterGameSession(_session);

        if (scenarioName == "prototype")
        {
            foreach (OwnUnitObservation unit in _session.Replica.OwnUnits)
            {
                _session.Move(unit.Id, unit.Position + new NumericsVector2(0, 60));
            }
            if (_session.Replica.OwnUnits.Length > 0) _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
        }
    }

    private void RebindHudCatalog()
    {
        if (_selectionCard is null || _contextActionPanel is null || _commandPanel is null || _roleBadgeOverlay is null) return;
        _selectionCard.SetCatalog(_catalog);
        _contextActionPanel.SetCatalog(_catalog);
        _commandPanel.SetCatalog(_catalog);
        _roleBadgeOverlay.SetCatalog(_catalog);
        _selectionCard.Visible = false;
        _commandPanel.Visible = false;
        _presentedKind = SelectionPresentationKind.None;
        _lastSelectionCardHeight = -1;
    }

    internal void StartLocalBattle()
    {
        EnsureBattleScenarioContext();
        Simulation localSimulation = BattleScenario.CreateSimulation(_scenarioGeometry!);
        _session = new LocalSession(localSimulation, player: 1);
        EnterGameSession(_session);
    }

    internal void StartMultiplayerConnect(string address)
    {
        EnsureBattleScenarioContext();
        bool ok = _lifecycleController.TryConnect(address, _matchSetup, out string? error);
        if (!ok)
        {
            _sessionMenu.SetStatus(error ?? "Connection failed.", ClientLifecycleState.Failed);
        }
        else
        {
            _sessionMenu.SetStatus(_lifecycleController.StatusDescription, _lifecycleController.State);
        }
    }

    internal void CancelMultiplayerConnect()
    {
        _lifecycleController.Cancel();
        _sessionMenu.SetStatus("Connection cancelled.", ClientLifecycleState.Inactive);
    }

    internal void EnterGameSession(IGameSession session)
    {
        _session = session;
        _isMenuMode = false;
        _sessionMenu.HideMenu();
        _pauseMenu.Close();
        _helicopterAudio?.Stop();
        ResetGameplayState();

        _hudCanvas.Visible = true;
        _lastTitlePlayerId = -1;
        _lastTitleScenario = string.Empty;
        _title.Text = FormatTitle(_session.AssignedPlayerId, _scenario);
        _subtitle.Text = _session.StatusDescription;

        if (_missionPanel is not null)
        {
            _missionPanel.SetScenario(_scenario);
            _missionPanel.SetLocalMode(_session is LocalSession);
            _missionPanel.SetShowBriefing(false);
            _missionPanel.ResetUi();
        }

        if (_minimap is not null)
        {
            _minimap.Reset();
            _minimap.Initialize(_scenarioGeometry!, _cameraRig, _camera, _session, _unitViews, _contactViews, _catalog, null);
        }

        ApplyPlayerHomeView(_session.AssignedPlayerId);
    }

    internal void LeaveCurrentMatch()
    {
        _helpPanel?.Close();
        _pauseMenu.Close();
        _lifecycleController.Disconnect();
        if (_session is not null)
        {
            _session.Dispose();
            _session = new NullSession();
        }
        _helicopterAudio?.Stop();
        ResetGameplayState();
        _isMenuMode = true;
        _hudCanvas.Visible = false;
        _sessionMenu.ShowMainMenuView();
    }

    private void OnNetworkSessionDisconnected(string reason)
    {
        _helpPanel?.Close();
        _pauseMenu.Close();
        if (_session is not null)
        {
            _session.Dispose();
            _session = new NullSession();
        }
        _helicopterAudio?.Stop();
        ResetGameplayState();
        _isMenuMode = true;
        _hudCanvas.Visible = false;
        _sessionMenu.ShowConnectionLostModal(reason);
    }

    private void SwitchUstyeSetup(int index)
    {
        _ustyeSetupIndex = Math.Clamp(index, 0, 3);
        ResetLocalScenario();
        Vector3 newFocus = _ustyeSetupIndex switch
        {
            0 => new Vector3(2550f, 4.5f, 2450f),
            1 => new Vector3(2860f, 4.5f, 3550f),
            2 => new Vector3(2250f, 4.5f, 2260f),
            3 => new Vector3(2050f, 4.5f, 2160f),
            _ => new Vector3(2550f, 4.5f, 2450f)
        };
        float newSize = _ustyeSetupIndex switch
        {
            0 => 220f,
            1 => 200f,
            2 => 180f,
            3 => 160f,
            _ => 220f
        };
        _cameraRig.SetDefaultView(newFocus, newSize);
        _cameraRig.ResetToDefault();
        string name = _ustyeSetupIndex switch
        {
            0 => "Central Bridge (CROSS-C)",
            1 => "Southern Crossing (CROSS-S)",
            2 => "City Maneuver (Panel Blocks)",
            3 => "Reconnaissance & LOS Occlusion",
            _ => "Default"
        };
        _status.Text = $"Ustye setup {_ustyeSetupIndex + 1}/4: {name}";
        _orderNotification?.ShowNotification($"Setup {_ustyeSetupIndex + 1}: {name}");
    }

    private static StyleBoxFlat PanelStyle() => HudTheme.CreatePanel(padding: 4, topRail: true);

    public static string FormatTitle(int assignedSide, string scenario)
    {
        string name = scenario switch
        {
            "visibility" => "RECON SCENARIO",
            "combat" => "COMBAT SCENARIO",
            "recon-raid" => "RECON RAID MISSION",
            "battle" => "MEETING ENGAGEMENT",
            "bridge-crossing" => "BRIDGE CROSSING",
            "vehicle-traffic" => "VEHICLE TRAFFIC PLAYGROUND",
            "group-move" => "GROUP MOVE PLAYGROUND",
            "ustye-ground" => "USTYE PLAYGROUND",
            "ustye-raid" => "USTYE RECON RAID",
            "forest-visibility" => "FOREST VISIBILITY POLYGON",
            "infantry-playground" => "INFANTRY PLAYGROUND",
            "infantry-transport" => "INFANTRY TRANSPORT PROVING GROUND",
            "helicopter-playground" => "HELICOPTER FLIGHT PROVING GROUND",
            "artillery-playground" => "BM-21 GRAD ROCKET ARTILLERY PROVING GROUND",
            "air-defense-playground" => "9K35 STRELA-10 AIR DEFENSE & FLARES PROVING GROUND",
            "urban-destruction-playground" => "SOVIET PANELKA 1-464 URBAN DESTRUCTION PROVING GROUND",
            _ => "M0 NETWORK TEST"
        };
        return $"DEAD GROUND   /   SIDE {assignedSide}   /   {name}";
    }

    internal void ApplyPlayerHomeView(int assignedPlayerId)
    {
        Vector3 homeFocus = GetDefaultHomeFocus(_scenario, assignedPlayerId, _initialSpawnFocus, _hasExplicitCameraFocus);
        _cameraRig.SetDefaultView(homeFocus, _initialSpawnSize);

        bool shouldCenter = ShouldCenterOnHomeView(
            _cameraRig.Focus, _cameraRig.Size, _cameraRig.TargetSize, _cameraRig.ViewYaw, _cameraRig.ViewPitch, _initialSpawnFocus, _initialSpawnSize);
        if (shouldCenter)
        {
            _cameraRig.CenterOn(homeFocus);
            _initialSpawnFocus = homeFocus;
        }

        GD.Print($"CAMERA_HOME side={assignedPlayerId} focus={_cameraRig.Focus} default_focus={homeFocus} centered={shouldCenter}");
    }

    internal static bool ShouldCenterOnHomeView(Vector3 currentFocus, float currentSize, float targetSize, float viewYaw, float viewPitch, Vector3 spawnFocus, float spawnSize)
    {
        Vector3 focusDelta = currentFocus - spawnFocus;
        bool cameraMoved = (focusDelta.X * focusDelta.X + focusDelta.Z * focusDelta.Z) > 1.0f
            || MathF.Abs(currentSize - spawnSize) > 0.5f
            || MathF.Abs(targetSize - spawnSize) > 0.5f
            || MathF.Abs(viewYaw) > 0.01f
            || MathF.Abs(viewPitch - OrbitCameraPose.DefaultPitch) > 0.01f;
        return !cameraMoved;
    }

    internal static Vector3 GetDefaultHomeFocus(string scenario, int assignedPlayerId, Vector3 fallbackFocus, bool hasExplicitFocus)
    {
        if (hasExplicitFocus)
        {
            return fallbackFocus;
        }

        if (scenario == "battle")
        {
            return assignedPlayerId switch
            {
                1 => new Vector3(BattleScenario.SovietStart.X, 0f, BattleScenario.SovietStart.Y),
                2 => new Vector3(BattleScenario.UsStart.X, 0f, BattleScenario.UsStart.Y),
                _ => fallbackFocus
            };
        }

        if (scenario == "forest-visibility")
        {
            return new Vector3(100f, 0f, 110f);
        }

        if (scenario == "infantry-playground")
        {
            return new Vector3(130f, 0f, 180f);
        }

        if (scenario == "infantry-transport")
        {
            return new Vector3(128f, 0f, 195f);
        }

        if (scenario == "helicopter-playground")
        {
            return new Vector3(HelicopterPlaygroundScenario.ChinookAlphaPos.X + 20f, 2f, HelicopterPlaygroundScenario.ChinookAlphaPos.Y);
        }

        if (scenario == "artillery-playground")
        {
            return new Vector3(1024f, 4f, ArtilleryPlaygroundScenario.GradAlphaPos.Y);
        }

        if (scenario == "air-defense-playground")
        {
            return new Vector3(1024f, 12f, 920f);
        }

        if (scenario == "urban-destruction-playground")
        {
            return new Vector3(500f, 4f, 470f);
        }

        return fallbackFocus;
    }

    internal static string FormatControlsHelpText(bool isMacOs) =>
        isMacOs
            ? "  LMB click/box  /  2xLMB type  /  Cmd/Ctrl+1-9 set  /  Shift+1-9 add  /  1-9 select (twice: center)  /  Z/X/C stance  /  RMB order  /  WASD pan"
            : "  LMB click/box  /  2xLMB type  /  Ctrl/Cmd+1-9 set  /  Shift+1-9 add  /  1-9 select (twice: center)  /  Z/X/C stance  /  RMB order  /  WASD pan";

    private void SetupMinimapDemo()
    {
        ReadOnlySpan<OwnUnitObservation> ownUnits = _session.Replica.OwnUnits;
        Span<ContactObservation> demoContacts = stackalloc ContactObservation[2]
        {
            new(new ContactId(1), ContactState.LastKnown, ContactClassification.Tank, 0, new NumericsVector2(3100f, 2550f), LastSeenTick: 10),
            new(new ContactId(2), ContactState.Current, ContactClassification.Recon, 1, new NumericsVector2(2860f, 2220f), LastSeenTick: 30)
        };

        byte[] buf = new byte[8192];
        int len = WireProtocol.WriteSnapshot(
            buf,
            sequence: 30,
            tick: 30,
            units: ownUnits,
            contacts: demoContacts,
            events: [],
            mission: default);

        _session.Replica.TryApply(buf.AsSpan(0, len));

        if (ownUnits.Length > 0)
        {
            for (int i = 0; i < Math.Min(3, ownUnits.Length); i++)
            {
                _unitViews.Selected.Add(ownUnits[i].Id);
            }
        }

        _cameraRig.Model.CurrentSize = 520f;
        _cameraRig.Model.SetZoom(520f);
        _cameraRig.CenterOn(new Vector3(2650f, 4.5f, 2450f));
        _unitViews.UpdateLod(_cameraRig.Size);
        _contactViews.UpdateLod(_cameraRig.Size);
        _wreckViews.UpdateLod(_cameraRig.Size);
        _buildingDestructionView?.UpdateLod(_cameraRig.Size);
        UpdateViews();
        _minimap?.UpdateMinimap(0.016);
    }

    public string TitleText => _title?.Text ?? string.Empty;

    private bool AdvanceBenchmarkWarmup(float deltaSec)
    {
        if (_benchmarkWarmupRemainingSec > 0f)
        {
            _benchmarkWarmupRemainingSec = MathF.Max(0f, _benchmarkWarmupRemainingSec - deltaSec);
        }
        if (_benchmarkWarmupFrames > 0)
        {
            _benchmarkWarmupFrames--;
        }
        if (_benchmarkWarmupFrames > 0 || _benchmarkWarmupRemainingSec > 0f)
        {
            return false;
        }

        _benchmarkWarmupCompleted = true;
        if (_benchmarkZoomToSize.HasValue)
        {
            float targetZoom = _benchmarkZoomToSize.Value;
            _benchmarkZoomToSize = null;
            _cameraRig.Model.SetSizeLimits(MathF.Min(30f, targetZoom), MathF.Max(5500f, targetZoom));
            _cameraRig.Model.CurrentSize = targetZoom;
            _cameraRig.Model.SetZoom(targetZoom);
        }
        return true;
    }

    private bool IsBenchmarkSampleLimitReached() =>
        !_isTimedBenchmark && _benchmarkMetrics is not null && _benchmarkMetrics.TotalCount >= _benchmarkTargetFrames;

    private bool IsBenchmarkFinished() =>
        _isTimedBenchmark
            ? _benchmarkElapsedSec >= _benchmarkOverrides.BenchmarkSeconds - 1e-6
            : _benchmarkMetrics is not null && _benchmarkMetrics.TotalCount >= _benchmarkTargetFrames;

    internal void ConfigureBenchmarkLifecycleForTest(int warmupFrames, float warmupSeconds, float? zoomToSize, float benchmarkSeconds, int targetFrames, int initialCapacity)
    {
        _benchmarkWarmupFrames = warmupFrames;
        _benchmarkWarmupRemainingSec = warmupSeconds;
        _benchmarkZoomToSize = zoomToSize;
        _benchmarkOverrides = _benchmarkOverrides with { WarmupSeconds = warmupSeconds, BenchmarkSeconds = benchmarkSeconds };
        _isTimedBenchmark = benchmarkSeconds > 0f;
        _benchmarkTargetFrames = _isTimedBenchmark ? 0 : targetFrames;
        _benchmarkElapsedSec = 0f;
        _benchmarkWarmupCompleted = false;
        _benchmarkActive = true;
        _benchmarkQuit = false;
        _benchmarkMetrics = new FramePacingMetrics(initialCapacity);
    }

    internal void StepBenchmarkLifecycleForTest(float deltaSec, float wallClockMs)
    {
        if (!_benchmarkActive || _benchmarkMetrics is null) return;
        if (!_benchmarkWarmupCompleted)
        {
            AdvanceBenchmarkWarmup(deltaSec);
            return;
        }
        if (!IsBenchmarkSampleLimitReached())
        {
            _benchmarkElapsedSec += wallClockMs * 0.001;
            _benchmarkMetrics.Record(wallClockMs, 0.2f, -1f);
            if (IsBenchmarkFinished())
            {
                _benchmarkActive = false;
            }
        }
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(SettingsPanel.ActiveBenchmarkOverrides, _benchmarkOverrides))
        {
            SettingsPanel.ActiveBenchmarkOverrides = null;
        }
        _combatEffects?.Clear();
        _smokeView?.Clear();
        _fogOfWar?.Dispose();
        _missionPanel?.Cleanup();
        _session?.Dispose();
    }
}
