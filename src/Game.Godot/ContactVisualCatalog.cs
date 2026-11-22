using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

// Contact presentation mapping maps visible contacts to their type model,
// with procedural fallbacks for types without an authored visual entry.
internal static class ContactVisualCatalog
{
    internal readonly record struct ContactVisualHandle(Node3D Root, VehicleVisual? Visual);

    // hostileFaction comes from the public match setup, never from private opponent truth.
    internal static Node3D? InstantiateEnemyModel(
        string? unitId,
        ContactClassification classification,
        string? hostileFaction = null,
        float? initialCameraSize = null)
    {
        return InstantiateEnemyVisual(unitId, classification, hostileFaction, initialCameraSize)?.Root;
    }

    internal static ContactVisualHandle? InstantiateEnemyVisual(
        string? unitId,
        ContactClassification classification,
        string? hostileFaction = null,
        float? initialCameraSize = null)
    {
        if (unitId is not null)
        {
            VehicleVisual? visual = VehicleVisualCatalog.InstantiateVisual(unitId, hostileFaction, initialCameraSize);
            if (visual is not null)
            {
                visual.Root.Name = classification switch
                {
                    ContactClassification.Tank => "EnemyTankModel",
                    ContactClassification.Recon => "EnemyReconModel",
                    ContactClassification.Apc => "EnemyApcModel",
                    ContactClassification.CommandVehicle => "EnemyCommandVehicleModel",
                    ContactClassification.Infantry => "EnemyInfantryModel",
                    ContactClassification.Helicopter => "EnemyHelicopterModel",
                    ContactClassification.Artillery => "EnemyArtilleryModel",
                    ContactClassification.AntiAir => "EnemyAntiAirModel",
                    _ => "EnemyVehicleModel"
                };
                AttachHostileAffiliationMarker(visual.Root, classification == ContactClassification.Infantry);
                return new ContactVisualHandle(visual.Root, visual);
            }
        }

        return classification switch
        {
            ContactClassification.Tank => CreateEnemyVisual("contact_tank", "EnemyTankModel", hostileFaction, initialCameraSize),
            ContactClassification.Recon => CreateEnemyVisual("contact_recon", "EnemyReconModel", hostileFaction, initialCameraSize),
            ContactClassification.Apc => CreateEnemyVisual("contact_apc", "EnemyApcModel", hostileFaction, initialCameraSize),
            ContactClassification.CommandVehicle => new ContactVisualHandle(CreateEnemyCommandVehicleModel(), null),
            ContactClassification.Infantry => CreateEnemyVisual("contact_infantry", "EnemyInfantryModel", hostileFaction, initialCameraSize, isInfantry: true),
            ContactClassification.Helicopter => CreateEnemyVisual("contact_helicopter", "EnemyHelicopterModel", hostileFaction, initialCameraSize),
            ContactClassification.Artillery => CreateEnemyVisual("contact_artillery", "EnemyArtilleryModel", hostileFaction, initialCameraSize),
            ContactClassification.AntiAir => CreateEnemyVisual("contact_anti_air", "EnemyAntiAirModel", hostileFaction, initialCameraSize),
            ContactClassification.Vehicle => new ContactVisualHandle(CreateEnemyGenericVehicleModel(), null),
            _ => null
        };
    }

    private static ContactVisualHandle CreateEnemyVisual(
        string visualId,
        string name,
        string? hostileFaction,
        float? initialCameraSize,
        bool isInfantry = false)
    {
        VehicleVisual visual = VehicleVisualCatalog.InstantiateVisual(visualId, hostileFaction, initialCameraSize)
            ?? throw new InvalidOperationException($"visuals.json has no {visualId} entry for enemy contact presentation.");
        visual.Root.Name = name;
        AttachHostileAffiliationMarker(visual.Root, isInfantry);
        return new ContactVisualHandle(visual.Root, visual);
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

    private static void AttachHostileAffiliationMarker(Node3D model, bool isInfantry = false)
    {
        // Ground-level hostile affiliation diamond/bracket to distinguish enemy models from own models
        Vector3 boxSize = isInfantry ? new Vector3(0.9f, 0.04f, 0.9f) : new Vector3(2.8f, 0.08f, 2.8f);
        float posY = isInfantry ? 0.03f : 0.06f;
        MeshInstance3D hostileBase = new()
        {
            Name = "HostileAffiliationMarker",
            Mesh = new BoxMesh { Size = boxSize },
            Position = new Vector3(0, posY, 0),
            Rotation = new Vector3(0, Mathf.DegToRad(45), 0),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.85f, 0.2f, 0.15f, 0.6f),
                Roughness = 0.9f,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                DisableFog = true
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        model.AddChild(hostileBase);
    }
}
