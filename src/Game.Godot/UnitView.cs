using Godot;

namespace DeadGround.Client;

internal sealed class UnitView
{
    // Orthographic camera sizes where the client switches to LOD1 and LOD2.
    internal const float Lod1CameraSize = 45f;
    internal const float Lod2CameraSize = 95f;
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

    // Animated nodes of one LOD. Every LOD follows the same turret, gun and recoil state.
    private sealed class Rig
    {
        internal Node3D? Turret;
        internal Node3D? GunPitch;
        internal Node3D? BarrelRecoil;
        internal Node3D? Muzzle;
        internal Vector3 BarrelBindPosition;
        internal GeometryInstance3D[] LeftMotion = [];
        internal GeometryInstance3D[] RightMotion = [];
    }

    public Node3D Root { get; } = new();
    private readonly MeshInstance3D _selection;
    private readonly Node3D? _turret;
    private readonly VehicleVisual? _visual;
    // One rig per LOD, created when that LOD is first shown.
    private readonly Rig?[] _rigs;
    private readonly float _trackGauge;
    private readonly float _trackWrap;
    private int _activeLod;
    private Vector3 _turretRotation;
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
    internal string? UnitId { get; }
    internal bool HasVehicleModel { get; }
    internal int ActiveLod => _activeLod;
    internal int LodCount => _rigs.Length;
    internal float LeftTrackTravel => _leftTrackTravel;
    internal float RightTrackTravel => _rightTrackTravel;
    internal float RockAngle => _rockAngle;
    internal GpuParticles3D? LeftDust => _dust[0];
    internal float DustSpeed => _dustSpeed;

    public UnitView(ushort definition = 0) : this(UnitViewSet.GetDefaultUnitId(definition)) { }

