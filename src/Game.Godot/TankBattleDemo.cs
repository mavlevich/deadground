using DeadGround.Engine;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

// Presentation for a local observer. The rules assembly owns all combat and bot decisions.
public partial class TankBattleDemo : Node3D
{
    private TankBattleDemoSession _session = null!;
    private StaticGeometry _geometry = null!;
    private UnitCatalog _catalog = null!;
    private UnitViewSet _sovietViews = null!;
    private UnitViewSet _usViews = null!;
    private CombatEffects _effects = null!;
    private FogOfWarView? _sovietFog;
    private FogOfWarView? _usFog;
    private ShaderMaterial _groundMaterial = null!;
    private int _fogMode;
    private bool _smokeFogPassed;
    private CameraRig _camera = null!;
    private Label _summary = null!;
    private Label _help = null!;
    private DirectionalLight3D _sun = null!;
    private Godot.Environment _environment = null!;
    private readonly OmniLight3D[] _shotLights = new OmniLight3D[8];
    private readonly float[] _lightRemaining = new float[8];
    private readonly uint[] _litEvents = new uint[512];
    private int _nextLight;
    private readonly CombatEventObservation[] _events = new CombatEventObservation[256];
    private bool _paused;
    private bool _fast;
    private bool _smoke;
    private int _lighting = 1;
    private int _lastHudSecond = -1;
    private int _initialViews;
    private int _initialModels;
    private int _smokePhase;
    private bool _smokeControlsPassed = true;
    private ulong _seed = TankBattleScenario.DefaultSeed;
    private string? _capturePath;
    private int _captureTick = 180;
    private bool _captured;
    private bool _capturePending;
    private int _maxFrame;
    private int _frames;
    private double _maxFrameMs;
    private int _closingFrames = -1;
    private int _exitCode;
    private float _initialPitch = OrbitCameraPose.DefaultPitch;
    private float _initialYaw;
    private MotionDiagnostics? _motionDiagnostics;
    private System.Numerics.Vector2? _prevLeadPos;

