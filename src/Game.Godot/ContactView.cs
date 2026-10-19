using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal sealed class ContactView
{
    public Node3D Root { get; } = new();
    public ContactObservation Observation { get; private set; }

    private readonly MeshInstance3D _ghostBase;
    private readonly MeshInstance3D _selection;

    private Node3D? _enemyModel;
    // Found once per model: a tree search per frame and per shot would cost every visible contact.
    private Node3D? _turret;
    private Node3D? _muzzle;
    private ushort _currentModelType = ushort.MaxValue;
    private ContactClassification _currentModelClassification = ContactClassification.Unknown;

    internal Node3D? EnemyModel => _enemyModel;
    internal bool HasEnemyModel => _enemyModel is not null && _enemyModel.Visible;
    internal MeshInstance3D GhostBase => _ghostBase;
    internal ushort CurrentModelType => _currentModelType;
    internal ContactClassification CurrentModelClassification => _currentModelClassification;

    private readonly Func<string?>? _hostileFaction;
    private readonly Func<ushort, string?>? _unitIdProvider;

    public ContactView(Func<string?>? hostileFaction = null, Func<ushort, string?>? unitIdProvider = null)
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
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
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
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        Root.AddChild(_selection);
    }

    public void Update(ContactObservation contact, float groundY, bool isSelected, float turretYaw = 0f) =>
        Update(contact, contact.Position, groundY, isSelected, turretYaw);

    public void Update(ContactObservation contact, System.Numerics.Vector2 position, float groundY, bool isSelected, float turretYaw = 0f)
    {
        Observation = contact;
        Root.Position = new Vector3(position.X, groundY, position.Y);

        if (contact.State == ContactState.Current)
        {
            _ghostBase.Visible = false;

            if (_enemyModel is null || _currentModelType != contact.Type || _currentModelClassification != contact.Classification)
            {
                ClearEnemyModel();
                string? unitId = _unitIdProvider is not null
                    ? _unitIdProvider(contact.Type)
                    : UnitViewSet.GetDefaultUnitId(contact.Type);
                _enemyModel = ContactVisualCatalog.InstantiateEnemyModel(unitId, contact.Classification, _hostileFaction?.Invoke());
                _currentModelType = contact.Type;
                _currentModelClassification = contact.Classification;
                if (_enemyModel is not null)
                {
                    Root.AddChild(_enemyModel);
                    _turret = _enemyModel.FindChild("TurretYaw", recursive: true, owned: false) as Node3D;
                    _muzzle = _enemyModel.FindChild("Muzzle", recursive: true, owned: false) as Node3D;
                }
            }

            if (_enemyModel is not null)
            {
                _enemyModel.Visible = true;
                SetTurretYaw(turretYaw);
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

    // Contact models carry no hull heading, so the world yaw is the turret's local yaw.
    public void SetTurretYaw(float worldYaw)
    {
        if (_turret is not null) _turret.Rotation = new Vector3(0, worldYaw, 0);
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

    private void ClearEnemyModel()
    {
        if (_enemyModel is not null)
        {
            Root.RemoveChild(_enemyModel);
            _enemyModel.QueueFree();
            _enemyModel = null;
        }
        _turret = null;
        _muzzle = null;
        _currentModelType = ushort.MaxValue;
        _currentModelClassification = ContactClassification.Unknown;
    }
}
