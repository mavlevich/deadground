using Godot;

namespace DeadGround.Client;

internal sealed class UnitView
{
    public Node3D Root { get; } = new();
    private readonly MeshInstance3D _selection;

    public UnitView(ushort definition = 0)
    {
        Root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(3, 2, 4) },
            Position = new Vector3(0, 1.1f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("b8c5a0"), Roughness = 0.85f }
        });
        Root.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.6f, 0.5f, 1.8f) },
            Position = new Vector3(0, 2.35f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("485c45"), Roughness = 1 }
        });

        if (definition == 1)
        {
            // Recon vehicle: communication and sensor antenna
            Root.AddChild(new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.08f, Height = 1.6f },
                Position = new Vector3(0.5f, 3.2f, -0.4f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("d5ded0"), Roughness = 0.4f }
            });
        }
        else if (definition == 2)
        {
            // Tank: turret and main gun barrel
            Root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 2.2f) },
                Position = new Vector3(0, 2.4f, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("3b4d38"), Roughness = 0.9f }
            });
            Root.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(0.24f, 0.24f, 2.4f) },
                Position = new Vector3(0, 2.4f, 1.8f),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color("252b24"), Roughness = 0.7f }
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
    }

    public void SetSelected(bool selected) => _selection.Visible = selected;
}