    public override void _Ready()
    {
        bool startPerspective = false;
        string[] args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--perspective") startPerspective = true;
            else if (args[i] == "--view-yaw" && i + 1 < args.Length && int.TryParse(args[++i], out int yaw)) _initialYaw = yaw * MathF.PI / 180f;
            else if (args[i] == "--demo-smoke-test") _smoke = true;
            else if (args[i] == "--motion-diagnostics") _motionDiagnostics = new MotionDiagnostics(4000);
            else if (args[i] == "--seed" && i + 1 < args.Length && ulong.TryParse(args[++i], out ulong seed)) _seed = seed;
            else if (args[i] == "--view-pitch" && i + 1 < args.Length && int.TryParse(args[++i], out int pitch)) _initialPitch = Math.Clamp(pitch, 8, 85) * MathF.PI / 180f;
            else if (args[i] == "--capture" && i + 1 < args.Length) _capturePath = args[++i];
            else if (args[i] == "--capture-tick" && i + 1 < args.Length && int.TryParse(args[++i], out int tick)) _captureTick = Math.Max(1, tick);
            else if (args[i] == "--quit-after-frames" && i + 1 < args.Length && int.TryParse(args[++i], out int frames)) _maxFrame = Math.Max(1, frames);
        }
        _geometry = TankBattleScenario.CreateStaticGeometry();
        if (_smoke) _smokeControlsPassed &= CheckBackdropSeams(_geometry.HeightMap);
        _catalog = ColdWarContent.LoadCatalog();
        MeshInstance3D terrain = TerrainView.Create(_geometry.HeightMap);
        _groundMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://TankBattleGround.gdshader") };
        _groundMaterial.SetShaderParameter("map_size", TankBattleScenario.MapSize);
        terrain.MaterialOverride = _groundMaterial;
        AddChild(terrain);
        AddChild(TankBattleBackdrop.Create(_geometry.HeightMap, terrain.MaterialOverride));
        for (int i = 0; i < _shotLights.Length; i++)
        {
            _shotLights[i] = new OmniLight3D
            {
                LightColor = new Color("ffc27c"),
                OmniRange = 32f,
                LightEnergy = 0f,
                ShadowEnabled = false
            };
            AddChild(_shotLights[i]);
        }
        _sun = new DirectionalLight3D { ShadowEnabled = true, DirectionalShadowMaxDistance = 1800f };
        AddChild(_sun);
        _environment = new Godot.Environment { TonemapMode = Godot.Environment.ToneMapper.Filmic };
        AddChild(new WorldEnvironment { Environment = _environment });
        ApplyLighting();
        Camera3D camera = new() { Projection = Camera3D.ProjectionType.Orthogonal, Current = true };
        AddChild(camera);
        _camera = new CameraRig(camera, new Vector3(TankBattleScenario.Center.X, 4, TankBattleScenario.Center.Y), 1900f, TankBattleScenario.MapSize, TankBattleScenario.MapSize);
        _camera.Model.SetSizeLimits(35f, 3200f);
        _camera.FarClipMargin = 16000f;
        _camera.GroundHeight = (x, z) => TankBattleBackdrop.SampleHeight(_geometry.HeightMap, x, z);
        if (startPerspective) _camera.SetPerspective(true);
        _sovietViews = new UnitViewSet(this, _geometry.HeightMap, _catalog, () => "soviet");
        _usViews = new UnitViewSet(this, _geometry.HeightMap, _catalog, () => "us");
        _effects = new CombatEffects(_geometry.HeightMap);
        AddChild(_effects);
        CanvasLayer canvas = new();
        AddChild(canvas);
        _summary = new Label { Position = new Vector2(24, 18), MouseFilter = Control.MouseFilterEnum.Ignore };
        _summary.AddThemeFontSizeOverride("font_size", 22);
        _summary.AddThemeColorOverride("font_shadow_color", Colors.Black);
        _summary.AddThemeConstantOverride("shadow_offset_x", 2);
        _summary.AddThemeConstantOverride("shadow_offset_y", 2);
        canvas.AddChild(_summary);
        _help = new Label { Position = new Vector2(24, 115), MouseFilter = Control.MouseFilterEnum.Ignore };
        _help.AddThemeFontSizeOverride("font_size", 16);
        canvas.AddChild(_help);
        Restart();
        GD.Print($"TANK_BATTLE_READY soviet=100 us=100 seed={_seed} mode=local_observer");
    }

    private static bool CheckBackdropSeams(HeightMap map)
    {
        for (int step = 0; step <= 2; step++)
        {
            float x = map.Width * step / 2f, z = map.Depth * step / 2f;
            if (MathF.Abs(TankBattleBackdrop.SampleHeight(map, x, 0) - map.GetHeight(x, 0)) > 0.001f ||
                MathF.Abs(TankBattleBackdrop.SampleHeight(map, x, map.Depth) - map.GetHeight(x, map.Depth)) > 0.001f ||
                MathF.Abs(TankBattleBackdrop.SampleHeight(map, 0, z) - map.GetHeight(0, z)) > 0.001f ||
                MathF.Abs(TankBattleBackdrop.SampleHeight(map, map.Width, z) - map.GetHeight(map.Width, z)) > 0.001f)
                return false;
        }
        return true;
    }

    private void Restart()
    {
        _session?.Dispose();
        _sovietViews.Clear();
        _usViews.Clear();
        _effects.Clear();
        Array.Clear(_litEvents);
        Array.Clear(_lightRemaining);
        foreach (OmniLight3D light in _shotLights) light.LightEnergy = 0f;
        _session = new TankBattleDemoSession(_geometry, _catalog, _seed);
        _sovietFog?.Dispose();
        _usFog?.Dispose();
        _sovietFog = new FogOfWarView(_geometry.HeightMap, _geometry, 40f);
        _usFog = new FogOfWarView(_geometry.HeightMap, _geometry, 40f);
        _sovietFog.UpdateAllNow(_session.Soviet.Replica, _catalog);
        _usFog.UpdateAllNow(_session.Us.Replica, _catalog);
        _groundMaterial.SetShaderParameter("soviet_fog", _sovietFog.Texture);
        _groundMaterial.SetShaderParameter("us_fog", _usFog.Texture);
        _groundMaterial.SetShaderParameter("fog_mode", _fogMode);
        _smokeFogPassed = _sovietFog.GetSeenCellCount() > 0 && _usFog.GetSeenCellCount() > 0 &&
            !_sovietFog.IsWorldPointSeen(TankBattleScenario.Center.X + TankBattleScenario.StartOffset, TankBattleScenario.Center.Y) &&
            !_usFog.IsWorldPointSeen(TankBattleScenario.Center.X - TankBattleScenario.StartOffset, TankBattleScenario.Center.Y);
        _paused = false;
        _lastHudSecond = -1;
        _camera.ResetToDefault();
        _camera.Orbit(_initialYaw, _initialPitch - OrbitCameraPose.DefaultPitch);
        SyncViews(0);
        _initialViews = _sovietViews.Views.Count + _usViews.Views.Count;
        _initialModels = 0;
        foreach (UnitView view in _sovietViews.Views.Values) if (view.HasVehicleModel) _initialModels++;
        foreach (UnitView view in _usViews.Views.Values) if (view.HasVehicleModel) _initialModels++;
        _captured = false;
        _prevLeadPos = null;
        _motionDiagnostics?.Reset();
        UpdateHud();
    }

    public override void _Process(double delta)
    {
        if (_closingFrames >= 0)
        {
            // Let AudioServer retire stopped playback objects, as in the normal client smoke path.
            if (++_closingFrames >= 12) GetTree().Quit(_exitCode);
            return;
        }
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_smoke && _smokePhase == 0) SendSmokeKey(Key.Space);
        float rotationTime = (float)Math.Clamp(delta, 0, 0.1);
        float yaw = (Input.IsPhysicalKeyPressed(Key.E) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.Q) ? 1f : 0f);
        float pitch = (Input.IsPhysicalKeyPressed(Key.T) ? 1f : 0f) - (Input.IsPhysicalKeyPressed(Key.G) ? 1f : 0f);
        if (yaw != 0 || pitch != 0) _camera.Orbit(yaw * rotationTime, pitch * rotationTime * 0.6f);
        _camera.Update(delta);
        // This viewing sandbox drops suspension time instead of fast-forwarding through the battle.
        double elapsed = _paused ? 0 : _smoke ? Simulation.TickSeconds : Math.Min(delta, 0.1) * (_fast ? 2 : 1);
        if (!_paused) _session.Advance(elapsed);
        SyncViews(elapsed);
        if (_motionDiagnostics is not null && !_paused)
        {
            EntityId leadId = new(1);
            System.Numerics.Vector2 pos = _session.Soviet.Replica.SamplePosition(leadId, _session.Soviet.RenderTick,
                out int leftTick, out int rightTick, out float alpha);
            float disp = _prevLeadPos.HasValue ? (pos - _prevLeadPos.Value).Length() : 0f;
            _prevLeadPos = pos;
            _session.Soviet.Replica.SampleYaws(leadId, _session.Soviet.RenderTick, out _, out float heading);
            int lod = _sovietViews.Views.TryGetValue(leadId, out UnitView? leadView) ? leadView.ActiveLod : 0;
            _motionDiagnostics.Record(
                elapsed,
                _session.Tick,
                _session.Soviet.RenderTick,
                leftTick,
                rightTick,
                alpha,
                pos,
                disp,
                heading,
                lod,
                rawDeltaSeconds: delta);
        }
        _sovietFog!.Update((float)elapsed, _session.Soviet.Replica, _catalog);
        _usFog!.Update((float)elapsed, _session.Us.Replica, _catalog);
        _effects.Update(elapsed);
        for (int i = 0; i < _shotLights.Length; i++)
        {
            _lightRemaining[i] = Math.Max(0f, _lightRemaining[i] - (float)elapsed);
            _shotLights[i].LightEnergy = _lightRemaining[i] * 70f;
        }
        ProcessSideEvents(_session.Soviet.Replica.Events, _sovietViews);
        ProcessSideEvents(_session.Us.Replica.Events, _usViews);
        _effects.UpdateListener(_camera.Focus);
        if (_lastHudSecond != _session.Tick / Simulation.TickRate) UpdateHud();
        _frames++;
        _maxFrameMs = Math.Max(_maxFrameMs, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (_capturePath is not null && !_captured && _session.Tick >= _captureTick)
        {
            _captured = true;
            _capturePending = true;
            RenderingServer.FramePostDraw += CaptureFrame;
        }
        if (_smoke && _smokePhase == 0)
        {
            _smokeControlsPassed &= _paused && _session.Tick == 0;
            float beforeWheel = _camera.Model.TargetSize;
            using InputEventMouseButton wheel = new() { Pressed = true, ButtonIndex = MouseButton.WheelUp, Factor = 0.05f };
            for (int i = 0; i < 20; i++) _UnhandledInput(wheel);
            _smokeControlsPassed &= MathF.Abs(_camera.Model.TargetSize - beforeWheel * CameraModel.DefaultZoomStepFactor) < 0.01f;
            float beforeDragYaw = _camera.ViewYaw;
            float beforeDragPitch = _camera.ViewPitch;
            using InputEventMouseMotion leftDrag = new() { ButtonMask = MouseButtonMask.Left, Relative = new Vector2(40, 20) };
            _UnhandledInput(leftDrag);
            _smokeControlsPassed &= _camera.ViewYaw == beforeDragYaw && _camera.ViewPitch == beforeDragPitch;
            using InputEventMouseMotion middleDrag = new() { ButtonMask = MouseButtonMask.Middle, Relative = new Vector2(40, 20) };
            _UnhandledInput(middleDrag);
            _smokeControlsPassed &= _camera.ViewYaw > beforeDragYaw && _camera.ViewPitch > beforeDragPitch;
            bool wasPerspective = _camera.IsPerspective;
            SendSmokeKey(Key.V);
            _smokeControlsPassed &= _camera.IsPerspective != wasPerspective;
            SendSmokeKey(Key.V);
            _smokeControlsPassed &= _camera.IsPerspective == wasPerspective;
            using InputEventMouseMotion orbit = new() { ButtonMask = MouseButtonMask.Middle, Relative = new Vector2(40, -2000) };
            _UnhandledInput(orbit);
            _smokeControlsPassed &= MathF.Abs(_camera.ViewPitch - OrbitCameraPose.MinPitch) < 0.001f;
            for (int i = 0; i < 4; i++) SendSmokeKey(Key.F);
            _smokeControlsPassed &= _fogMode == 0;
            SendSmokeKey(Key.Key1);
            _smokeControlsPassed &= _camera.Model.TargetSize == 650f;
            SendSmokeKey(Key.Key2);
            _smokeControlsPassed &= _camera.Focus.X > TankBattleScenario.Center.X;
            SendSmokeKey(Key.Key3);
            _smokeControlsPassed &= _camera.Model.TargetSize == 1900f && _camera.Focus.X == TankBattleScenario.Center.X;
            using InputEventMouseMotion altOrbit = new() { AltPressed = true, Relative = new Vector2(20, 0) };
            float beforeAlt = _camera.ViewYaw;
            _UnhandledInput(altOrbit);
            _smokeControlsPassed &= _camera.ViewYaw > beforeAlt;
            _camera.ResetToDefault();
            SendSmokeKey(Key.Space);
            _smokeControlsPassed &= !_paused;
            _smokePhase = 1;
        }
        else if (_smoke && _smokePhase == 1 && _session.Tick >= 3000)
        {
            _smokeControlsPassed &= _session.Shots > 0;
            SendSmokeKey(Key.R);
            _smokeControlsPassed &= _session.Tick == 0 && _session.Shots == 0 && _initialViews == 200;
            _smokePhase = 2;
        }
        if (_smoke && (_session.Tick >= 3600 || _frames >= 10000))
        {
            bool ok = _initialViews == 200 && _initialModels == 200 && _session.Shots > 20 &&
                _session.Director.SovietRemaining + _session.Director.UsRemaining < 200 && _session.Director.RejectedOrders == 0 &&
                _smokeControlsPassed && _smokeFogPassed && _smokePhase == 2;
            GD.Print($"TANK_BATTLE_SMOKE_{(ok ? "OK" : "FAIL")} tick={_session.Tick} initial={_initialViews} models={_initialModels} controls={_smokeControlsPassed} fog={_smokeFogPassed} shots={_session.Shots} soviet={_session.Director.SovietRemaining} us={_session.Director.UsRemaining} rejected={_session.Director.RejectedOrders}");
            BeginQuit(ok ? 0 : 1);
        }
        else if (_maxFrame > 0 && _frames >= _maxFrame)
        {
            GD.Print($"TANK_BATTLE_PREVIEW frames={_frames} tick={_session.Tick} shots={_session.Shots} max_cpu_frame_ms={_maxFrameMs:F2}");
            BeginQuit(0);
        }
    }

    private void BeginQuit(int exitCode)
    {
        if (_motionDiagnostics is not null && _motionDiagnostics.Count > 30)
        {
            MotionDiagnostics.Analysis analysis = _motionDiagnostics.Analyze(warmupFrames: 30);
            GD.Print($"MOTION_DIAGNOSTICS samples={analysis.SampleCount} clock_stalls={analysis.ClockStallFrames} clock_jumps={analysis.ClockJumpFrames} pos_stalls={analysis.PositionStallFrames} pos_jumps={analysis.PositionJumpFrames} max_disp={analysis.MaxDisplacement:F4} min_disp={analysis.MinDisplacement:F4} mean_disp={analysis.MeanDisplacement:F4}");
        }
        _effects.Clear();
        _closingFrames = 0;
        _exitCode = exitCode;
    }

    private void SendSmokeKey(Key key)
    {
        using InputEventKey input = new() { Pressed = true, Keycode = key };
        _UnhandledInput(input);
    }

    private void SyncViews(double delta)
    {
        _sovietViews.Sync(_session.Soviet.Replica, _session.Soviet.RenderTick);
        _usViews.Sync(_session.Us.Replica, _session.Us.RenderTick);
        Basis healthBarBasis = GetViewport().GetCamera3D().GlobalTransform.Basis;
        _sovietViews.FaceHealthBars(healthBarBasis);
        _usViews.FaceHealthBars(healthBarBasis);
        _sovietViews.UpdateLod(_camera.Size);
        _usViews.UpdateLod(_camera.Size);
        _sovietViews.UpdateRecoil(delta);
        _usViews.UpdateRecoil(delta);
        _sovietViews.UpdateEffects(delta);
        _usViews.UpdateEffects(delta);
    }

    private void ProcessSideEvents(ReadOnlySpan<CombatEventObservation> events, UnitViewSet views)
    {
        int count = 0;
        foreach (CombatEventObservation ev in events)
        {
            // Use each shooter's own observation for barrel recoil. Shared destruction IDs are deduplicated
            // by CombatEffects. Do not draw a second impact from the defender's TookFire event.
            if (ev.Kind == CombatEventKind.UnitDestroyed ||
                ev.Kind == CombatEventKind.ShotFired && ev.ShooterUnitId.Value != 0)
                _events[count++] = ev;
            if (ev.Kind == CombatEventKind.ShotFired && ev.ShooterUnitId.Value != 0 &&
                _session.Tick - ev.ServerTick < 3 && _litEvents[ev.EventId % 512] != ev.EventId)
            {
                _litEvents[ev.EventId % 512] = ev.EventId;
                int slot = _nextLight++ % _shotLights.Length;
                _lightRemaining[slot] = 0.12f;
                _shotLights[slot].Position = new Vector3(ev.Origin.X, ev.Origin.Y + 1f, ev.Origin.Z);
                _shotLights[slot].LightEnergy = 8.4f;
            }
        }
        _effects.ProcessEvents(_events.AsSpan(0, count), views, _session.Tick);
    }

    private void UpdateHud()
    {
        _lastHudSecond = _session.Tick / Simulation.TickRate;
        TankBattleDirector director = _session.Director;
        string result = director.IsFinished
            ? director.SovietRemaining == 0 ? "US WINS" : director.UsRemaining == 0 ? "SOVIET WINS" : "TIME LIMIT"
            : _paused ? "PAUSED" : "AUTOBATTLE";
        _summary.Text = $"DEAD GROUND  /  100 vs 100  /  {result}\nT-62: {director.SovietRemaining}     M60A1: {director.UsRemaining}     Shots: {_session.Shots}     Time: {_lastHudSecond}s\nCurrent contacts: Soviet {CountCurrent(_session.Soviet.Replica.Contacts)} / US {CountCurrent(_session.Us.Replica.Contacts)}   |   3.2 x 3.2 km / two rows per side";
        string fog = _fogMode == 0 ? "BOTH" : _fogMode == 1 ? "SOVIET" : _fogMode == 2 ? "US" : "OFF";
        string light = _lighting == 0 ? "DAY" : _lighting == 1 ? "DUSK" : "NIGHT";
        _help.Text = $"WASD / arrows: camera   Wheel/pinch: zoom   MMB / Alt+mouse or Q/E: orbit   T/G: tilt   V: perspective/RTS\n1 / 2: inspect army   3: overview   Space: pause   R: restart   Tab: {(_fast ? "2x" : "1x")} speed   L: {light}   Esc: quit\nF: visibility ({fog}, Soviet amber / US cyan)   Observer sees all units. Terrain shading shows ground visibility. Seed: {_seed}";
    }

    private void ApplyLighting()
    {
        bool night = _lighting == 2;
        bool day = _lighting == 0;
        _sun.RotationDegrees = new Vector3(day ? -55 : night ? -35 : -16, -65, 0);
        _sun.LightColor = new Color(day ? "fff0d2" : night ? "8bade3" : "ffc17d");
        _sun.LightEnergy = day ? 1.2f : night ? 0.32f : 1.35f;
        _environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        _environment.Sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(day ? "617e91" : night ? "080f20" : "383e60"),
                SkyHorizonColor = new Color(day ? "b6bca7" : night ? "27374f" : "d99563"),
                GroundBottomColor = new Color("141a1b"),
                GroundHorizonColor = new Color(day ? "80856a" : night ? "18263a" : "976c52")
            }
        };
        _environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        _environment.AmbientLightColor = new Color(day ? "b9c6b2" : night ? "6082b2" : "9698ae");
        _environment.AmbientLightEnergy = day ? 0.55f : night ? 0.30f : 0.45f;
        _environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
        _environment.FogEnabled = true;
        _environment.FogDensity = 0.00018f;
        _environment.FogLightColor = new Color(day ? "b4bac0" : night ? "1b273b" : "b39d87");
        _environment.FogSkyAffect = 0.2f;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_camera.HandleZoomInput(@event))
        {
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is InputEventMouseMotion motion && ((motion.ButtonMask & MouseButtonMask.Middle) != 0 || motion.AltPressed))
        {
            _camera.DragOrbit(motion.Relative);
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.Space) { _paused = !_paused; UpdateHud(); }
        else if (key.Keycode == Key.Tab) { _fast = !_fast; UpdateHud(); }
        else if (key.Keycode == Key.R) Restart();
        else if (key.Keycode == Key.V) { _camera.SetPerspective(!_camera.IsPerspective); UpdateHud(); }
        else if (key.Keycode == Key.F)
        {
            _fogMode = (_fogMode + 1) % 4;
            _groundMaterial.SetShaderParameter("fog_mode", _fogMode);
            UpdateHud();
        }
        else if (key.Keycode == Key.L) { _lighting = (_lighting + 1) % 3; ApplyLighting(); UpdateHud(); }
        else if (key.Keycode == Key.Key1) FocusArmy(_session.Soviet.Replica.OwnUnits);
        else if (key.Keycode == Key.Key2) FocusArmy(_session.Us.Replica.OwnUnits);
        else if (key.Keycode == Key.Key3)
        {
            _camera.CenterOn(new Vector3(TankBattleScenario.Center.X, 4, TankBattleScenario.Center.Y));
            _camera.Model.SetZoom(1900f);
        }
        else if (key.Keycode == Key.Escape) BeginQuit(0);
    }

    private void FocusArmy(ReadOnlySpan<OwnUnitObservation> units)
    {
        _camera.CenterOn(GetArmyFocus(units));
        _camera.Model.SetZoom(650f);
    }

    private static int CountCurrent(ReadOnlySpan<ContactObservation> contacts)
    {
        int count = 0;
        foreach (ContactObservation contact in contacts) if (contact.IsCurrent) count++;
        return count;
    }

    private static Vector3 GetArmyFocus(ReadOnlySpan<OwnUnitObservation> units)
    {
        if (units.IsEmpty) return new Vector3(TankBattleScenario.Center.X, 4, TankBattleScenario.Center.Y);
        System.Numerics.Vector2 center = default;
        foreach (OwnUnitObservation unit in units) center += unit.Position;
        center /= units.Length;
        return new Vector3(center.X, 4, center.Y);
    }

    private void CaptureFrame()
    {
        RenderingServer.FramePostDraw -= CaptureFrame;
        _capturePending = false;
        Error result = GetViewport().GetTexture().GetImage().SavePng(_capturePath!);
        GD.Print($"TANK_BATTLE_CAPTURE path={_capturePath} result={result}");
        foreach (UnitView view in _sovietViews.Views.Values)
        {
            Camera3D camera = GetViewport().GetCamera3D();
            GD.Print($"TANK_BATTLE_VIEW actor={view.Root.GlobalPosition} screen={camera.UnprojectPosition(view.Root.GlobalPosition)} behind={camera.IsPositionBehind(view.Root.GlobalPosition)} camera={camera.GlobalPosition} focus={_camera.Focus} pitch={_camera.ViewPitch}");
            break;
        }
    }

    public override void _ExitTree()
    {
        if (_capturePending) RenderingServer.FramePostDraw -= CaptureFrame;
        _effects?.Clear();
        _session?.Dispose();
        _sovietFog?.Dispose();
        _usFog?.Dispose();
    }
}
