using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal sealed class ContactView
{
    public Node3D Root { get; } = new();
    public ContactObservation Observation { get; private set; }

    private static readonly StringName AnimIdle = "Idle";
    private static readonly StringName AnimWalk = "Walk";
    private static readonly StringName AnimRun = "Run";
    private static readonly StringName AnimAimStanding = "AimStanding";

    private sealed record ContactRig(
        Node3D? Turret,
        Node3D? Muzzle,
        AnimationPlayer? AnimPlayer,
        Node3D? FrontRotor,
        Node3D? RearRotor,
        Node3D? FrontRotorBlur,
        Node3D? RearRotorBlur,
        Node3D? RearRamp);

    private readonly MeshInstance3D _ghostBase;
    private readonly MeshInstance3D _selection;

    private Node3D? _enemyModel;
    private VehicleVisual? _visual;
    private ContactRig?[] _rigs = [];
    private int _activeLod;
    // Found once per model LOD: a tree search per frame and per shot would cost every visible contact.
    private Node3D? _turret;
    private Node3D? _muzzle;
    private AnimationPlayer? _animPlayer;
    private Node3D? _frontRotor;
    private Node3D? _rearRotor;
    private Node3D? _frontRotorBlur;
    private Node3D? _rearRotorBlur;
    private Node3D? _rearRamp;
    private float _altitude;
    private float _rotorAngle;
    private string _currentAnim = "";
    private StringName? _currentAnimName;
    private float _aimingTimer;
    private float _relativeTurretYaw;
    private float? _lastCameraSize;
    private bool _hasPendingLodWhileHidden;
    private Vector3 _prevPos;
    private bool _hasPrevPos;
    private ushort _currentModelType = ushort.MaxValue;
    private ContactClassification _currentModelClassification = ContactClassification.Unknown;

    internal Node3D? EnemyModel => _enemyModel;
    internal bool HasEnemyModel => _enemyModel is not null && _enemyModel.Visible;
    internal int ActiveLod => _activeLod;
    internal int LodCount => _visual?.LodCount ?? (_enemyModel is not null ? 1 : 0);
    internal bool IsLodLoaded(int index) => _visual is not null ? _visual.IsLoaded(index) : index == 0 && _enemyModel is not null;
    internal Node3D? GetLod(int index) => _visual is not null ? _visual.GetLoadedLod(index) : index == 0 ? _enemyModel : null;
    internal MeshInstance3D GhostBase => _ghostBase;
    internal ushort CurrentModelType => _currentModelType;
    internal ContactClassification CurrentModelClassification => _currentModelClassification;
    internal string CurrentAnimation => _currentAnim;
    internal AnimationPlayer? AnimationPlayer => _animPlayer;
    internal float Altitude => _altitude;

    public void SetAiming(bool aiming)
    {
        if (aiming) _aimingTimer = 0.8f;
    }

    public void PlayAnimation(string clipName)
    {
        StringName name = clipName;
        if (_animPlayer is not null && _animPlayer.HasAnimation(name))
        {
            _animPlayer.Play(name);
            _currentAnim = clipName;
            _currentAnimName = name;
        }
    }

    private readonly Func<string?>? _hostileFaction;
    private readonly Func<ushort, string?>? _unitIdProvider;

    public ContactView(
        Func<string?>? hostileFaction = null,
        Func<ushort, string?>? unitIdProvider = null)
    {
        _hostileFaction = hostileFaction;
        _unitIdProvider = unitIdProvider;

        // Ground-level base marker for last-known contacts
        _ghostBase = new MeshInstance3D
        {
            Name = "GhostBaseMarker",
            Mesh = new TorusMesh { InnerRadius = 1.3f, OuterRadius = 1.6f, Rings = 16, RingSegments = 8 },
            Scale = new Vector3(1, 0.15f, 1),
            Position = new Vector3(0, 0.1f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("a86658"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true
            },
            Visible = false
        };
        Root.AddChild(_ghostBase);

        // Selection ring (distinct red color)
        _selection = new MeshInstance3D
        {
            Name = "SelectionRing",
            Mesh = new TorusMesh { InnerRadius = 2.4f, OuterRadius = 2.7f, Rings = 24, RingSegments = 8 },
            Scale = new Vector3(1, 0.15f, 1),
            Position = new Vector3(0, 0.15f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color("ff3322"),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        Root.AddChild(_selection);
    }

    internal ContactView(VehicleVisual visual, ContactClassification classification = ContactClassification.Tank)
        : this()
    {
        ArgumentNullException.ThrowIfNull(visual);
        _enemyModel = visual.Root;
        _visual = visual;
        _currentModelClassification = classification;
        _activeLod = visual.ActiveLod;
        _rigs = new ContactRig?[visual.LodCount];
        Root.AddChild(_enemyModel);
        ApplyActiveRig(EnsureRig(_activeLod, classification == ContactClassification.Infantry));
    }

    public void Update(ContactObservation contact, float groundY, bool isSelected) =>
        Update(contact, contact.Position, groundY, isSelected, contact.TurretYaw, contact.HullHeading, contact.Altitude);

    public void Update(ContactObservation contact, System.Numerics.Vector2 position, float groundY, bool isSelected,
        float turretYaw, float hullHeading, float altitude = 0f)
    {
        Observation = contact;
        _altitude = MathF.Max(0f, altitude);
        Vector3 newPos = new(position.X, groundY, position.Y);
        float speed = 0f;
        if (_hasPrevPos)
        {
            Vector3 deltaPos = newPos - _prevPos;
            deltaPos.Y = 0;
            speed = deltaPos.Length() / Simulation.TickSeconds;
        }
        _prevPos = newPos;
        _hasPrevPos = true;
        Root.Position = newPos;

        if (_aimingTimer > 0f)
        {
            _aimingTimer = MathF.Max(0f, _aimingTimer - Simulation.TickSeconds);
        }

        bool isInfantry = contact.Classification == ContactClassification.Infantry;
        bool isHelicopter = contact.Classification == ContactClassification.Helicopter;
        _selection.Scale = isInfantry
            ? new Vector3(0.35f, 0.15f, 0.35f)
            : isHelicopter
                ? new Vector3(2.6f, 0.15f, 2.6f)
                : new Vector3(1, 0.15f, 1);
        _ghostBase.Scale = isInfantry
            ? new Vector3(0.5f, 0.15f, 0.5f)
            : isHelicopter
                ? new Vector3(2.4f, 0.15f, 2.4f)
                : new Vector3(1, 0.15f, 1);

        if (contact.State == ContactState.Current)
        {
            _ghostBase.Visible = false;

            if (_enemyModel is null || _currentModelType != contact.Type || _currentModelClassification != contact.Classification)
            {
                ClearEnemyModel();
                string? unitId = _unitIdProvider is not null
                    ? _unitIdProvider(contact.Type)
                    : UnitViewSet.GetDefaultUnitId(contact.Type);
                ContactVisualCatalog.ContactVisualHandle? handle = ContactVisualCatalog.InstantiateEnemyVisual(
                    unitId,
                    contact.Classification,
                    _hostileFaction?.Invoke(),
                    _lastCameraSize);
                _enemyModel = handle?.Root;
                _visual = handle?.Visual;
                _currentModelType = contact.Type;
                _currentModelClassification = contact.Classification;
                _hasPendingLodWhileHidden = false;
                if (_enemyModel is not null)
                {
                    Root.AddChild(_enemyModel);
                    if (_visual is not null)
                    {
                        _activeLod = _visual.ActiveLod;
                        _rigs = new ContactRig?[_visual.LodCount];
                    }
                    else
                    {
                        _activeLod = 0;
                        _rigs = new ContactRig?[1];
                    }
                    ApplyActiveRig(EnsureRig(_activeLod, isInfantry));
                }
            }

            if (_enemyModel is not null)
            {
                bool returningFromHidden = !_enemyModel.Visible;
                _enemyModel.Visible = true;
                _relativeTurretYaw = Mathf.Wrap(turretYaw - hullHeading, -Mathf.Pi, Mathf.Pi);
                if (returningFromHidden && _hasPendingLodWhileHidden && _lastCameraSize.HasValue)
                {
                    int unlimited = int.MaxValue;
                    ApplyLod(_lastCameraSize.Value, ref unlimited);
                    _hasPendingLodWhileHidden = false;
                }
                bool isAiming = _aimingTimer > 0f || MathF.Abs(_relativeTurretYaw) > 0.05f;
                float yaw = (isInfantry && isAiming && speed < 0.15f) ? turretYaw : hullHeading;
                _enemyModel.Position = new Vector3(0f, _altitude, 0f);
                _enemyModel.Rotation = new Vector3(0, yaw, 0);
                if (_turret is not null)
                    _turret.Rotation = new Vector3(0, _relativeTurretYaw, 0);

                if (isHelicopter || _frontRotor is not null || _frontRotorBlur is not null)
                {
                    bool airborne = contact.FlightPhase != FlightPhase.Ground || _altitude > 0.25f;
                    _rotorAngle = Mathf.Wrap(_rotorAngle + (airborne ? 1.1f : 0.14f), 0f, Mathf.Tau);
                    if (_frontRotor is not null)
                    {
                        _frontRotor.Visible = !airborne;
                        if (!airborne) _frontRotor.Rotation = new Vector3(0f, _rotorAngle, 0f);
                    }
                    if (_rearRotor is not null)
                    {
                        _rearRotor.Visible = !airborne;
                        if (!airborne) _rearRotor.Rotation = new Vector3(0f, -_rotorAngle, 0f);
                    }
                    if (_frontRotorBlur is not null)
                    {
                        _frontRotorBlur.Visible = airborne;
                        if (airborne) _frontRotorBlur.Rotation = new Vector3(0f, _rotorAngle * 0.25f, 0f);
                    }
                    if (_rearRotorBlur is not null)
                    {
                        _rearRotorBlur.Visible = airborne;
                        if (airborne) _rearRotorBlur.Rotation = new Vector3(0f, -_rotorAngle * 0.25f, 0f);
                    }
                    if (_rearRamp is not null)
                    {
                        _rearRamp.Rotation = new Vector3(airborne ? 0f : Mathf.DegToRad(42f), 0f, 0f);
                    }
                }

                if (_animPlayer is not null)
                {
                    StringName targetAnimName = speed >= 2.2f ? AnimRun : (speed >= 0.15f ? AnimWalk : (isAiming ? AnimAimStanding : AnimIdle));
                    if (_currentAnimName != targetAnimName && _animPlayer.HasAnimation(targetAnimName))
                    {
                        _animPlayer.Play(targetAnimName);
                        _currentAnimName = targetAnimName;
                        _currentAnim = targetAnimName.ToString();
                    }

                    if (targetAnimName == AnimWalk)
                    {
                        _animPlayer.SpeedScale = MathF.Max(0.1f, speed / 1.5f);
                    }
                    else if (targetAnimName == AnimRun)
                    {
                        _animPlayer.SpeedScale = MathF.Max(0.1f, speed / 3.5f);
                    }
                    else
                    {
                        _animPlayer.SpeedScale = 1.0f;
                    }
                }
            }
        }
        else
        {
            // Last-known contact: hide live model, display uncertainty ghost marker
            if (_enemyModel is not null)
            {
                _enemyModel.Visible = false;
            }
            _ghostBase.Visible = true;
        }

        _selection.Visible = isSelected;
    }

    internal Node3D? Turret => _turret;

    public bool TryGetMuzzleTransform(out Transform3D transform)
    {
        if (_enemyModel is not null && _enemyModel.Visible && _muzzle is not null)
        {
            transform = _muzzle.GlobalTransform;
            return true;
        }
        transform = default;
        return false;
    }

    internal bool UpdateLod(float cameraSize)
    {
        int unlimited = int.MaxValue;
        return UpdateLod(cameraSize, ref unlimited);
    }

    internal bool UpdateLod(float cameraSize, ref int remainingInstantiations)
    {
        _lastCameraSize = cameraSize;
        if (_enemyModel is null) return false;
        if (!_enemyModel.Visible)
        {
            _hasPendingLodWhileHidden = true;
            return false;
        }
        _hasPendingLodWhileHidden = false;
        if (_visual is null || _visual.LodCount <= 1) return false;

        return ApplyLod(cameraSize, ref remainingInstantiations);
    }

    private bool ApplyLod(float cameraSize, ref int remainingInstantiations)
    {
        if (_enemyModel is null || _visual is null || _visual.LodCount <= 1) return false;

        int targetLod = VehicleLod.SelectNextLod(cameraSize, _activeLod, _visual.LodCount);
        if (targetLod == _activeLod) return false;

        if (!_visual.IsLoaded(targetLod))
        {
            if (remainingInstantiations <= 0) return false;
            remainingInstantiations--;
        }

        AnimationPlayer? prevPlayer = _animPlayer;
        float prevSpeed = prevPlayer?.SpeedScale ?? 1.0f;
        _visual.SwitchLod(targetLod, out _);
        _activeLod = targetLod;
        ApplyActiveRig(EnsureRig(targetLod, _currentModelClassification == ContactClassification.Infantry));
        VehicleLod.TransferAnimationState(prevPlayer, _animPlayer, _currentAnimName, prevSpeed);
        return true;
    }

    private ContactRig EnsureRig(int lod, bool isInfantry)
    {
        ContactRig? rig = _rigs[lod];
        if (rig is not null)
        {
            if (rig.Turret is not null)
                rig.Turret.Rotation = new Vector3(0, _relativeTurretYaw, 0);
            return rig;
        }

        Node3D lodNode = _visual?.GetOrCreateLod(lod) ?? _enemyModel!;
        Node3D? turret = lodNode.FindChild("TurretYaw", recursive: true, owned: false) as Node3D;
        Node3D? muzzle = lodNode.FindChild("Muzzle", recursive: true, owned: false) as Node3D;
        AnimationPlayer? animPlayer = lodNode.FindChild("AnimationPlayer", recursive: true, owned: false) as AnimationPlayer;
        Node3D? frontRotor = lodNode.FindChild("FrontRotor", recursive: true, owned: false) as Node3D;
        Node3D? rearRotor = lodNode.FindChild("RearRotor", recursive: true, owned: false) as Node3D;
        Node3D? frontRotorBlur = lodNode.FindChild("FrontRotorBlur", recursive: true, owned: false) as Node3D;
        Node3D? rearRotorBlur = lodNode.FindChild("RearRotorBlur", recursive: true, owned: false) as Node3D;
        Node3D? rearRamp = lodNode.FindChild("RearRamp", recursive: true, owned: false) as Node3D;
        DisableShadowCasting(frontRotorBlur);
        DisableShadowCasting(rearRotorBlur);
        if (muzzle is null && isInfantry)
        {
            Node3D mNode = new() { Name = "Muzzle", Position = new Vector3(0, 1.35f, -0.6f) };
            lodNode.AddChild(mNode);
            muzzle = mNode;
        }
        if (turret is not null)
        {
            turret.Rotation = new Vector3(0, _relativeTurretYaw, 0);
        }
        rig = new ContactRig(turret, muzzle, animPlayer, frontRotor, rearRotor, frontRotorBlur, rearRotorBlur, rearRamp);
        _rigs[lod] = rig;
        return rig;
    }

    private void ApplyActiveRig(ContactRig rig)
    {
        _turret = rig.Turret;
        _muzzle = rig.Muzzle;
        _animPlayer = rig.AnimPlayer;
        _frontRotor = rig.FrontRotor;
        _rearRotor = rig.RearRotor;
        _frontRotorBlur = rig.FrontRotorBlur;
        _rearRotorBlur = rig.RearRotorBlur;
        _rearRamp = rig.RearRamp;
    }

    private void ClearEnemyModel()
    {
        if (_enemyModel is not null)
        {
            Root.RemoveChild(_enemyModel);
            _enemyModel.QueueFree();
            _enemyModel = null;
            _visual = null;
            _rigs = [];
            _activeLod = 0;
            _turret = null;
            _muzzle = null;
            _animPlayer = null;
            _frontRotor = null;
            _rearRotor = null;
            _frontRotorBlur = null;
            _rearRotorBlur = null;
            _rearRamp = null;
            _altitude = 0f;
            _rotorAngle = 0f;
            _currentAnim = "";
            _currentAnimName = null;
            _aimingTimer = 0f;
            _relativeTurretYaw = 0f;
            _hasPendingLodWhileHidden = false;
            _hasPrevPos = false;
        }
        _currentModelType = ushort.MaxValue;
        _currentModelClassification = ContactClassification.Unknown;
    }

    private static void DisableShadowCasting(Node? root)
    {
        if (root is null) return;
        if (root is GeometryInstance3D geom)
        {
            geom.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        int count = root.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            DisableShadowCasting(root.GetChild(i));
        }
    }
}