    public UnitView(string? unitId, string? faction = null)
    {
        UnitId = unitId;
        VehicleVisual? visual = VehicleVisualCatalog.InstantiateVisual(unitId, faction);
        HasVehicleModel = visual is not null;
        Model = visual?.Root ?? new Node3D { Name = "PlaceholderModel" };
        _visual = visual;
        _trackGauge = visual?.TrackGauge ?? 0f;
        _trackWrap = visual?.TrackWrap ?? 0f;
        Root.AddChild(Model);
        _rigs = new Rig?[visual?.LodCount ?? 1];
        Rig lod0 = CreateRig(visual?.GetOrCreateLod(0) ?? Model);
        _rigs[0] = lod0;
        _turret = lod0.Turret;
        _gunPitch = lod0.GunPitch;
        _barrelRecoil = lod0.BarrelRecoil;
        _muzzle = lod0.Muzzle;
        _barrelBindPosition = lod0.BarrelBindPosition;
        _modelBindPosition = Model.Position;
        _modelScale = Model.Scale;
        CreateDust(lod0);
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
            Mesh = new TorusMesh { InnerRadius = 2.5f, OuterRadius = 2.8f, Rings = 24, RingSegments = 8 },
            Scale = new Vector3(1, 0.15f, 1),
            Position = new Vector3(0, 0.15f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("edc76b"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        Root.AddChild(_selection);

        _healthBarBg = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.8f, 0.12f, 0.12f) },
            Position = new Vector3(0, 3.4f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("1a1a1a"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        Root.AddChild(_healthBarBg);

        _healthBarFg = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.8f, 0.12f, 0.14f) },
            Position = new Vector3(0, 3.4f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("44bb44"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        Root.AddChild(_healthBarFg);

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
        AlbedoColor = new Color(0.9294118f, 0.7803922f, 0.41960785f, 0.8f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };

    private static readonly StandardMaterial3D SensorRangeMaterial = new()
    {
        AlbedoColor = new Color(0.60784316f, 0.76862746f, 0.8862745f, 0.35f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled
    };

    private readonly MeshInstance3D _healthBarBg;
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
            _weaponRangeRing.Visible = _weaponRange > 0f;
            _sensorRangeRing.Visible = _sensorRange > 0f;
            DrapeRings();
        }
    }

    private void DrapeRings()
    {
        if (!_isSelected) return;
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

        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, material);
        const int segments = 72;
        const float step = Mathf.Tau / segments;
        float cx = Root.Position.X;
        float cy = Root.Position.Y;
        float cz = Root.Position.Z;

        for (int i = 0; i < segments; i++)
        {
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
                mesh.SurfaceAddVertex(new Vector3(cwx0 - cx, wy0 - cy, cwz0 - cz));
                mesh.SurfaceAddVertex(new Vector3(cwx1 - cx, wy1 - cy, cwz1 - cz));
            }
            else
            {
                mesh.SurfaceAddVertex(new Vector3(wx0 - cx, 0.12f, wz0 - cz));
                mesh.SurfaceAddVertex(new Vector3(wx1 - cx, 0.12f, wz1 - cz));
            }
        }
        mesh.SurfaceEnd();
    }
    private readonly Node3D? _gunPitch;
    private readonly Node3D? _barrelRecoil;
    private readonly Node3D? _muzzle;
    private readonly Vector3 _barrelBindPosition;
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
        };
        rig.BarrelBindPosition = rig.BarrelRecoil?.Position ?? Vector3.Zero;
        rig.LeftMotion = FindMotionMeshes(lod.FindChild("LeftTrack", recursive: true, owned: false));
        rig.RightMotion = FindMotionMeshes(lod.FindChild("RightTrack", recursive: true, owned: false));
        return rig;
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

    private Rig ActiveRig => _rigs[_activeLod]!;

    private Node3D LodNode(int lod) => _visual?.GetOrCreateLod(lod) ?? Model;

    // A LOD shown for the first time picks up the current aim and recoil.
    private Rig EnsureRig(int lod)
    {
        Rig? rig = _rigs[lod];
        if (rig is not null) return rig;
        rig = CreateRig(LodNode(lod));
        if (_hasAim && rig.Turret is not null)
            rig.Turret.Rotation = _turretRotation;
        if (rig.BarrelRecoil is not null)
            rig.BarrelRecoil.Position = rig.BarrelBindPosition + new Vector3(0, 0, _recoilAmount);
        _rigs[lod] = rig;
        return rig;
    }

    internal bool IsLodLoaded(int index) => _rigs[index] is not null;

    internal Node3D? GetLod(int index) => _rigs[index] is null ? null : LodNode(index);
    internal int LeftMotionMeshCount => ActiveRig.LeftMotion.Length;
    internal int RightMotionMeshCount => ActiveRig.RightMotion.Length;

    // The travel value the shader sees on the active LOD, for regression checks.
    internal float GetShaderTrackTravel(bool left)
    {
        GeometryInstance3D[] meshes = left ? ActiveRig.LeftMotion : ActiveRig.RightMotion;
        return meshes.Length == 0 ? float.NaN : meshes[0].GetInstanceShaderParameter(TrackTravelParameter).AsSingle();
    }

    internal void UpdateLod(float cameraSize)
    {
        int lod = cameraSize < Lod1CameraSize ? 0 : cameraSize < Lod2CameraSize ? 1 : 2;
        lod = Math.Min(lod, _rigs.Length - 1);
        if (lod == _activeLod) return;
        EnsureRig(lod);
        LodNode(_activeLod).Visible = false;
        LodNode(lod).Visible = true;
        _activeLod = lod;
        PushTrackTravel();
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
                float heading = Mathf.Atan2(-movement.X, -movement.Z);
                float turn = _hasHeading ? Mathf.Wrap(heading - _heading, -Mathf.Pi, Mathf.Pi) : 0f;
                _heading = heading;
                ApplyHullPose();
                _hasHeading = true;
                AdvanceTracks(movement.Length(), turn);
                if (movement.Length() <= MaxDustStep)
                    _distanceSinceEffects += movement.Length();
            }
        }
        _previousPosition = position;
        _hasPosition = true;
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

    public void AimAt(Vector3 observedPosition)
    {
        if (_turret is null) return;
        Vector3 direction = observedPosition - _previousPosition;
        if (direction.X * direction.X + direction.Z * direction.Z <= 0.000001f) return;
        float worldYaw = Mathf.Atan2(-direction.X, -direction.Z);
        _turretRotation = new Vector3(0, Mathf.Wrap(worldYaw - _heading, -Mathf.Pi, Mathf.Pi), 0);
        _hasAim = true;
        foreach (Rig? rig in _rigs)
        {
            if (rig?.Turret is not null)
                rig.Turret.Rotation = _turretRotation;
        }
    }

    internal Vector3 TurretRotation => _turret?.Rotation ?? Vector3.Zero;

    public void ApplyRecoil(Vector3 shotDirection)
    {
        if (_barrelRecoil is not null)
        {
            _recoilAmount = 0.35f;
            SetBarrelRecoil(_recoilAmount);
            StartRock(shotDirection);
        }
        else
        {
            _fallbackRecoilOffset = -shotDirection.Normalized() * 0.35f;
            Model.Position = _modelBindPosition + _fallbackRecoilOffset;
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
            Model.Position = _modelBindPosition + _fallbackRecoilOffset;
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

    private void ApplyHullPose()
    {
        if (_rockAngle == 0f)
        {
            Model.Rotation = new Vector3(0, _heading, 0);
            return;
        }
        Model.Basis = new Basis(Vector3.Up, _heading) * new Basis(_rockAxis, _rockAngle);
        Model.Scale = _modelScale;
    }

    // One dust emitter per side along the running gear, in world space so the dust stays behind.
    private void CreateDust(Rig lod0)
    {
        for (int side = 0; side < 2; side++)
        {
            GeometryInstance3D[] meshes = side == 0 ? lod0.LeftMotion : lod0.RightMotion;
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
        bool show = ratio > 0.05f && _activeLod < 2;
        foreach (GpuParticles3D? dust in _dust)
        {
            if (dust is null) continue;
            // Each write is a RenderingServer call, so skip unchanged values.
            if (show && Mathf.Abs(dust.AmountRatio - ratio) > 0.02f)
                dust.AmountRatio = ratio;
            if (dust.Emitting != show)
                dust.Emitting = show;
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
        Model.Position = _modelBindPosition;
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
        _healthBarFg.Position = new Vector3(-0.9f * (1f - ratio), 3.4f, 0);
    }

    public float HealthBarScaleX => _healthBarFg.Scale.X;

    public void SetSelected(bool selected)
    {
        _isSelected = selected;
        _selection.Visible = selected;
        _weaponRangeRing.Visible = selected && _weaponRange > 0f;
        _sensorRangeRing.Visible = selected && _sensorRange > 0f;
        if (selected && (!_ringsDraped || (Root.Position - _lastDrapedPosition).LengthSquared() >= MinRedrapeDistanceSquared))
        {
            DrapeRings();
        }
    }
}
