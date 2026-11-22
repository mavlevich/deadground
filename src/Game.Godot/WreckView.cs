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

    public WreckId Id { get; set; }
    public Node3D Root { get; } = new();
    internal Node3D Model { get; }
    internal string? UnitId { get; }
    internal bool HasVehicleModel { get; }
    internal int ActiveLod => _activeLod;
    public int DestructionTick { get; set; }
    public bool IsSmoking => _smokeEmitter is not null && _smokeEmitter.Emitting;
    public bool IsBurning => _fireEmitter is not null && _fireEmitter.Emitting;
    public int ActiveEmitterCount => (IsSmoking ? 1 : 0) + (IsBurning ? 1 : 0);

    public const float FireDurationSeconds = 8.0f;
    public const float SmokeDurationSeconds = 45.0f;

    private readonly VehicleVisual? _visual;
    private readonly Node3D?[]? _turretNodes;
    private GpuParticles3D? _fireEmitter;
    private GpuParticles3D? _smokeEmitter;
    private int _activeLod;
    private float _hullHeading;
    private float _turretYaw;

    public WreckView(ushort definition = 0, float? initialCameraSize = null)
        : this(UnitViewSet.GetDefaultUnitId(definition), initialCameraSize: initialCameraSize) { }

    public WreckView(string? unitId, string? faction = null, float? initialCameraSize = null)
    {
        UnitId = unitId;
        _visual = VehicleVisualCatalog.InstantiateVisual(unitId, faction, initialCameraSize);
        HasVehicleModel = _visual is not null;

        if (_visual is not null)
        {
            Model = _visual.Root;
            _activeLod = _visual.ActiveLod;
            _turretNodes = new Node3D?[_visual.LodCount];
            Node3D initialNode = _visual.GetOrCreateLod(_activeLod);
            ApplyCharredMaterial(initialNode, CharredMaterial);
            _turretNodes[_activeLod] = initialNode.FindChild("TurretYaw", recursive: true, owned: false) as Node3D;
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

    internal bool IsLodLoaded(int index) => _visual is not null && _visual.IsLoaded(index);

    internal Node3D? GetLod(int index) => _visual?.GetLoadedLod(index);

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

    internal bool UpdateLod(float cameraSize)
    {
        int unlimited = int.MaxValue;
        return UpdateLod(cameraSize, ref unlimited);
    }

    internal bool UpdateLod(float cameraSize, ref int remainingInstantiations)
    {
        if (_visual is null || _turretNodes is null) return false;

        int targetLod = VehicleLod.SelectNextLod(cameraSize, _activeLod, _visual.LodCount);
        if (targetLod == _activeLod) return false;

        bool firstLoad = !_visual.IsLoaded(targetLod);
        if (firstLoad)
        {
            if (remainingInstantiations <= 0) return false;
            remainingInstantiations--;
        }

        _visual.SwitchLod(targetLod, out Node3D next);
        _activeLod = targetLod;
        if (firstLoad)
            ApplyCharredMaterial(next, CharredMaterial);

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
        return true;
    }

    internal void UpdateEffects(float ageSeconds, bool allowEffects)
    {
        bool allowFire = allowEffects && ageSeconds < FireDurationSeconds;
        bool allowSmoke = allowEffects && ageSeconds < SmokeDurationSeconds;
        UpdateEffects(ageSeconds, allowFire, allowSmoke);
    }

    internal void UpdateEffects(float ageSeconds, bool allowFire, bool allowSmoke)
    {
        if (ageSeconds >= SmokeDurationSeconds || ageSeconds < 0f)
        {
            if (_fireEmitter is not null && _fireEmitter.Emitting) _fireEmitter.Emitting = false;
            if (_smokeEmitter is not null && _smokeEmitter.Emitting) _smokeEmitter.Emitting = false;
            if (ageSeconds >= SmokeDurationSeconds)
            {
                if (_fireEmitter is not null)
                {
                    Root.RemoveChild(_fireEmitter);
                    _fireEmitter.QueueFree();
                    _fireEmitter = null;
                }
                if (_smokeEmitter is not null)
                {
                    Root.RemoveChild(_smokeEmitter);
                    _smokeEmitter.QueueFree();
                    _smokeEmitter = null;
                }
            }
            return;
        }

        if (allowFire)
        {
            if (_fireEmitter is null)
            {
                _fireEmitter = ParticleEffects.CreateWreckFire();
                _fireEmitter.Position = new Vector3(0f, 1.15f, -0.2f);
                Root.AddChild(_fireEmitter);
            }
            if (!_fireEmitter.Emitting) _fireEmitter.Emitting = true;
        }
        else if (_fireEmitter is not null && _fireEmitter.Emitting)
        {
            _fireEmitter.Emitting = false;
        }

        if (allowSmoke)
        {
            if (_smokeEmitter is null)
            {
                _smokeEmitter = ParticleEffects.CreateWreckSmoke();
                _smokeEmitter.Position = new Vector3(0f, 1.25f, -0.2f);
                Root.AddChild(_smokeEmitter);
            }
            if (!_smokeEmitter.Emitting) _smokeEmitter.Emitting = true;
        }
        else if (_smokeEmitter is not null && _smokeEmitter.Emitting)
        {
            _smokeEmitter.Emitting = false;
        }
    }

    internal void ClearEffects()
    {
        DestructionTick = 0;
        if (_fireEmitter is not null)
        {
            Root.RemoveChild(_fireEmitter);
            _fireEmitter.QueueFree();
            _fireEmitter = null;
        }
        if (_smokeEmitter is not null)
        {
            Root.RemoveChild(_smokeEmitter);
            _smokeEmitter.QueueFree();
            _smokeEmitter = null;
        }
    }

    internal static void ApplyCharredMaterial(Node node, Material material)
    {
        // Hide flight rotor blur discs on wrecks so they do not turn into giant solid black discs.
        string name = node.Name.ToString();
        if (name == "FrontRotorBlur" || name == "RearRotorBlur")
        {
            if (node is Node3D blurNode)
            {
                blurNode.Visible = false;
            }
            if (node is GeometryInstance3D blurGeom)
            {
                blurGeom.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            }
            return;
        }

        if (node is Node3D partNode)
        {
            if (name == "FrontRotor")
            {
                partNode.Rotation = new Vector3(Mathf.DegToRad(9f), Mathf.DegToRad(18f), Mathf.DegToRad(-7f));
            }
            else if (name == "RearRotor")
            {
                partNode.Rotation = new Vector3(Mathf.DegToRad(-11f), Mathf.DegToRad(-24f), Mathf.DegToRad(8f));
            }
            else if (name == "RearRamp")
            {
                partNode.Rotation = new Vector3(Mathf.DegToRad(26f), 0f, 0f);
            }
        }

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
