using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal enum GarrisonXRayMode : byte
{
    None = 0,
    Idle = 1,
    Selected = 2,
    Cqb = 3,
    HostileCqb = 4
}

internal sealed class UnitView
{
    // Orthographic camera sizes where the client switches to LOD1 and LOD2.
    internal const float Lod1CameraSize = VehicleLod.Lod1CameraSize;
    internal const float Lod2CameraSize = VehicleLod.Lod2CameraSize;
    // A heading snap must not spin the tracks by more than this, in model meters.
    private const float MaxTurnTrackTravel = 0.5f;
    // Hull rock after a shot: a damped spring about the axis across the shot direction.
    private const float RockFrequencyHz = 1.8f;
    private const float RockDamping = 0.28f;
    private const float RockImpulse = 0.22f;
    // Movement dust reaches full strength at this speed in world meters per second.
    internal const float FullDustSpeed = 6f;
    // A longer step between two position updates is a snap or correction, not driving.
    internal const float MaxDustStep = 2f;
    private static readonly StringName TrackTravelParameter = "track_travel";

    // Animated nodes of one LOD. Every LOD follows the same turret, recoil and animation state.
    private sealed class Rig
    {
        internal Node3D? Turret;
        internal Node3D? GunPitch;
        internal Node3D? BarrelRecoil;
        internal Node3D? Muzzle;
        internal Node3D[] LaunchTubes = [];
        internal AnimationPlayer? AnimPlayer;
        internal Vector3 BarrelBindPosition;
        internal GeometryInstance3D[] LeftMotion = [];
        internal GeometryInstance3D[] RightMotion = [];
        internal Node3D? FrontRotor;
        internal Node3D? RearRotor;
        internal Node3D? FrontRotorBlur;
        internal Node3D? RearRotorBlur;
        internal GeometryInstance3D[] SolidRotorGeoms = [];
        internal GeometryInstance3D[] BlurRotorGeoms = [];
        internal float LastBlurBlend = -1f;
        internal Node3D? RearRamp;
        internal Vector3 RearRampBindRotation;
        internal GeometryInstance3D[] AllMeshes = [];
    }

    public Node3D Root { get; } = new();
    private readonly MeshInstance3D _selection;
    private Node3D? _turret;
    private readonly VehicleVisual? _visual;
    // One rig per LOD, created when that LOD is first shown.
    private readonly Rig?[] _rigs;
    private readonly float _trackGauge;
    private readonly float _trackWrap;
    private int _activeLod;
    private bool _dustCreated;
    private Vector3 _turretRotation;
    private float _gunPitchRad;
    private int _nextLaunchTubeCursor;
    private bool _hasAim;
    private float _leftTrackTravel;
    private float _rightTrackTravel;
    private bool _hasHeading;
    // Hull heading kept apart from Model.Rotation, which also carries the shot rock.
    private float _heading;
    private float _rockAngle;
    private float _rockVelocity;
    private Vector3 _rockAxis = Vector3.Right;
    private readonly Vector3 _modelScale;
    private readonly GpuParticles3D?[] _dust = new GpuParticles3D?[2];
    private float _distanceSinceEffects;
    private float _dustSpeed;
    private Vector3 _previousPosition;
    private bool _hasPosition;
    internal Node3D Model { get; }
    private static readonly StringName AnimIdle = "Idle";
    private static readonly StringName AnimWalk = "Walk";
    private static readonly StringName AnimRun = "Run";
    private static readonly StringName AnimAimStanding = "AimStanding";

    internal string? UnitId { get; }
    internal bool HasVehicleModel { get; }
    internal bool IsInfantry { get; }
    internal bool IsHelicopter { get; }
    private float _altitude;
    private float _pitchRad;
    private float _rollRad;
    private FlightPhase _flightPhase = FlightPhase.Ground;
    private FlightStateFlags _flightFlags = FlightStateFlags.None;
    private bool _hasSlingLoad;
    private bool _isSlung;
    private float _swayPhase;
    private float _rotorSpinSpeed;
    private float _rotorAngle;
    private float _rampAngleRad;
    private MeshInstance3D? _altitudeTether;
    private MeshInstance3D? _groundFootprintRing;
    private Node3D? _fastRopeRoot;
    private MeshInstance3D? _fastRopeLeft;
    private MeshInstance3D? _fastRopeRight;
    private float _fastRopeLingerTimer;
    private Node3D? _slingHarnessRoot;
    private GpuParticles3D? _downwashDust;
    private GpuParticles3D? _crashSmoke;
    private string _currentAnim = "";
    private StringName? _currentAnimName;
    private float _aimingTimer;
    private bool _isGarrisonedVisual;
    private GarrisonXRayMode _garrisonXRayMode = GarrisonXRayMode.None;
    private bool _garrisonIsRunning;
    private bool _isFastRoping;
    private Vector3 _fastRopeTop;
    private Vector3 _fastRopeBottom;
    private Vector3 _fastRopeLandingTarget;
    private float _fastRopeElapsed;
    private float _fastRopeYaw;
    private const float FastRopeSlideDuration = 0.82f;
    private const float FastRopeStepOffDuration = 0.28f;
    internal bool WasContainedInTransport { get; set; }
    internal bool IsFastRoping => _isFastRoping;
    internal bool IsGarrisonedVisual => _isGarrisonedVisual;
    internal GarrisonXRayMode ActiveGarrisonXRayMode => _garrisonXRayMode;
    internal int ActiveLod => _activeLod;
    internal int LodCount => _rigs.Length;
    internal float Altitude => _altitude;
    internal FlightPhase CurrentFlightPhase => _flightPhase;
    internal bool AreRotorBlursVisible => ActiveRig.FrontRotorBlur?.Visible == true;
    internal float RearRampAngleDeg => Mathf.RadToDeg(_rampAngleRad);
    internal bool IsAltitudeTetherVisible => _altitudeTether?.Visible == true;
    internal bool IsGroundFootprintVisible => _groundFootprintRing?.Visible == true;
    internal bool AreFastRopesVisible => _fastRopeRoot?.Visible == true;
    internal bool IsSlingHarnessVisible => _slingHarnessRoot?.Visible == true;
    internal bool IsDownwashEmitting => _downwashDust?.Emitting == true;
    internal bool IsSlung => _isSlung;
    internal float LeftTrackTravel => _leftTrackTravel;
    internal float RightTrackTravel => _rightTrackTravel;
    internal float RockAngle => _rockAngle;
    internal float GunPitchRad => _gunPitchRad;
    internal Node3D? GunPitchNode => ActiveRig.GunPitch;
    internal int LaunchTubeCount => ActiveRig.LaunchTubes.Length;
    internal GpuParticles3D? LeftDust => _dust[0];
    internal float DustSpeed => _dustSpeed;
    internal void SetDustSpeedForTesting(float speed)
    {
        _dustSpeed = speed;
        _distanceSinceEffects = speed * 0.016f;
    }
    internal string CurrentAnimation => _currentAnim;
    internal AnimationPlayer? AnimationPlayer => ActiveRig.AnimPlayer;

    public void SetAiming(bool aiming)
    {
        if (aiming)
        {
            _aimingTimer = 0.8f;
        }
    }

    public void PlayAnimation(string clipName)
    {
        StringName name = clipName;
        AnimationPlayer? player = ActiveRig.AnimPlayer;
        if (player is not null && player.HasAnimation(name))
        {
            player.Play(name);
            _currentAnim = clipName;
            _currentAnimName = name;
        }
    }

    public UnitView(ushort definition = 0, float? initialCameraSize = null)
        : this(UnitViewSet.GetDefaultUnitId(definition), classification: UnitViewSet.GetDefaultClassification(definition), initialCameraSize: initialCameraSize) { }

    public UnitView(string? unitId, string? faction = null, string? classification = null, float? initialCameraSize = null)
        : this(unitId, classification, VehicleVisualCatalog.InstantiateVisual(unitId, faction, initialCameraSize)) { }

    internal UnitView(VehicleVisual visual, string? unitId = null, string? classification = null)
        : this(unitId, classification, visual ?? throw new ArgumentNullException(nameof(visual))) { }

