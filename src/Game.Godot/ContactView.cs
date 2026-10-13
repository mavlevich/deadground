using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal sealed class ContactView
{
    public Node3D Root { get; } = new();
    public ContactObservation Observation { get; private set; }

    private readonly MeshInstance3D _marker;
    private readonly MeshInstance3D _ghostBase;
    private readonly MeshInstance3D _selection;
    private readonly StandardMaterial3D _currentMaterial;
    private readonly StandardMaterial3D _lastKnownMaterial;

    private Node3D? _enemyModel;
    private ContactClassification _currentModelClassification = ContactClassification.Unknown;

    internal Node3D? EnemyModel => _enemyModel;
    internal bool HasEnemyModel => _enemyModel is not null && _enemyModel.Visible;
    internal MeshInstance3D Marker => _marker;
    internal MeshInstance3D GhostBase => _ghostBase;
    internal ContactClassification CurrentModelClassification => _currentModelClassification;

    public ContactView()
    {
        _currentMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color("e04b36"),
            Roughness = 0.5f,
            EmissionEnabled = true,
            Emission = new Color("ff4020"),
            EmissionEnergyMultiplier = 0.4f
        };

        _lastKnownMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.72f, 0.45f, 0.38f, 0.75f),
            Roughness = 0.9f,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha
        };

        // Marker diamond shape (box rotated 45 degrees) for unknown contacts
        _marker = new MeshInstance3D
        {
            Name = "NeutralContactMarker",
            Mesh = new BoxMesh { Size = new Vector3(1.4f, 1.4f, 1.4f) },
            Position = new Vector3(0, 1.8f, 0),
            Rotation = new Vector3(Mathf.DegToRad(45), Mathf.DegToRad(45), 0),
            MaterialOverride = _currentMaterial
        };
        Root.AddChild(_marker);

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

    public void Update(ContactObservation contact, float groundY, bool isSelected) =>
        Update(contact, contact.Position, groundY, isSelected);

    public void Update(ContactObservation contact, System.Numerics.Vector2 position, float groundY, bool isSelected)
    {
        Observation = contact;
        Root.Position = new Vector3(position.X, groundY, position.Y);

        if (contact.State == ContactState.Current)
        {
            _ghostBase.Visible = false;

            if (contact.Level == DetectionLevel.Identified && contact.Classification != ContactClassification.Unknown)
            {
                // Identified enemy: switch to generic 3D model
                if (_currentModelClassification != contact.Classification)
                {
                    ClearEnemyModel();
                    _enemyModel = ContactVisualCatalog.InstantiateEnemyModel(contact.Classification);
                    _currentModelClassification = contact.Classification;
                    if (_enemyModel is not null)
                    {
                        Root.AddChild(_enemyModel);
                    }
                }

                if (_enemyModel is not null)
                {
                    _enemyModel.Visible = true;
                    _marker.Visible = false;
                }
                else
                {
                    _marker.Visible = true;
                    _marker.MaterialOverride = _currentMaterial;
                    _marker.Scale = Vector3.One;
                }
            }
            else
            {
                // Unknown or unconfirmed enemy: clean up any previously identified model
                ClearEnemyModel();
                _marker.Visible = true;
                _marker.MaterialOverride = _currentMaterial;
                _marker.Scale = Vector3.One;
            }
        }
        else
        {
            // Last-known contact: hide live model, display uncertainty ghost marker
            if (_enemyModel is not null)
            {
                _enemyModel.Visible = false;
            }
            _marker.Visible = true;
            _marker.MaterialOverride = _lastKnownMaterial;
            _marker.Scale = new Vector3(0.85f, 0.85f, 0.85f);
            _ghostBase.Visible = true;
        }

        _selection.Visible = isSelected;
    }

    private void ClearEnemyModel()
    {
        if (_enemyModel is not null)
        {
            Root.RemoveChild(_enemyModel);
            _enemyModel.QueueFree();
            _enemyModel = null;
        }
        _currentModelClassification = ContactClassification.Unknown;
    }
}
