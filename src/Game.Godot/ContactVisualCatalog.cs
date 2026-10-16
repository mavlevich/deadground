using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

// Contact presentation mapping maps allowed ContactClassification to generic visual models.
// It is intentionally decoupled from own-unit definition IDs to prevent information leaks.
internal static class ContactVisualCatalog
{
    private static PackedScene? _reconScene;

    // hostileFaction comes from the public match setup, never from the contact itself.
    internal static Node3D? InstantiateEnemyModel(ContactClassification classification, string? hostileFaction = null)
    {
        return classification switch
        {
            ContactClassification.Tank => CreateEnemyTankModel(hostileFaction),
            ContactClassification.Recon => CreateEnemyReconModel(),
            ContactClassification.CommandVehicle => CreateEnemyCommandVehicleModel(),
            ContactClassification.Vehicle => CreateEnemyGenericVehicleModel(),
            _ => null
        };
    }

    private static Node3D CreateEnemyTankModel(string? hostileFaction)
    {
        VehicleVisual visual = VehicleVisualCatalog.InstantiateVisual("contact_tank", hostileFaction)
            ?? throw new InvalidOperationException("visuals.json has no contact_tank entry for enemy tank presentation.");
        visual.Root.Name = "EnemyTankModel";
        AttachHostileAffiliationMarker(visual.Root);
        return visual.Root;
    }

    private static Node3D CreateEnemyReconModel()
    {
        _reconScene ??= GD.Load<PackedScene>("res://Assets/Vehicles/recon.glb")
            ?? throw new InvalidOperationException("Recon asset could not be loaded for enemy contact presentation.");

        Node3D model = _reconScene.Instantiate<Node3D>();
        model.Name = "EnemyReconModel";
        model.Scale = Vector3.One * 0.75f;
        AttachHostileAffiliationMarker(model);
        return model;
    }

    private static Node3D CreateEnemyCommandVehicleModel()
    {
        Node3D model = new() { Name = "EnemyCommandVehicleModel" };

        StandardMaterial3D hullMaterial = new()
        {
            AlbedoColor = new Color("584d44"),
            Roughness = 0.85f
        };

        StandardMaterial3D shelterMaterial = new()
        {
            AlbedoColor = new Color("443a32"),
            Roughness = 0.9f
        };

        StandardMaterial3D mastMaterial = new()
        {
            AlbedoColor = new Color("c8b488"),
            Roughness = 0.6f
        };

        // Main chassis
        model.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.4f, 1.2f, 4.0f) },
            Position = new Vector3(0, 0.7f, 0),
            MaterialOverride = hullMaterial
        });

        // Command shelter / radio cabin
        model.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 1.0f, 2.4f) },
            Position = new Vector3(0, 1.7f, -0.4f),
            MaterialOverride = shelterMaterial
        });

        // Communication dome
        model.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.35f, BottomRadius = 0.45f, Height = 0.3f },
            Position = new Vector3(0, 2.3f, -0.6f),
            MaterialOverride = mastMaterial
        });

        // Antenna mast
        model.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.08f, Height = 1.6f },
            Position = new Vector3(0.6f, 2.8f, -0.2f),
            MaterialOverride = mastMaterial
        });

        model.Scale = Vector3.One * 0.75f;
        AttachHostileAffiliationMarker(model);
        return model;
    }

    private static Node3D CreateEnemyGenericVehicleModel()
    {
        Node3D model = new() { Name = "EnemyGenericVehicleModel" };

        StandardMaterial3D hullMaterial = new()
        {
            AlbedoColor = new Color("4e5248"),
            Roughness = 0.85f
        };

        // Main chassis
        model.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.2f, 1.0f, 3.8f) },
            Position = new Vector3(0, 0.6f, 0),
            MaterialOverride = hullMaterial
        });

        // Cab / superstructure
        model.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.8f, 0.8f, 1.6f) },
            Position = new Vector3(0, 1.4f, 0.4f),
            MaterialOverride = hullMaterial
        });

        model.Scale = Vector3.One * 0.75f;
        AttachHostileAffiliationMarker(model);
        return model;
    }

    private static void AttachHostileAffiliationMarker(Node3D model)
    {
        // Ground-level hostile affiliation diamond/bracket to distinguish enemy models from own models
        MeshInstance3D hostileBase = new()
        {
            Name = "HostileAffiliationMarker",
            Mesh = new BoxMesh { Size = new Vector3(2.8f, 0.08f, 2.8f) },
            Position = new Vector3(0, 0.06f, 0),
            Rotation = new Vector3(0, Mathf.DegToRad(45), 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.85f, 0.2f, 0.15f, 0.6f),
                Roughness = 0.9f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        model.AddChild(hostileBase);
    }
}
