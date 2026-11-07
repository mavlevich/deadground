using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal sealed class WreckView
{
    private static readonly StandardMaterial3D CharredMaterial = new()
    {
        AlbedoColor = new Color(0.12f, 0.12f, 0.12f),
        Roughness = 0.90f,
        Metallic = 0.15f
    };

    public Node3D Root { get; } = new();
    internal Node3D Model { get; }
    internal string? UnitId { get; }
    internal bool HasVehicleModel { get; }
    internal int ActiveLod => _activeLod;

    private readonly VehicleVisual? _visual;
    private readonly Node3D?[]? _turretNodes;
    private int _activeLod;
    private float _hullHeading;
    private float _turretYaw;

    public WreckView(ushort definition = 0) : this(UnitViewSet.GetDefaultUnitId(definition)) { }

    public WreckView(string? unitId, string? faction = null)
    {
        UnitId = unitId;
        _visual = VehicleVisualCatalog.InstantiateVisual(unitId, faction);
        HasVehicleModel = _visual is not null;

        if (_visual is not null)
        {
            Model = _visual.Root;
            _turretNodes = new Node3D?[_visual.LodCount];
            Node3D lod0 = _visual.GetOrCreateLod(0);
            ApplyCharredMaterial(lod0, CharredMaterial);
            _turretNodes[0] = lod0.FindChild("TurretYaw", recursive: true, owned: false) as Node3D;
        }
        else
        {
            Model = new MeshInstance3D
            {
                Name = "PlaceholderWreck",
                Mesh = new BoxMesh { Size = new Vector3(2.5f, 1.2f, 4.5f) },
                MaterialOverride = CharredMaterial
            };
        }

        Root.AddChild(Model);
    }

    public void Update(WreckObservation wreck, float groundY)
    {
        _hullHeading = wreck.HullHeading;
        _turretYaw = wreck.TurretYaw;
        Root.Position = new Vector3(wreck.Position.X, groundY, wreck.Position.Y);
        Model.Rotation = new Vector3(0, _hullHeading, 0);

        if (_turretNodes is not null)
        {
            Node3D? turret = _turretNodes[_activeLod];
            if (turret is not null)
            {
                turret.Rotation = new Vector3(0, Mathf.Wrap(_turretYaw - _hullHeading, -Mathf.Pi, Mathf.Pi), 0);
            }
        }
    }

    internal void UpdateLod(float cameraSize)
    {
        if (_visual is null || _turretNodes is null) return;

        int targetLod = cameraSize switch
        {
            > UnitView.Lod2CameraSize when _visual.LodCount > 2 => 2,
            > UnitView.Lod1CameraSize when _visual.LodCount > 1 => 1,
            _ => 0
        };

        if (targetLod == _activeLod) return;

        Node3D current = _visual.GetOrCreateLod(_activeLod);
        current.Visible = false;

        _activeLod = targetLod;
        Node3D next = _visual.GetOrCreateLod(targetLod);
        ApplyCharredMaterial(next, CharredMaterial);
        next.Visible = true;

        Node3D? turret = _turretNodes[targetLod];
        if (turret is null)
        {
            turret = next.FindChild("TurretYaw", recursive: true, owned: false) as Node3D;
            _turretNodes[targetLod] = turret;
        }

        if (turret is not null)
        {
            turret.Rotation = new Vector3(0, Mathf.Wrap(_turretYaw - _hullHeading, -Mathf.Pi, Mathf.Pi), 0);
        }
    }

    internal static void ApplyCharredMaterial(Node node, Material material)
    {
        if (node is GeometryInstance3D geom)
        {
            geom.MaterialOverride = material;
        }

        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            ApplyCharredMaterial(node.GetChild(i), material);
        }
    }
}
