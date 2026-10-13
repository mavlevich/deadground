using Godot;

namespace DeadGround.Client;

internal sealed class UnitView
{
    public Node3D Root { get; } = new();
    private readonly MeshInstance3D _selection;
    private readonly Node3D? _turret;
    private Vector3 _previousPosition;
    private bool _hasPosition;
    internal Node3D Model { get; }
    internal bool HasVehicleModel { get; }

    public UnitView(ushort definition = 0) : this(UnitViewSet.GetDefaultUnitId(definition)) { }

    public UnitView(string? unitId)
    {
        Node3D? imported = VehicleVisualCatalog.Instantiate(unitId);
        HasVehicleModel = imported is not null;
        Model = imported ?? new Node3D { Name = "PlaceholderModel" };
        Root.AddChild(Model);
        _turret = Model.FindChild("TurretYaw", recursive: true, owned: false) as Node3D;
        if (imported is null)
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
    private Vector3 _recoilOffset = Vector3.Zero;
    public Vector3 RecoilOffset => _recoilOffset;

    public void UpdatePosition(Vector3 position)
    {
        if (_hasPosition)
        {
            Vector3 movement = position - _previousPosition;
            movement.Y = 0;
            if (movement.LengthSquared() > 0.000001f)
                Model.Rotation = new Vector3(0, Mathf.Atan2(-movement.X, -movement.Z), 0);
        }
        _previousPosition = position;
        _hasPosition = true;
        Root.Position = position + _recoilOffset;
    }

    public void AimAt(Vector3 observedPosition)
    {
        if (_turret is null) return;
        Vector3 direction = observedPosition - _previousPosition;
        if (direction.X * direction.X + direction.Z * direction.Z <= 0.000001f) return;
        float worldYaw = Mathf.Atan2(-direction.X, -direction.Z);
        _turret.Rotation = new Vector3(0, Mathf.Wrap(worldYaw - Model.Rotation.Y, -Mathf.Pi, Mathf.Pi), 0);
    }

    public void ApplyRecoil(Vector3 shotDirection)
    {
        _recoilOffset = -shotDirection.Normalized() * 0.35f;
    }

    public void UpdateRecoil(double delta)
    {
        _recoilOffset = _recoilOffset.MoveToward(Vector3.Zero, (float)delta * 3.5f);
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