    private UnitView(string? unitId, string? classification, VehicleVisual? visual)
    {
        UnitId = unitId;
        classification ??= UnitViewSet.GetDefaultClassification(unitId);
        IsInfantry = string.Equals(classification, "infantry", StringComparison.OrdinalIgnoreCase);
        IsHelicopter = string.Equals(classification, "helicopter", StringComparison.OrdinalIgnoreCase);
        HasVehicleModel = visual is not null;
        Model = visual?.Root ?? new Node3D { Name = "PlaceholderModel" };
        _visual = visual;
        _activeLod = visual?.ActiveLod ?? 0;
        _trackGauge = visual?.TrackGauge ?? 0f;
        _trackWrap = visual?.TrackWrap ?? 0f;
        Root.AddChild(Model);
        _rigs = new Rig?[visual?.LodCount ?? 1];
        Rig initialRig = CreateRig(visual?.GetOrCreateLod(_activeLod) ?? Model);
        _rigs[_activeLod] = initialRig;
        if (initialRig.FrontRotor is not null || initialRig.FrontRotorBlur is not null)
        {
            IsHelicopter = true;
        }
        _turret = initialRig.Turret;
        _barrelRecoil = initialRig.BarrelRecoil;
        _muzzle = initialRig.Muzzle;
        if (_muzzle is null && IsInfantry)
        {
            Node3D mNode = new() { Name = "Muzzle", Position = new Vector3(0, 1.35f, -0.6f) };
            Model.AddChild(mNode);
            _muzzle = mNode;
            initialRig.Muzzle = mNode;
        }
        _barrelBindPosition = initialRig.BarrelBindPosition;
        _modelBindPosition = Model.Position;
        _modelScale = Model.Scale;
        if (IsHelicopter)
        {
            _rampAngleRad = Mathf.DegToRad(42f);
            _rotorSpinSpeed = 2.8f;
            ApplyHelicopterRigState(initialRig);
            _altitudeTether = new MeshInstance3D
            {
                Name = "AltitudeTether",
                Mesh = new BoxMesh { Size = new Vector3(0.32f, 1.0f, 0.32f) },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.70f, 0.90f, 1.0f, 0.55f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    DisableFog = true
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false
            };
            Root.AddChild(_altitudeTether);

            _groundFootprintRing = new MeshInstance3D
            {
                Name = "GroundFootprintRing",
                Mesh = new TorusMesh { InnerRadius = 4.9f, OuterRadius = 5.35f, Rings = 32, RingSegments = 8 },
                Scale = new Vector3(1f, 0.14f, 1f),
                Position = new Vector3(0f, 0.18f, 0f),
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.68f, 0.90f, 1.0f, 0.48f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    DisableFog = true
                },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false
            };
            Root.AddChild(_groundFootprintRing);

            StandardMaterial3D ropeMat = new()
            {
                AlbedoColor = new Color(0.18f, 0.16f, 0.12f),
                Roughness = 0.95f
            };
            _fastRopeRoot = new Node3D { Name = "FastRopeRoot", Visible = false };
            _fastRopeLeft = new MeshInstance3D
            {
                Name = "FastRopeLeft",
                Mesh = new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.08f, Height = 1.0f, RadialSegments = 6 },
                MaterialOverride = ropeMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            _fastRopeRight = new MeshInstance3D
            {
                Name = "FastRopeRight",
                Mesh = new CylinderMesh { TopRadius = 0.08f, BottomRadius = 0.08f, Height = 1.0f, RadialSegments = 6 },
                MaterialOverride = ropeMat,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            _fastRopeRoot.AddChild(_fastRopeLeft);
            _fastRopeRoot.AddChild(_fastRopeRight);
            Root.AddChild(_fastRopeRoot);

            StandardMaterial3D cableMat = new()
            {
                AlbedoColor = new Color(0.14f, 0.14f, 0.15f),
                Roughness = 0.7f,
                Metallic = 0.5f
            };
            _slingHarnessRoot = new Node3D { Name = "SlingHarnessRoot", Visible = false };
            CylinderMesh cableMesh = new() { TopRadius = 0.06f, BottomRadius = 0.06f, Height = 1.0f, RadialSegments = 6 };
            // 4-leg tandem cargo sling converging from CH-47D belly hook to cargo corners
            Vector3[] cornerOffsets =
            [
                new(-1.1f, 0f, -1.6f),
                new(1.1f, 0f, -1.6f),
                new(-1.1f, 0f, 1.6f),
                new(1.1f, 0f, 1.6f)
            ];
            for (int c = 0; c < cornerOffsets.Length; c++)
            {
                Vector3 top = new(0f, 0f, 0f);
                Vector3 bottom = new(cornerOffsets[c].X, -9.2f, cornerOffsets[c].Z);
                Vector3 mid = (top + bottom) * 0.5f;
                Vector3 delta = bottom - top;
                float len = delta.Length();
                MeshInstance3D leg = new()
                {
                    Name = $"SlingCable_{c}",
                    Mesh = cableMesh,
                    MaterialOverride = cableMat,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                    Position = mid,
                    Scale = new Vector3(1f, len, 1f)
                };
                Vector3 dir = delta / len;
                Vector3 axis = Vector3.Up.Cross(dir);
                if (axis.LengthSquared() > 1e-6f)
                {
                    float ang = Mathf.Acos(Mathf.Clamp(Vector3.Up.Dot(dir), -1f, 1f));
                    leg.Basis = new Basis(axis.Normalized(), ang).Scaled(new Vector3(1f, len, 1f));
                }
                _slingHarnessRoot.AddChild(leg);
            }
            Root.AddChild(_slingHarnessRoot);

            _downwashDust = ParticleEffects.CreateMovementDust(new Vector3(5.2f, 0.18f, 5.2f));
            _downwashDust.Name = "RotorDownwashDust";
            _downwashDust.Position = new Vector3(0f, 0.35f, 0f);
            _downwashDust.Emitting = false;
            Root.AddChild(_downwashDust);
        }
        CreateDustIfReady(initialRig);
        if (visual is null)
        {
            Model.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(3, 2, 4) },
                Position = new Vector3(0, 1.1f, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("b8c5a0"), Roughness = 0.85f }
            });
            Model.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(1.6f, 0.5f, 1.8f) },
                Position = new Vector3(0, 2.35f, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("485c45"), Roughness = 1 }
            });
        }

        _selection = new MeshInstance3D
        {
            Mesh = IsInfantry
                ? new TorusMesh { InnerRadius = 0.65f, OuterRadius = 0.85f, Rings = 24, RingSegments = 8 }
                : IsHelicopter
                    ? new TorusMesh { InnerRadius = 6.8f, OuterRadius = 7.4f, Rings = 32, RingSegments = 8 }
                    : new TorusMesh { InnerRadius = 2.5f, OuterRadius = 2.8f, Rings = 24, RingSegments = 8 },
            Scale = new Vector3(1, 0.15f, 1),
            Position = new Vector3(0, 0.15f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = HudTheme.Accent,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        Root.AddChild(_selection);

        _healthBarRoot = new Node3D { Position = new Vector3(0, IsHelicopter ? 6.5f : (IsInfantry ? 2.1f : 3.4f), 0) };
        Root.AddChild(_healthBarRoot);
        Vector3 barSize = IsInfantry ? new Vector3(1.1f, 0.08f, 0.08f) : new Vector3(1.8f, 0.12f, 0.12f);
        _healthBarBg = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = barSize },
            Position = Vector3.Zero,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("1a1a1a"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _healthBarRoot.AddChild(_healthBarBg);

        _healthBarFg = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(barSize.X, barSize.Y, barSize.Z + 0.02f) },
            Position = new Vector3(0, 0, 0.03f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = HudTheme.Text,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        _healthBarRoot.AddChild(_healthBarFg);

        _weaponRangeRing = new MeshInstance3D
        {
            Mesh = _weaponRangeMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        Root.AddChild(_weaponRangeRing);

        _sensorRangeRing = new MeshInstance3D
        {
            Mesh = _sensorRangeMesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        Root.AddChild(_sensorRangeRing);

        DeadGround.Rules.Ops.UnitCatalog defaultCat = UnitViewSet.GetDefaultCatalog();
        if (unitId is not null)
        {
            for (ushort i = 0; i < defaultCat.Units.Length; i++)
            {
                if (defaultCat.Units[i].Id == unitId)
                {
                    var (sensorRange, weaponRange) = UnitViewSet.GetRanges(defaultCat, i);
                    ConfigureRanges(sensorRange, weaponRange, null);
                    break;
                }
            }
        }
    }

    private static readonly StandardMaterial3D WeaponRangeMaterial = new()
    {
        AlbedoColor = new Color(HudTheme.Accent, 0.8f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DisableFog = true
    };

    private static readonly StandardMaterial3D SensorRangeMaterial = new()
    {
        AlbedoColor = new Color(HudTheme.Text, 0.45f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        DisableFog = true
    };

    private readonly Node3D _healthBarRoot;
    private readonly MeshInstance3D _healthBarBg;
    internal Node3D HealthBarRoot => _healthBarRoot;
    internal MeshInstance3D HealthBarFill => _healthBarFg;

    internal void FaceHealthBar(Basis cameraBasis) => _healthBarRoot.GlobalBasis = cameraBasis;
    internal void SetWorldHealthBarVisible(bool visible) => _healthBarRoot.Visible = visible;
    private readonly MeshInstance3D _healthBarFg;
    private readonly MeshInstance3D _weaponRangeRing;
    private readonly MeshInstance3D _sensorRangeRing;
    private readonly ImmediateMesh _weaponRangeMesh = new();
    private readonly ImmediateMesh _sensorRangeMesh = new();
    private float _weaponRange;
    private float _sensorRange;
    private DeadGround.Engine.HeightMap? _map;
    private Vector3 _lastDrapedPosition;
    private bool _ringsDraped;
    private bool _isSelected;
    private bool _rangeIndicatorsVisible = true;
    private readonly Vector3[] _ringVertices = new Vector3[144];
    internal const float MinRedrapeDistance = 3.0f;
    private const float MinRedrapeDistanceSquared = MinRedrapeDistance * MinRedrapeDistance;
    internal int RingsRebuildCount { get; private set; }

    public float WeaponRange => _weaponRange;
    public float SensorRange => _sensorRange;
    public bool IsArmed => _weaponRange > 0f;
    internal MeshInstance3D WeaponRangeRing => _weaponRangeRing;
    internal MeshInstance3D SensorRangeRing => _sensorRangeRing;

    public void ConfigureRanges(float sensorRange, float weaponRange, DeadGround.Engine.HeightMap? map)
    {
        _sensorRange = sensorRange;
        _weaponRange = weaponRange;
        _map = map;
        _ringsDraped = false;
        if (_isSelected)
        {
            _weaponRangeRing.Visible = _rangeIndicatorsVisible && _weaponRange > 0f;
            _sensorRangeRing.Visible = _rangeIndicatorsVisible && _sensorRange > 0f;
            DrapeRings();
        }
    }

    private void DrapeRings()
    {
        if (!_isSelected || !_rangeIndicatorsVisible) return;
        if (_weaponRange > 0f)
        {
            BuildRing(_weaponRangeMesh, WeaponRangeMaterial, _weaponRange);
        }
        if (_sensorRange > 0f)
        {
            BuildRing(_sensorRangeMesh, SensorRangeMaterial, _sensorRange);
        }
        _lastDrapedPosition = Root.Position;
        _ringsDraped = true;
        RingsRebuildCount++;
    }

    internal static bool ClipSegmentToMap(
        float x0, float z0, float x1, float z1,
        float width, float depth,
        out float cx0, out float cz0, out float cx1, out float cz1)
    {
        float dx = x1 - x0;
        float dz = z1 - z0;
        float t0 = 0.0f;
        float t1 = 1.0f;

        if (!ClipTest(-dx, x0, ref t0, ref t1)) goto Rejected;
        if (!ClipTest(dx, width - x0, ref t0, ref t1)) goto Rejected;
        if (!ClipTest(-dz, z0, ref t0, ref t1)) goto Rejected;
        if (!ClipTest(dz, depth - z0, ref t0, ref t1)) goto Rejected;

        cx0 = x0 + t0 * dx;
        cz0 = z0 + t0 * dz;
        cx1 = x0 + t1 * dx;
        cz1 = z0 + t1 * dz;
        return true;

    Rejected:
        cx0 = cz0 = cx1 = cz1 = 0f;
        return false;

        static bool ClipTest(float p, float q, ref float t0, ref float t1)
        {
            if (p < 0.0f)
            {
                float r = q / p;
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else if (p > 0.0f)
            {
                float r = q / p;
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            else if (q < 0.0f)
            {
                return false;
            }
            return true;
        }
    }

    private void BuildRing(ImmediateMesh mesh, StandardMaterial3D material, float radius)
    {
        mesh.ClearSurfaces();
        if (radius <= 0f) return;

        const int segments = 72;
        const float step = Mathf.Tau / segments;
        float cx = Root.Position.X;
        float cy = Root.Position.Y;
        float cz = Root.Position.Z;

        int vertexCount = 0;

        for (int i = 0; i < segments; i++)
        {
            if (ReferenceEquals(material, SensorRangeMaterial) && (i & 1) == 1) continue;
            float theta0 = i * step;
            float theta1 = (i + 1) * step;
            float wx0 = cx + radius * Mathf.Cos(theta0);
            float wz0 = cz + radius * Mathf.Sin(theta0);
            float wx1 = cx + radius * Mathf.Cos(theta1);
            float wz1 = cz + radius * Mathf.Sin(theta1);

            if (_map is not null)
            {
                if (!ClipSegmentToMap(wx0, wz0, wx1, wz1, _map.Width, _map.Depth, out float cwx0, out float cwz0, out float cwx1, out float cwz1))
                {
                    continue;
                }

                float wy0 = _map.GetHeight(cwx0, cwz0) + 0.12f;
                float wy1 = _map.GetHeight(cwx1, cwz1) + 0.12f;
                _ringVertices[vertexCount++] = new Vector3(cwx0 - cx, wy0 - cy, cwz0 - cz);
                _ringVertices[vertexCount++] = new Vector3(cwx1 - cx, wy1 - cy, cwz1 - cz);
            }
            else
            {
                _ringVertices[vertexCount++] = new Vector3(wx0 - cx, 0.12f, wz0 - cz);
                _ringVertices[vertexCount++] = new Vector3(wx1 - cx, 0.12f, wz1 - cz);
            }
        }

        if (vertexCount == 0) return;

        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, material);
        for (int i = 0; i < vertexCount; i++)
        {
            mesh.SurfaceAddVertex(_ringVertices[i]);
        }
        mesh.SurfaceEnd();
    }
    private Node3D? _barrelRecoil;
    private Node3D? _muzzle;
    private Vector3 _barrelBindPosition;
    private readonly Vector3 _modelBindPosition;
    private float _recoilAmount;
    private Vector3 _fallbackRecoilOffset = Vector3.Zero;

    public Vector3 RecoilOffset => _barrelRecoil is not null
        ? new Vector3(0, 0, _recoilAmount)
        : _fallbackRecoilOffset;

    private static Rig CreateRig(Node3D lod)
    {
        Rig rig = new()
        {
            Turret = lod.FindChild("TurretYaw", recursive: true, owned: false) as Node3D,
            GunPitch = lod.FindChild("GunPitch", recursive: true, owned: false) as Node3D,
            BarrelRecoil = lod.FindChild("BarrelRecoil", recursive: true, owned: false) as Node3D,
            Muzzle = lod.FindChild("Muzzle", recursive: true, owned: false) as Node3D,
            AnimPlayer = lod.FindChild("AnimationPlayer", recursive: true, owned: false) as AnimationPlayer,
            FrontRotor = lod.FindChild("FrontRotor", recursive: true, owned: false) as Node3D,
            RearRotor = lod.FindChild("RearRotor", recursive: true, owned: false) as Node3D,
            FrontRotorBlur = lod.FindChild("FrontRotorBlur", recursive: true, owned: false) as Node3D,
            RearRotorBlur = lod.FindChild("RearRotorBlur", recursive: true, owned: false) as Node3D,
            RearRamp = lod.FindChild("RearRamp", recursive: true, owned: false) as Node3D,
        };
        rig.LaunchTubes = FindLaunchTubes(rig.GunPitch ?? lod);
        rig.BarrelBindPosition = rig.BarrelRecoil?.Position ?? Vector3.Zero;
        rig.RearRampBindRotation = rig.RearRamp?.Rotation ?? Vector3.Zero;
        rig.LeftMotion = FindMotionMeshes(lod.FindChild("LeftTrack", recursive: true, owned: false));
        rig.RightMotion = FindMotionMeshes(lod.FindChild("RightTrack", recursive: true, owned: false));
        // Transparent rotor blur discs must not cast solid shadows and should stay visually subtle without bright yellow rings.
        ConfigureRotorBlurVisual(rig.FrontRotorBlur);
        ConfigureRotorBlurVisual(rig.RearRotorBlur);
        // Match tactical low-vis CARC composite blades on solid rotors (remove bright yellow tip bands for ground/flight consistency).
        ConfigureSolidRotorVisual(rig.FrontRotor);
        ConfigureSolidRotorVisual(rig.RearRotor);
        rig.SolidRotorGeoms = CollectGeometryInstances(rig.FrontRotor, rig.RearRotor);
        rig.BlurRotorGeoms = CollectGeometryInstances(rig.FrontRotorBlur, rig.RearRotorBlur);
        rig.AllMeshes = CollectGeometryInstances(lod, null);
        return rig;
    }

    internal static GeometryInstance3D[] CollectGeometryInstances(Node? a, Node? b)
    {
        if (a is null && b is null) return [];
        List<GeometryInstance3D> list = [];
        CollectInto(a, list);
        CollectInto(b, list);
        return [.. list];

        static void CollectInto(Node? node, List<GeometryInstance3D> target)
        {
            if (node is null) return;
            if (node is GeometryInstance3D geom) target.Add(geom);
            int count = node.GetChildCount();
            for (int i = 0; i < count; i++) CollectInto(node.GetChild(i), target);
        }
    }

    private static StandardMaterial3D? s_rotorBlurMaterial;
    private static StandardMaterial3D? s_rotorBladeMaterial;

    private static StandardMaterial3D GetRotorBladeMaterial()
    {
        return s_rotorBladeMaterial ??= new StandardMaterial3D
        {
            AlbedoColor = new Color(0.14f, 0.15f, 0.13f),
            Roughness = 0.85f,
            Metallic = 0.08f
        };
    }

    internal static void ConfigureSolidRotorVisual(Node? root)
    {
        if (root is null) return;
        if (root is MeshInstance3D meshInstance && meshInstance.Mesh is Mesh mesh)
        {
            int surfaceCount = mesh.GetSurfaceCount();
            for (int s = 0; s < surfaceCount; s++)
            {
                Material? mat = meshInstance.GetSurfaceOverrideMaterial(s) ?? mesh.SurfaceGetMaterial(s);
                bool isYellowTip = mat is not null &&
                    (mat.ResourceName.Contains("TipYellow", StringComparison.OrdinalIgnoreCase) ||
                     (mat is BaseMaterial3D bm && bm.AlbedoColor.R > 0.65f && bm.AlbedoColor.G > 0.50f && bm.AlbedoColor.B < 0.30f));
                if (isYellowTip)
                {
                    meshInstance.SetSurfaceOverrideMaterial(s, GetRotorBladeMaterial());
                }
            }
        }
        else if (root is GeometryInstance3D geom &&
            geom.Name.ToString().Contains("BladeTipStripe", StringComparison.OrdinalIgnoreCase))
        {
            geom.MaterialOverride = GetRotorBladeMaterial();
        }
        int count = root.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            ConfigureSolidRotorVisual(root.GetChild(i));
        }
    }

    private static StandardMaterial3D GetRotorBlurMaterial()
    {
        if (s_rotorBlurMaterial is not null)
            return s_rotorBlurMaterial;

        const int size = 256;
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            float cy = (y + 0.5f) / size * 2.0f - 1.0f;
            for (int x = 0; x < size; x++)
            {
                float cx = (x + 0.5f) / size * 2.0f - 1.0f;
                float r = MathF.Sqrt(cx * cx + cy * cy);
                int idx = (y * size + x) * 4;
                if (r >= 1.0f || r <= 0.08f)
                {
                    pixels[idx] = 0;
                    pixels[idx + 1] = 0;
                    pixels[idx + 2] = 0;
                    pixels[idx + 3] = 0;
                    continue;
                }

                float theta = MathF.Atan2(cy, cx);
                float cosWave = MathF.Cos(3.0f * theta + r * 0.9f);
                float wave = 0.40f + 0.60f * (cosWave * cosWave);
                float innerMask = Math.Clamp((r - 0.08f) / 0.07f, 0f, 1f);
                float outerMask = Math.Clamp((1.0f - r) / 0.08f, 0f, 1f);
                float radial = innerMask * outerMask;

                // Subtle dark charcoal rotor motion blur with very faint muted outer tip hint (no bright yellow halo)
                float tipHint = MathF.Exp(-((r - 0.92f) * (r - 0.92f)) / (0.03f * 0.03f));
                float red = 0.14f + 0.10f * tipHint;
                float green = 0.15f + 0.09f * tipHint;
                float blue = 0.13f + 0.05f * tipHint;
                float alpha = radial * (0.14f * wave + 0.07f * (1.0f - r * 0.4f) + 0.04f * tipHint);
                alpha = Math.Clamp(alpha, 0f, 0.24f);

                pixels[idx] = (byte)MathF.Round(red * 255f);
                pixels[idx + 1] = (byte)MathF.Round(green * 255f);
                pixels[idx + 2] = (byte)MathF.Round(blue * 255f);
                pixels[idx + 3] = (byte)MathF.Round(alpha * 255f);
            }
        }

        Image image = Image.CreateFromData(size, size, useMipmaps: false, Image.Format.Rgba8, pixels);
        ImageTexture tex = ImageTexture.CreateFromImage(image);
        s_rotorBlurMaterial = new StandardMaterial3D
        {
            AlbedoTexture = tex,
            AlbedoColor = Colors.White,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            Roughness = 0.92f,
            Metallic = 0.05f
        };
        return s_rotorBlurMaterial;
    }

    internal static void ConfigureRotorBlurVisual(Node? root)
    {
        if (root is null) return;
        if (root is GeometryInstance3D geom)
        {
            geom.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            geom.MaterialOverride = GetRotorBlurMaterial();
        }
        int count = root.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            ConfigureRotorBlurVisual(root.GetChild(i));
        }
    }

    // Running gear meshes exported with motion data (see vehicle_kit.emit_motion_parts).
    private static GeometryInstance3D[] FindMotionMeshes(Node? side)
    {
        if (side is null) return [];
        List<GeometryInstance3D> found = [];
        foreach (Node node in side.FindChildren("*_Motion_*", "GeometryInstance3D", recursive: true, owned: false))
            found.Add((GeometryInstance3D)node);
        return [.. found];
    }

    // Balanced left-right ripple firing order for 4x10 BM-21 Grad rocket racks (Launch_01..Launch_40).
    private static readonly int[] BalancedTubeSequence = BuildBalancedTubeSequence();

    private static int[] BuildBalancedTubeSequence()
    {
        int[] seq = new int[40];
        int k = 0;
        for (int row = 0; row < 4; row++)
        {
            for (int pair = 0; pair < 5; pair++)
            {
                seq[k++] = row * 10 + pair;
                seq[k++] = row * 10 + (9 - pair);
            }
        }
        return seq;
    }

    private static Node3D[] FindLaunchTubes(Node root)
    {
        List<Node3D> found = [];
        for (int i = 1; i <= 40; i++)
        {
            string name = i < 10 ? $"Launch_0{i}" : $"Launch_{i}";
            if (root.FindChild(name, recursive: true, owned: false) is Node3D tube)
            {
                found.Add(tube);
            }
        }
        return found.Count > 0 ? [.. found] : [];
    }

    private Rig ActiveRig => _rigs[_activeLod]!;

    private Node3D LodNode(int lod) => _visual?.GetOrCreateLod(lod) ?? Model;

    // A LOD shown for the first time picks up the current aim and recoil.
    private Rig EnsureRig(int lod)
    {
        Rig? rig = _rigs[lod];
        if (rig is not null) return rig;
        rig = CreateRig(LodNode(lod));
        if (_turret is null && rig.Turret is not null) _turret = rig.Turret;
        if (_barrelRecoil is null && rig.BarrelRecoil is not null)
        {
            _barrelRecoil = rig.BarrelRecoil;
            _barrelBindPosition = rig.BarrelBindPosition;
        }
        if (_muzzle is null && rig.Muzzle is not null) _muzzle = rig.Muzzle;
        if (_hasAim && rig.Turret is not null)
            rig.Turret.Rotation = _turretRotation;
        if (rig.GunPitch is not null)
            rig.GunPitch.Rotation = new Vector3(_gunPitchRad, 0f, 0f);
        if (rig.BarrelRecoil is not null)
            rig.BarrelRecoil.Position = rig.BarrelBindPosition + new Vector3(0, 0, _recoilAmount);
        if (IsHelicopter)
            ApplyHelicopterRigState(rig);
        _rigs[lod] = rig;
        CreateDustIfReady(rig);
        return rig;
    }

    internal bool IsLodLoaded(int index) => _rigs[index] is not null;

    internal Node3D? GetLod(int index) => _rigs[index] is null ? null : LodNode(index);
    internal int LeftMotionMeshCount => ActiveRig.LeftMotion.Length;
    internal int RightMotionMeshCount => ActiveRig.RightMotion.Length;

    internal int LoadedMotionMeshCount
    {
        get
        {
            int count = 0;
            foreach (Rig? rig in _rigs)
                if (rig is not null) count += rig.LeftMotion.Length + rig.RightMotion.Length;
            return count;
        }
    }

    // The travel value the shader sees on the active LOD, for regression checks.
    internal float GetShaderTrackTravel(bool left)
    {
        GeometryInstance3D[] meshes = left ? ActiveRig.LeftMotion : ActiveRig.RightMotion;
        return meshes.Length == 0 ? float.NaN : meshes[0].GetInstanceShaderParameter(TrackTravelParameter).AsSingle();
    }

    internal bool UpdateLod(float cameraSize)
    {
        int unlimited = int.MaxValue;
        return UpdateLod(cameraSize, ref unlimited);
    }

    internal bool UpdateLod(float cameraSize, ref int remainingInstantiations)
    {
        int lod = VehicleLod.SelectNextLod(cameraSize, _activeLod, _rigs.Length);
        if (lod == _activeLod) return false;
        if (!IsLodLoaded(lod))
        {
            if (remainingInstantiations <= 0) return false;
            remainingInstantiations--;
        }

        AnimationPlayer? prevPlayer = _rigs[_activeLod]?.AnimPlayer;
        Rig nextRig = EnsureRig(lod);
        if (_visual is not null)
        {
            _visual.SwitchLod(lod, out _);
        }
        else
        {
            LodNode(_activeLod).Visible = false;
            LodNode(lod).Visible = true;
        }
        _activeLod = lod;
        PushTrackTravel();
        if (IsHelicopter)
            ApplyHelicopterRigState(nextRig);
        if (_isGarrisonedVisual)
            ApplyGarrisonXRayToRig(nextRig, _garrisonXRayMode);
        VehicleLod.TransferAnimationState(prevPlayer, nextRig.AnimPlayer, _currentAnimName, prevPlayer?.SpeedScale ?? 1.0f);
        return true;
    }

    // Track travel in model meters: forward distance plus the turn difference between the sides.
    private void AdvanceTracks(float worldDistance, float turn)
    {
        if (_trackGauge <= 0f) return;
        float forward = worldDistance / Model.Scale.X;
        float differential = Mathf.Clamp(turn * _trackGauge * 0.5f, -MaxTurnTrackTravel, MaxTurnTrackTravel);
        _leftTrackTravel = WrapTravel(_leftTrackTravel + forward - differential);
        _rightTrackTravel = WrapTravel(_rightTrackTravel + forward + differential);
        PushTrackTravel();
    }

    // Keeps shader arguments small on long drives. The period is a whole number of track pattern
    // cycles, so links never jump; wheels may snap slightly once per period.
    private float WrapTravel(float travel) =>
        _trackWrap > 0f ? travel - _trackWrap * Mathf.Floor(travel / _trackWrap) : travel;

    private void PushTrackTravel()
    {
        Rig rig = ActiveRig;
        foreach (GeometryInstance3D mesh in rig.LeftMotion)
            mesh.SetInstanceShaderParameter(TrackTravelParameter, _leftTrackTravel);
        foreach (GeometryInstance3D mesh in rig.RightMotion)
            mesh.SetInstanceShaderParameter(TrackTravelParameter, _rightTrackTravel);
    }

    public bool TryGetMuzzleTransform(out Transform3D transform)
    {
        Node3D? muzzle = ActiveRig.Muzzle ?? _muzzle;
        if (muzzle is not null)
        {
            transform = muzzle.GlobalTransform;
            return true;
        }
        transform = default;
        return false;
    }

    public bool TryGetMuzzleTransformForTarget(Vector3 targetWorldPos, out Transform3D transform)
    {
        if (IsHelicopter)
        {
            // CH-47D door gunners fire from port/starboard cabin door windows facing the target.
            Transform3D modelXform = Model.GlobalTransform;
            Vector3 toTarget = targetWorldPos - modelXform.Origin;
            float sideSign = modelXform.Basis.X.Dot(toTarget) >= 0f ? 1f : -1f;
            Vector3 localGunPos = new(sideSign * 1.72f, 2.05f, -2.35f);
            Vector3 worldGunPos = modelXform * localGunPos;
            transform = new Transform3D(modelXform.Basis, worldGunPos);
            return true;
        }
        return TryGetMuzzleTransform(out transform);
    }

    public bool TryGetNextLaunchTubeTransform(out Transform3D transform)
    {
        Node3D[] tubes = ActiveRig.LaunchTubes;
        if (tubes.Length > 0)
        {
            int seqIdx = _nextLaunchTubeCursor % tubes.Length;
            int tubeIdx = tubes.Length == 40 ? BalancedTubeSequence[seqIdx] : seqIdx;
            _nextLaunchTubeCursor++;
            transform = tubes[tubeIdx].GlobalTransform;
            return true;
        }
        return TryGetMuzzleTransform(out transform);
    }

    public void SetGunPitch(float pitchRad)
    {
        if (MathF.Abs(pitchRad - _gunPitchRad) < 0.0001f)
            return;
        _gunPitchRad = pitchRad;
        Vector3 rot = new(pitchRad, 0f, 0f);
        foreach (Rig? rig in _rigs)
        {
            if (rig?.GunPitch is not null)
                rig.GunPitch.Rotation = rot;
        }
    }

    public void ApplyRocketLaunchRecoil(Vector3 shotDirection)
    {
        Vector3 flat = new(shotDirection.X, 0f, shotDirection.Z);
        if (flat.LengthSquared() < 0.0001f) return;
        Vector3 local = flat.Normalized().Rotated(Vector3.Up, -_heading);
        _rockAxis = local.Cross(Vector3.Up).Normalized();
        _rockVelocity += RockImpulse * 0.35f;
    }

    public bool TryGetMuzzlePosition(out Vector3 position)
    {
        Node3D? muzzle = ActiveRig.Muzzle ?? _muzzle;
        if (muzzle is not null)
        {
            position = muzzle.GlobalPosition;
            return true;
        }
        position = default;
        return false;
    }

    public void UpdatePosition(Vector3 position)
    {
        if (_hasPosition)
        {
            Vector3 movement = position - _previousPosition;
            movement.Y = 0;
            if (movement.LengthSquared() > 0.000001f)
            {
                float stepDist = movement.Length();
                float forwardDist = movement.X * (-MathF.Sin(_heading)) + movement.Z * (-MathF.Cos(_heading));
                AdvanceTracks(forwardDist, 0f);
                if (stepDist <= MaxDustStep)
                    _distanceSinceEffects += stepDist;
            }
        }
        _previousPosition = position;
        _hasPosition = true;
        if (_isFastRoping)
        {
            _fastRopeLandingTarget = position;
            return;
        }
        Root.Position = position;
        if (!_ringsDraped || (position - _lastDrapedPosition).LengthSquared() >= MinRedrapeDistanceSquared)
        {
            _ringsDraped = false;
            if (_isSelected)
            {
                DrapeRings();
            }
        }
    }

    /// <summary>
    /// The hull heading comes from the snapshot, like the turret yaw, so a turret that rides on the
    /// hull stays fixed to it on screen. A hull turn runs one track ahead of the other.
    /// </summary>
    public void SetHullHeading(float worldYaw)
    {
        if (_hasHeading && worldYaw == _heading) return;
        float turn = _hasHeading ? Mathf.Wrap(worldYaw - _heading, -Mathf.Pi, Mathf.Pi) : 0f;
        _heading = worldYaw;
        _hasHeading = true;
        ApplyHullPose();
        if (turn != 0f) AdvanceTracks(0f, turn);
    }

    internal float Heading => _heading;

    public void SetTurretYaw(float worldYaw)
    {
        if (_turret is not null || (_visual is not null && !IsInfantry))
        {
            _turretRotation = new Vector3(0, Mathf.Wrap(worldYaw - _heading, -Mathf.Pi, Mathf.Pi), 0);
            _hasAim = true;
            foreach (Rig? rig in _rigs)
            {
                if (rig?.Turret is not null)
                    rig.Turret.Rotation = _turretRotation;
            }
        }
        else if (IsInfantry)
        {
            _turretRotation = new Vector3(0, Mathf.Wrap(worldYaw - _heading, -Mathf.Pi, Mathf.Pi), 0);
            _hasAim = MathF.Abs(Mathf.Wrap(worldYaw - _heading, -Mathf.Pi, Mathf.Pi)) > 0.02f;
            ApplyHullPose();
        }
    }

    public void AimAt(Vector3 observedPosition)
    {
        if (_turret is null && _visual is null && !IsInfantry) return;
        Vector3 direction = observedPosition - _previousPosition;
        if (direction.X * direction.X + direction.Z * direction.Z <= 0.000001f) return;
        float worldYaw = Mathf.Atan2(-direction.X, -direction.Z);
        SetTurretYaw(worldYaw);
    }

    internal Vector3 TurretRotation => ActiveRig.Turret?.Rotation ?? _turret?.Rotation ?? Vector3.Zero;

    public void ApplyRecoil(Vector3 shotDirection, bool isMachineGun = false, bool isRifle = false)
    {
        if (IsInfantry || isRifle)
        {
            SetAiming(true);
            return;
        }
        if (isMachineGun)
        {
            // Machine guns only kick an articulated barrel node (e.g. KPVT on BTR-70/BRDM-2);
            // never shake the entire vehicle or helicopter hull via fallback offset.
            if (_barrelRecoil is not null)
            {
                _recoilAmount = 0.08f;
                SetBarrelRecoil(_recoilAmount);
            }
            return;
        }
        if (IsHelicopter)
            return;
        if (_barrelRecoil is not null)
        {
            _recoilAmount = 0.35f;
            SetBarrelRecoil(_recoilAmount);
            StartRock(shotDirection);
        }
        else
        {
            _fallbackRecoilOffset = -shotDirection.Normalized() * 0.35f;
            Model.Position = _modelBindPosition + new Vector3(0f, _altitude, 0f) + _fallbackRecoilOffset;
        }
    }

    public void UpdateRecoil(double delta)
    {
        float dt = (float)delta;
        if (_barrelRecoil is not null)
        {
            if (_recoilAmount > 0f)
            {
                _recoilAmount = Mathf.MoveToward(_recoilAmount, 0f, dt * 3.5f);
                SetBarrelRecoil(_recoilAmount);
            }
        }
        else if (_fallbackRecoilOffset != Vector3.Zero)
        {
            _fallbackRecoilOffset = _fallbackRecoilOffset.MoveToward(Vector3.Zero, dt * 3.5f);
            Model.Position = _modelBindPosition + new Vector3(0f, _altitude, 0f) + _fallbackRecoilOffset;
        }
        UpdateRock(dt);
    }

    // The recoil pushes the trunnions back above the centre of mass, so the nose lifts and the
    // hull rocks a few times on its suspension. Presentation only.
    private void StartRock(Vector3 shotDirection)
    {
        Vector3 flat = new(shotDirection.X, 0f, shotDirection.Z);
        if (flat.LengthSquared() < 0.0001f) return;
        Vector3 local = flat.Normalized().Rotated(Vector3.Up, -_heading);
        _rockAxis = local.Cross(Vector3.Up).Normalized();
        _rockVelocity += RockImpulse;
    }

    private void UpdateRock(float dt)
    {
        if (_rockAngle == 0f && _rockVelocity == 0f) return;
        float omega = Mathf.Tau * RockFrequencyHz;
        // Small fixed substeps keep the spring stable after long frames.
        float remaining = Mathf.Min(dt, 0.25f);
        while (remaining > 0f)
        {
            float step = Mathf.Min(remaining, 1f / 120f);
            _rockVelocity += (-omega * omega * _rockAngle - 2f * RockDamping * omega * _rockVelocity) * step;
            _rockAngle += _rockVelocity * step;
            remaining -= step;
        }
        if (Mathf.Abs(_rockAngle) < 0.0002f && Mathf.Abs(_rockVelocity) < 0.002f)
        {
            _rockAngle = 0f;
            _rockVelocity = 0f;
        }
        ApplyHullPose();
    }

    public void UpdateFlightState(
        float altitude,
        float pitchRad,
        float rollRad,
        FlightPhase phase,
        FlightStateFlags flags = FlightStateFlags.None,
        bool hasSlingLoad = false)
    {
        float clampedAlt = MathF.Max(0f, altitude);
        float prevAlt = _altitude;
        bool altChanged = MathF.Abs(clampedAlt - prevAlt) > 0.001f;
        bool poseChanged = MathF.Abs(pitchRad - _pitchRad) > 0.0005f || MathF.Abs(rollRad - _rollRad) > 0.0005f;
        bool flagsChanged = flags != _flightFlags || hasSlingLoad != _hasSlingLoad;
        _altitude = clampedAlt;
        _pitchRad = pitchRad;
        _rollRad = rollRad;
        _flightPhase = phase;
        _flightFlags = flags;
        _hasSlingLoad = hasSlingLoad;

        if (altChanged)
        {
            Model.Position = _modelBindPosition + new Vector3(0f, _altitude, 0f) + _fallbackRecoilOffset;
            float baseBarHeight = IsHelicopter ? 6.5f : (IsInfantry ? 2.1f : 3.4f);
            _healthBarRoot.Position = new Vector3(0f, baseBarHeight + _altitude, 0f);
            bool showAirMarkers = _altitude > 0.35f;
            if (_altitudeTether is not null)
            {
                if (_altitudeTether.Visible != showAirMarkers)
                    _altitudeTether.Visible = showAirMarkers;
                if (showAirMarkers)
                {
                    _altitudeTether.Scale = new Vector3(1f, _altitude, 1f);
                    _altitudeTether.Position = new Vector3(0f, _altitude * 0.5f, 0f);
                }
            }
            if (_groundFootprintRing is not null && _groundFootprintRing.Visible != showAirMarkers)
            {
                _groundFootprintRing.Visible = showAirMarkers;
            }
        }

        if (altChanged || flagsChanged)
        {
            if (_fastRopeRoot is not null)
            {
                bool activeRopes = (_flightFlags & FlightStateFlags.FastRopeActive) != 0 && _altitude > 1.0f;
                if (activeRopes)
                {
                    // Keep ropes visible long enough for the final disembarking trooper to finish sliding down
                    _fastRopeLingerTimer = FastRopeSlideDuration + 0.15f;
                }
                else if (_hasSlingLoad || _altitude <= 1.0f || MathF.Abs(clampedAlt - prevAlt) > 2.0f)
                {
                    _fastRopeLingerTimer = 0f;
                }
                bool showRopes = (activeRopes || _fastRopeLingerTimer > 0f) && _altitude > 1.0f;
                if (_fastRopeRoot.Visible != showRopes)
                    _fastRopeRoot.Visible = showRopes;
                if (showRopes && _fastRopeLeft is not null && _fastRopeRight is not null)
                {
                    // On open ground (~18 m hover) ropes reach ground; over a building roof (> 20 m hover)
                    // terminate ropes cleanly at the rooftop deck (~15.2 m drop from rear ramp).
                    float rampY = _altitude + 1.2f;
                    float ropeHeight = _altitude > 20.0f ? FlightSystem.RoofFastRopeClearanceAboveRoofMeters + 1.2f : rampY;
                    float ropeCenterY = rampY - ropeHeight * 0.5f;
                    // Hang twin braided ropes from the lowered rear ramp of the helicopter
                    Vector3 rearOffset = new Vector3(MathF.Sin(_heading), 0f, MathF.Cos(_heading)) * 5.8f;
                    Vector3 sideOffset = new Vector3(MathF.Cos(_heading), 0f, -MathF.Sin(_heading)) * 0.85f;
                    _fastRopeLeft.Scale = new Vector3(1f, ropeHeight, 1f);
                    _fastRopeLeft.Position = rearOffset - sideOffset + new Vector3(0f, ropeCenterY, 0f);
                    _fastRopeRight.Scale = new Vector3(1f, ropeHeight, 1f);
                    _fastRopeRight.Position = rearOffset + sideOffset + new Vector3(0f, ropeCenterY, 0f);
                }
            }

            if (_slingHarnessRoot is not null)
            {
                bool showSling = _hasSlingLoad && _altitude > 0.5f;
                if (_slingHarnessRoot.Visible != showSling)
                    _slingHarnessRoot.Visible = showSling;
                if (showSling)
                {
                    _slingHarnessRoot.Position = new Vector3(0f, _altitude + 0.6f, 0f);
                    _slingHarnessRoot.Rotation = new Vector3(0f, _heading, 0f);
                }
            }
        }

        if (poseChanged)
        {
            ApplyHullPose();
        }
    }

    public void UpdateSlungState(float altitude, bool isSlung)
    {
        float clampedAlt = isSlung ? MathF.Max(0f, altitude) : 0f;
        bool changed = isSlung != _isSlung || MathF.Abs(clampedAlt - _altitude) > 0.001f;
        _isSlung = isSlung;
        _altitude = clampedAlt;
        if (changed)
        {
            Model.Position = _modelBindPosition + new Vector3(0f, _altitude, 0f) + _fallbackRecoilOffset;
            float baseBarHeight = IsInfantry ? 2.1f : 3.4f;
            _healthBarRoot.Position = new Vector3(0f, baseBarHeight + _altitude, 0f);
            if (!_isSlung && (_pitchRad != 0f || _rollRad != 0f))
            {
                _pitchRad = 0f;
                _rollRad = 0f;
                ApplyHullPose();
            }
        }
    }

    private void ApplyHelicopterRigState(Rig rig)
    {
        // Smooth RPM cross-fade between 3D solid blades and translucent motion-blur discs (no sudden 1-frame snap)
        float blurBlend = Mathf.SmoothStep(6.5f, 16.5f, _rotorSpinSpeed);
        bool showSolid = blurBlend < 0.96f;
        bool showBlur = blurBlend > 0.01f;

        if (rig.FrontRotor is not null)
        {
            if (rig.FrontRotor.Visible != showSolid)
                rig.FrontRotor.Visible = showSolid;
            if (showSolid)
                rig.FrontRotor.Rotation = new Vector3(0f, _rotorAngle, 0f);
        }
        if (rig.RearRotor is not null)
        {
            if (rig.RearRotor.Visible != showSolid)
                rig.RearRotor.Visible = showSolid;
            if (showSolid)
                rig.RearRotor.Rotation = new Vector3(0f, -_rotorAngle, 0f);
        }
        if (rig.FrontRotorBlur is not null)
        {
            if (rig.FrontRotorBlur.Visible != showBlur)
                rig.FrontRotorBlur.Visible = showBlur;
            if (showBlur)
                rig.FrontRotorBlur.Rotation = new Vector3(0f, _rotorAngle * 0.40f, 0f);
        }
        if (rig.RearRotorBlur is not null)
        {
            if (rig.RearRotorBlur.Visible != showBlur)
                rig.RearRotorBlur.Visible = showBlur;
            if (showBlur)
                rig.RearRotorBlur.Rotation = new Vector3(0f, -_rotorAngle * 0.40f, 0f);
        }

        if (MathF.Abs(blurBlend - rig.LastBlurBlend) > 0.005f)
        {
            rig.LastBlurBlend = blurBlend;
            float solidTransparency = Mathf.SmoothStep(0.32f, 0.96f, blurBlend);
            float blurTransparency = 1.0f - blurBlend;
            for (int i = 0; i < rig.SolidRotorGeoms.Length; i++)
            {
                rig.SolidRotorGeoms[i].Transparency = solidTransparency;
            }
            for (int i = 0; i < rig.BlurRotorGeoms.Length; i++)
            {
                rig.BlurRotorGeoms[i].Transparency = blurTransparency;
            }
        }

        if (rig.RearRamp is not null)
        {
            rig.RearRamp.Rotation = rig.RearRampBindRotation + new Vector3(_rampAngleRad, 0f, 0f);
        }
    }

    private void ApplyHullPose()
    {
        if (_rockAngle == 0f && _pitchRad == 0f && _rollRad == 0f)
        {
            float yaw = (IsInfantry && (_aimingTimer > 0f || _hasAim) && _dustSpeed < 0.15f)
                ? (_turretRotation.Y + _heading)
                : _heading;
            Model.Rotation = new Vector3(0, yaw, 0);
            return;
        }
        Basis basis = new Basis(Vector3.Up, _heading);
        if (_pitchRad != 0f)
            basis *= new Basis(Vector3.Right, _pitchRad);
        if (_rollRad != 0f)
            basis *= new Basis(Vector3.Forward, _rollRad);
        if (_rockAngle != 0f)
            basis *= new Basis(_rockAxis, _rockAngle);
        Model.Basis = basis;
        Model.Scale = _modelScale;
    }

    // One dust emitter per side along the running gear, in world space so the dust stays behind.
    private void CreateDustIfReady(Rig rig)
    {
        if (_dustCreated || (rig.LeftMotion.Length == 0 && rig.RightMotion.Length == 0)) return;
        _dustCreated = true;
        for (int side = 0; side < 2; side++)
        {
            GeometryInstance3D[] meshes = side == 0 ? rig.LeftMotion : rig.RightMotion;
            if (meshes.Length == 0) continue;
            Aabb bounds = default;
            bool first = true;
            foreach (GeometryInstance3D mesh in meshes)
            {
                Aabb box = TransformTo(mesh, Model) * mesh.GetAabb();
                bounds = first ? box : bounds.Merge(box);
                first = false;
            }
            Vector3 center = bounds.GetCenter();
            GpuParticles3D dust = ParticleEffects.CreateMovementDust(new Vector3(bounds.Size.X * 0.5f, 0.05f, bounds.Size.Z * 0.5f));
            dust.Position = new Vector3(center.X, bounds.Position.Y + 0.4f, center.Z);
            Model.AddChild(dust);
            _dust[side] = dust;
        }
    }

    private static Transform3D TransformTo(Node3D node, Node3D ancestor)
    {
        Transform3D result = Transform3D.Identity;
        Node? current = node;
        while (current is Node3D spatial && current != ancestor)
        {
            result = spatial.Transform * result;
            current = current.GetParent();
        }
        return result;
    }

    // Dust follows the smoothed speed from position updates; it stops on the distant zoom.
    internal void UpdateEffects(double delta)
    {
        float dt = (float)delta;
        if (dt <= 0f) return;
        float speed = _distanceSinceEffects / dt;
        _distanceSinceEffects = 0f;
        _dustSpeed = Mathf.Lerp(_dustSpeed, speed, 1f - Mathf.Exp(-6f * dt));
        float ratio = Mathf.Clamp(_dustSpeed / FullDustSpeed, 0f, 1f);
        bool show = ratio > 0.05f && _activeLod < 2 && !IsInfantry;
        foreach (GpuParticles3D? dust in _dust)
        {
            if (dust is null) continue;
            // Each write is a RenderingServer call, so skip unchanged values.
            if (show && Mathf.Abs(dust.AmountRatio - ratio) > 0.02f)
                dust.AmountRatio = ratio;
            if (dust.Emitting != show)
                dust.Emitting = show;
        }

        if (_aimingTimer > 0f)
        {
            _aimingTimer = MathF.Max(0f, _aimingTimer - dt);
        }

        if (IsHelicopter)
        {
            float targetSpin = _flightPhase switch
            {
                FlightPhase.Ground => 2.8f,
                FlightPhase.Crashing => 14.0f,
                _ => 24.0f
            };
            float spoolRate = _flightPhase == FlightPhase.Ground ? 1.8f : 2.3f;
            _rotorSpinSpeed = Mathf.Lerp(_rotorSpinSpeed, targetSpin, 1f - Mathf.Exp(-spoolRate * dt));
            _rotorAngle = Mathf.Wrap(_rotorAngle + _rotorSpinSpeed * dt, 0f, Mathf.Tau);

            bool rampOpen = _flightPhase == FlightPhase.Ground || (_flightFlags & FlightStateFlags.FastRopeActive) != 0 || _fastRopeLingerTimer > 0f;
            if ((_flightFlags & FlightStateFlags.FastRopeActive) == 0 && _fastRopeLingerTimer > 0f)
            {
                _fastRopeLingerTimer = MathF.Max(0f, _fastRopeLingerTimer - dt);
                if (_fastRopeLingerTimer <= 0f && _fastRopeRoot is not null && _fastRopeRoot.Visible)
                {
                    _fastRopeRoot.Visible = false;
                }
            }
            float targetRampRad = rampOpen ? Mathf.DegToRad(42f) : 0f;
            _rampAngleRad = Mathf.MoveToward(_rampAngleRad, targetRampRad, Mathf.DegToRad(50f) * dt);
            ApplyHelicopterRigState(ActiveRig);

            // Rotor downwash dust on terrain below low-hovering / landing / fast-roping helicopters
            if (_downwashDust is not null)
            {
                bool showDownwash = _flightPhase != FlightPhase.Ground && _altitude < 22.0f && _activeLod < 2;
                if (showDownwash)
                {
                    float washRatio = Mathf.Clamp(1.0f - (_altitude / 22.0f), 0.15f, 1.0f);
                    if (Mathf.Abs(_downwashDust.AmountRatio - washRatio) > 0.04f)
                        _downwashDust.AmountRatio = washRatio;
                }
                if (_downwashDust.Emitting != showDownwash)
                    _downwashDust.Emitting = showDownwash;
            }

            if (_flightPhase == FlightPhase.Crashing)
            {
                if (_crashSmoke is null)
                {
                    _crashSmoke = ParticleEffects.CreateMovementDust(new Vector3(0.8f, 0.8f, 1.2f));
                    _crashSmoke.Position = new Vector3(0f, 3.2f, 2.5f);
                    Model.AddChild(_crashSmoke);
                }
                if (!_crashSmoke.Emitting)
                {
                    _crashSmoke.AmountRatio = 1.0f;
                    _crashSmoke.Emitting = true;
                }
            }
            else if (_crashSmoke is not null && _crashSmoke.Emitting)
            {
                _crashSmoke.Emitting = false;
            }
        }
        else if (_isSlung && _altitude > 0.5f)
        {
            // Subtle damped pendulum sway for externally slung cargo vehicles
            _swayPhase = Mathf.Wrap(_swayPhase + dt * 2.1f, 0f, Mathf.Tau);
            _pitchRad = Mathf.Sin(_swayPhase) * 0.045f;
            _rollRad = Mathf.Cos(_swayPhase * 0.85f) * 0.055f;
            ApplyHullPose();
        }

        if (_isFastRoping)
        {
            UpdateFastRopeDescent(dt);
        }

        if (_isGarrisonedVisual)
        {
            _dustSpeed = (_garrisonIsRunning || (_isFastRoping && _fastRopeElapsed >= FastRopeSlideDuration)) ? 4.5f : 0f;
        }

        AnimationPlayer? animPlayer = ActiveRig.AnimPlayer;
        if (animPlayer is not null)
        {
            StringName targetAnimName;
            string targetAnimStr;
            float authoredSpeed = 3.0f;
            if (_dustSpeed >= 0.15f)
            {
                if (animPlayer.HasAnimation(AnimRun))
                {
                    targetAnimName = AnimRun;
                    targetAnimStr = "Run";
                    authoredSpeed = 3.0f;
                }
                else
                {
                    targetAnimName = AnimWalk;
                    targetAnimStr = "Walk";
                    authoredSpeed = 1.5f;
                }
            }
            else if (_aimingTimer > 0f || _hasAim)
            {
                targetAnimName = AnimAimStanding;
                targetAnimStr = "AimStanding";
            }
            else
            {
                targetAnimName = AnimIdle;
                targetAnimStr = "Idle";
            }

            if (_currentAnimName != targetAnimName && animPlayer.HasAnimation(targetAnimName))
            {
                animPlayer.Play(targetAnimName);
                _currentAnimName = targetAnimName;
                _currentAnim = targetAnimStr;
            }

            if (targetAnimName == AnimRun)
            {
                animPlayer.SpeedScale = Math.Clamp(_dustSpeed / authoredSpeed, 0.70f, 1.50f);
            }
            else if (targetAnimName == AnimWalk)
            {
                animPlayer.SpeedScale = Math.Clamp(_dustSpeed / authoredSpeed, 0.50f, 1.50f);
            }
            else
            {
                animPlayer.SpeedScale = 1.0f;
            }
        }
    }

    public void ResetRecoil()
    {
        _rockAngle = 0f;
        _rockVelocity = 0f;
        ApplyHullPose();
        _recoilAmount = 0f;
        if (_barrelRecoil is not null)
        {
            SetBarrelRecoil(0f);
        }
        _fallbackRecoilOffset = Vector3.Zero;
        Model.Position = _modelBindPosition + new Vector3(0f, _altitude, 0f);
    }

    private void SetBarrelRecoil(float amount)
    {
        foreach (Rig? rig in _rigs)
        {
            if (rig?.BarrelRecoil is not null)
                rig.BarrelRecoil.Position = rig.BarrelBindPosition + new Vector3(0, 0, amount);
        }
    }

    public void UpdateHealth(int health, int maxHealth)
    {
        if (maxHealth <= 0) return;
        float ratio = Mathf.Clamp(health / (float)maxHealth, 0f, 1f);
        _healthBarFg.Scale = new Vector3(ratio, 1f, 1f);
        _healthBarFg.Position = new Vector3(-0.9f * (1f - ratio), 0, 0.03f);
    }

    public float HealthBarScaleX => _healthBarFg.Scale.X;

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        _selection.Visible = selected;
        _weaponRangeRing.Visible = selected && _rangeIndicatorsVisible && _weaponRange > 0f;
        _sensorRangeRing.Visible = selected && _rangeIndicatorsVisible && _sensorRange > 0f;
        if (selected && _rangeIndicatorsVisible && (!_ringsDraped || (Root.Position - _lastDrapedPosition).LengthSquared() >= MinRedrapeDistanceSquared))
        {
            DrapeRings();
        }
    }

    internal void SetRangeIndicatorsVisible(bool visible)
    {
        if (_rangeIndicatorsVisible == visible) return;
        _rangeIndicatorsVisible = visible;
        SetSelected(_isSelected);
    }

    private static Shader? s_garrisonXRayShader;
    private static ShaderMaterial? s_garrisonXRayIdle;
    private static ShaderMaterial? s_garrisonXRaySelected;
    private static ShaderMaterial? s_garrisonXRayCqb;
    private static ShaderMaterial? s_garrisonXRayHostileCqb;

    internal static ShaderMaterial? GetGarrisonXRayMaterial(GarrisonXRayMode mode)
    {
        if (mode == GarrisonXRayMode.None) return null;
        s_garrisonXRayShader ??= new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, depth_draw_never, depth_test_disabled, cull_back;

                uniform vec4 xray_color : source_color = vec4(0.28, 0.90, 0.80, 0.45);
                uniform sampler2D depth_tex : hint_depth_texture, filter_nearest;

                void fragment() {
                    float scene_depth = texture(depth_tex, SCREEN_UV).r;
                    vec3 ndc_scene = vec3(SCREEN_UV * 2.0 - 1.0, scene_depth);
                    vec4 view_scene = INV_PROJECTION_MATRIX * vec4(ndc_scene, 1.0);
                    float scene_dist = -(view_scene.z / view_scene.w);

                    vec3 ndc_frag = vec3(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z);
                    vec4 view_frag = INV_PROJECTION_MATRIX * vec4(ndc_frag, 1.0);
                    float frag_dist = -(view_frag.z / view_frag.w);

                    // Render X-Ray silhouette only when occluded behind a wall/roof (>= 2.0 m closer to camera,
                    // strictly above soldier body height so unoccluded soldiers never self-highlight)
                    if (frag_dist <= scene_dist + 2.0) {
                        discard;
                    }

                    float fresnel = pow(1.0 - clamp(abs(dot(normalize(NORMAL), normalize(VIEW))), 0.0, 1.0), 1.3);
                    ALBEDO = xray_color.rgb;
                    ALPHA = clamp(xray_color.a * (0.55 + 0.65 * fresnel), 0.14, 0.88);
                }
                """
        };
        return mode switch
        {
            GarrisonXRayMode.Idle => s_garrisonXRayIdle ??= CreateXRayMat(new Color(0.28f, 0.90f, 0.80f, 0.44f)),
            GarrisonXRayMode.Selected => s_garrisonXRaySelected ??= CreateXRayMat(new Color(0.42f, 1.00f, 0.88f, 0.72f)),
            GarrisonXRayMode.Cqb => s_garrisonXRayCqb ??= CreateXRayMat(new Color(1.00f, 0.54f, 0.18f, 0.76f)),
            GarrisonXRayMode.HostileCqb => s_garrisonXRayHostileCqb ??= CreateXRayMat(new Color(1.00f, 0.20f, 0.16f, 0.82f)),
            _ => null
        };

        static ShaderMaterial CreateXRayMat(Color color)
        {
            ShaderMaterial mat = new()
            {
                Shader = s_garrisonXRayShader,
                RenderPriority = 10
            };
            mat.SetShaderParameter("xray_color", color);
            return mat;
        }
    }

    internal bool TryGetFastRopeWorldEndpoints(bool leftRope, out Vector3 topWorldPos, out Vector3 bottomWorldPos)
    {
        if (IsHelicopter && AreFastRopesVisible && _fastRopeLeft is not null && _fastRopeRight is not null)
        {
            MeshInstance3D rope = leftRope ? _fastRopeLeft : _fastRopeRight;
            Vector3 ropeCenter = Root.Position + rope.Position;
            float halfH = MathF.Max(1.0f, rope.Scale.Y * 0.5f);
            topWorldPos = new Vector3(ropeCenter.X, ropeCenter.Y + halfH - 1.0f, ropeCenter.Z);
            bottomWorldPos = new Vector3(ropeCenter.X, ropeCenter.Y - halfH, ropeCenter.Z);
            return true;
        }
        topWorldPos = default;
        bottomWorldPos = default;
        return false;
    }

    internal void StartFastRopeDescent(Vector3 ropeTop, Vector3 ropeBottom, Vector3 landingTarget, float yawRad)
    {
        _isFastRoping = true;
        _fastRopeTop = ropeTop;
        _fastRopeBottom = ropeBottom;
        _fastRopeLandingTarget = landingTarget;
        _fastRopeElapsed = 0f;
        _fastRopeYaw = yawRad;
        _altitude = 0f;
        _pitchRad = -0.18f; // Slight lean back onto the rope during descent
        _rollRad = 0f;
        _rockAngle = 0f;
        _rockVelocity = 0f;
        _heading = yawRad;
        _hasHeading = true;
        Root.Position = ropeTop;
        Model.Position = _modelBindPosition;
        ApplyHullPose();
        if (!Root.Visible)
        {
            Root.Visible = true;
        }
    }

    private void UpdateFastRopeDescent(float dt)
    {
        _fastRopeElapsed += dt;
        float totalDuration = FastRopeSlideDuration + FastRopeStepOffDuration;
        if (_fastRopeElapsed >= totalDuration)
        {
            _isFastRoping = false;
            _pitchRad = 0f;
            _rollRad = 0f;
            Root.Position = _fastRopeLandingTarget;
            _previousPosition = _fastRopeLandingTarget;
            ApplyHullPose();
            return;
        }

        if (_fastRopeElapsed < FastRopeSlideDuration)
        {
            float t = _fastRopeElapsed / FastRopeSlideDuration;
            // Fast initial drop down the rope with controlled braking right before touchdown
            float ease = t < 0.75f
                ? (t / 0.75f) * (t / 0.75f) * 0.82f
                : 0.82f + (1f - MathF.Pow(1f - (t - 0.75f) / 0.25f, 2f)) * 0.18f;
            Vector3 ropePos = _fastRopeTop.Lerp(_fastRopeBottom, Math.Clamp(ease, 0f, 1f));
            Root.Position = ropePos;
            _pitchRad = -0.20f * (1f - t * 0.6f);
            _heading = _fastRopeYaw;
            ApplyHullPose();
            _dustSpeed = 0f;
        }
        else
        {
            float t = (_fastRopeElapsed - FastRopeSlideDuration) / FastRopeStepOffDuration;
            float smooth = t * t * (3f - 2f * t);
            Vector3 pos = _fastRopeBottom.Lerp(_fastRopeLandingTarget, Math.Clamp(smooth, 0f, 1f));
            Root.Position = pos;
            Vector3 runDir = _fastRopeLandingTarget - _fastRopeBottom;
            if (runDir.X * runDir.X + runDir.Z * runDir.Z > 0.01f)
            {
                _heading = MathF.Atan2(-runDir.X, -runDir.Z);
            }
            _pitchRad = 0f;
            ApplyHullPose();
            _dustSpeed = 4.5f;
        }
    }

    private static void ApplyGarrisonXRayToRig(Rig? rig, GarrisonXRayMode mode)
    {
        if (rig is null) return;
        ShaderMaterial? mat = GetGarrisonXRayMaterial(mode);
        GeometryInstance3D[] meshes = rig.AllMeshes;
        for (int i = 0; i < meshes.Length; i++)
        {
            if (meshes[i].MaterialOverlay != mat)
            {
                meshes[i].MaterialOverlay = mat;
            }
        }
    }

    internal void ApplyGarrisonPresentation(
        Vector3 worldPos,
        float yawRad,
        bool isRunning,
        bool isAiming,
        bool isRoof,
        bool isSelected,
        GarrisonXRayMode xrayMode)
    {
        _isGarrisonedVisual = true;
        _garrisonIsRunning = isRunning;
        _altitude = 0f;
        _rockAngle = 0f;
        _rockVelocity = 0f;
        _turretRotation = Vector3.Zero;
        _hasAim = isAiming;
        _previousPosition = worldPos;
        _hasPosition = true;
        _distanceSinceEffects = 0f;

        if (_isFastRoping)
        {
            _fastRopeLandingTarget = worldPos;
        }
        else
        {
            _pitchRad = 0f;
            _rollRad = 0f;
            _heading = yawRad;
            _hasHeading = true;
            _dustSpeed = isRunning ? 4.5f : 0f;
            Root.Position = worldPos;
            Model.Position = _modelBindPosition;
            Model.Rotation = new Vector3(0f, yawRad, 0f);
        }

        if (_healthBarRoot.Visible)
        {
            _healthBarRoot.Visible = false;
        }
        _isSelected = isSelected;
        bool showGroundRing = isSelected && isRoof && !_isFastRoping;
        if (_selection.Visible != showGroundRing)
        {
            _selection.Visible = showGroundRing;
        }
        bool showWeaponRange = isSelected && _rangeIndicatorsVisible && _weaponRange > 0f;
        bool showSensorRange = isSelected && _rangeIndicatorsVisible && _sensorRange > 0f;
        if (_weaponRangeRing.Visible != showWeaponRange) _weaponRangeRing.Visible = showWeaponRange;
        if (_sensorRangeRing.Visible != showSensorRange) _sensorRangeRing.Visible = showSensorRange;
        if (isSelected && _rangeIndicatorsVisible && (!_ringsDraped || (Root.Position - _lastDrapedPosition).LengthSquared() >= MinRedrapeDistanceSquared))
        {
            DrapeRings();
        }

        GarrisonXRayMode effectiveXRay = (_isFastRoping || isRoof) ? GarrisonXRayMode.None : xrayMode;
        if (_garrisonXRayMode != effectiveXRay)
        {
            _garrisonXRayMode = effectiveXRay;
            ApplyGarrisonXRayToRig(ActiveRig, effectiveXRay);
        }

        if (!Root.Visible)
        {
            Root.Visible = true;
        }
    }

    internal void ClearGarrisonPresentation()
    {
        if (!_isGarrisonedVisual) return;
        _isGarrisonedVisual = false;
        _garrisonIsRunning = false;
        if (_garrisonXRayMode != GarrisonXRayMode.None)
        {
            _garrisonXRayMode = GarrisonXRayMode.None;
            for (int i = 0; i < _rigs.Length; i++)
            {
                ApplyGarrisonXRayToRig(_rigs[i], GarrisonXRayMode.None);
            }
        }
        if (!_healthBarRoot.Visible)
        {
            _healthBarRoot.Visible = true;
        }
        SetSelected(_isSelected);
    }

    internal void SetContainedHidden()
    {
        Root.Visible = false;
        if (_isSelected)
        {
            SetSelected(false);
        }
        _hasPosition = false;
        _distanceSinceEffects = 0f;
        _dustSpeed = 0f;
        foreach (GpuParticles3D? dust in _dust)
        {
            if (dust is not null && dust.Emitting)
            {
                dust.Emitting = false;
            }
        }
        AnimationPlayer? animPlayer = ActiveRig.AnimPlayer;
        if (animPlayer is not null && _currentAnimName != AnimIdle && animPlayer.HasAnimation(AnimIdle))
        {
            animPlayer.Play(AnimIdle);
            animPlayer.SpeedScale = 1.0f;
            _currentAnimName = AnimIdle;
            _currentAnim = "Idle";
        }
    }
}
