using Godot;

namespace DeadGround.Client;

internal sealed class UnitView
{
    // Orthographic camera sizes where the client switches to LOD1 and LOD2.
    internal const float Lod1CameraSize = 45f;
    internal const float Lod2CameraSize = 95f;
    // A heading snap must not spin the tracks by more than this, in model meters.
    private const float MaxTurnTrackTravel = 0.5f;
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
    private Vector3 _previousPosition;
    private bool _hasPosition;
    internal Node3D Model { get; }
    internal bool HasVehicleModel { get; }
    internal int ActiveLod => _activeLod;
    internal int LodCount => _rigs.Length;
    internal float LeftTrackTravel => _leftTrackTravel;
    internal float RightTrackTravel => _rightTrackTravel;

    public UnitView(ushort definition = 0) : this(UnitViewSet.GetDefaultUnitId(definition)) { }

    public UnitView(string? unitId, string? faction = null)
    {
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
    }

    private readonly MeshInstance3D _healthBarBg;
    private readonly MeshInstance3D _healthBarFg;
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
                float turn = _hasHeading ? Mathf.Wrap(heading - Model.Rotation.Y, -Mathf.Pi, Mathf.Pi) : 0f;
                Model.Rotation = new Vector3(0, heading, 0);
                _hasHeading = true;
                AdvanceTracks(movement.Length(), turn);
            }
        }
        _previousPosition = position;
        _hasPosition = true;
        Root.Position = position;
    }

    public void AimAt(Vector3 observedPosition)
    {
        if (_turret is null) return;
        Vector3 direction = observedPosition - _previousPosition;
        if (direction.X * direction.X + direction.Z * direction.Z <= 0.000001f) return;
        float worldYaw = Mathf.Atan2(-direction.X, -direction.Z);
        _turretRotation = new Vector3(0, Mathf.Wrap(worldYaw - Model.Rotation.Y, -Mathf.Pi, Mathf.Pi), 0);
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
    }

    public void ResetRecoil()
    {
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

    public void SetSelected(bool selected) => _selection.Visible = selected;
}
