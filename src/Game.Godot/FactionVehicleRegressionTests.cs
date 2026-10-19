using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

// Faction vehicle presentation: model choice, LOD switching, track motion and imported materials.
internal static class FactionVehicleRegressionTests
{
    private const string WeatheringShader = "res://Assets/Vehicles/Shared/vehicle_weathering.gdshader";
    private const string MotionShader = "res://Assets/Vehicles/Shared/vehicle_weathering_motion.gdshader";

    internal static void Run(Node3D scene)
    {
        CheckFactionModels(scene);
        CheckLodSwitching(scene);
        CheckTrackTravel(scene);
        CheckImportedMaterials();
        CheckEnemyTankFaction();
        CheckEnemyContactFactions();
    }

    private static void CheckFactionModels(Node3D scene)
    {
        UnitView soviet = new("prototype_tank", "soviet");
        UnitView us = new("prototype_tank", "us");
        UnitView none = new("prototype_tank");
        UnitView unknown = new("prototype_tank", "unknown_faction");
        UnitView sovietApc = new("apc", "soviet");
        UnitView usApc = new("apc", "us");
        UnitView sovietRecon = new("recon_vehicle", "soviet");
        try
        {
            Require(soviet.LodCount == 3 && soviet.GetLod(0)!.SceneFilePath.EndsWith("T62Alt/t62alt.glb", StringComparison.Ordinal),
                "Soviet tank did not use the T-62 with three LODs.");
            Require(us.LodCount == 3 && us.GetLod(0)!.SceneFilePath.EndsWith("M60/m60.glb", StringComparison.Ordinal),
                "US tank did not use the M60 with three LODs.");
            Require(none.LodCount == 1 && none.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/tank.glb", StringComparison.Ordinal),
                "A match without factions must keep the prototype tank.");
            Require(unknown.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/tank.glb", StringComparison.Ordinal),
                "An unknown faction must fall back to the base scene.");
            Require(soviet.Model.FindChild("TurretYaw", true, false) is not null, "Faction tank lost its turret pivot.");

            Require(sovietApc.LodCount == 3 && sovietApc.GetLod(0)!.SceneFilePath.EndsWith("BTR70/btr70.glb", StringComparison.Ordinal),
                "Soviet APC did not use the BTR-70 with three LODs.");
            Require(usApc.LodCount == 1 && usApc.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "US APC must keep the placeholder until its own model exists.");
            Require(sovietRecon.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "The BTR-70 is an APC; Soviet recon must not use it.");
            Require(sovietApc.LeftMotionMeshCount > 0 && sovietApc.RightMotionMeshCount > 0,
                "BTR-70 wheel motion meshes were not found.");
            Require(sovietApc.Model.FindChild("TurretYaw", true, false) is not null, "BTR-70 lost its turret pivot.");
        }
        finally
        {
            soviet.Root.Free();
            us.Root.Free();
            none.Root.Free();
            unknown.Root.Free();
            sovietApc.Root.Free();
            usApc.Root.Free();
            sovietRecon.Root.Free();
        }
    }

    private static void CheckLodSwitching(Node3D scene)
    {
        UnitView view = new("prototype_tank", "soviet");
        scene.AddChild(view.Root);
        try
        {
            Require(view.ActiveLod == 0 && view.GetLod(0)!.Visible, "LOD0 must start visible.");
            Require(!view.IsLodLoaded(1) && !view.IsLodLoaded(2), "Unused LODs must not be instantiated up front.");
            view.UpdatePosition(new Vector3(0, 0, 0));
            view.UpdatePosition(new Vector3(0, 0, -5));
            view.AimAt(new Vector3(10, 0, -5));
            view.ApplyRecoil(Vector3.Forward);
            view.UpdateLod(75f);
            Require(view.ActiveLod == 1 && view.GetLod(1)!.Visible && !view.GetLod(0)!.Visible, "RTS zoom must show LOD1.");
            Require(!view.IsLodLoaded(2), "LOD2 must wait until the distant zoom.");
            view.UpdateLod(160f);
            Require(view.ActiveLod == 2 && view.GetLod(2)!.Visible && !view.GetLod(1)!.Visible, "Distant zoom must show LOD2.");
            foreach (int lod in new[] { 0, 1, 2 })
            {
                Node3D turret = (Node3D)view.GetLod(lod)!.FindChild("TurretYaw", true, false)!;
                Require(Mathf.IsEqualApprox(turret.Rotation.Y, -Mathf.Pi / 2), $"LOD{lod} turret did not take the current aim.");
                Node3D barrel = (Node3D)view.GetLod(lod)!.FindChild("BarrelRecoil", true, false)!;
                Require(barrel.Position.Z > 0.3f, $"LOD{lod} barrel did not take the current recoil.");
            }
            view.UpdateLod(38f);
            Require(view.ActiveLod == 0 && view.GetLod(0)!.Visible && !view.GetLod(2)!.Visible, "Close zoom must return to LOD0.");
            Require(view.TryGetMuzzlePosition(out Vector3 muzzle) && muzzle.Z < -5, "Muzzle must come from the visible LOD.");
        }
        finally { view.Root.Free(); }
    }

    private static void CheckTrackTravel(Node3D scene)
    {
        UnitView view = new("prototype_tank", "us");
        scene.AddChild(view.Root);
        try
        {
            Require(view.LeftMotionMeshCount > 0 && view.RightMotionMeshCount > 0, "Track motion meshes were not found.");
            view.SetHullHeading(0f); // the spawn heading is not a turn
            Require(view.LeftTrackTravel == 0f && view.RightTrackTravel == 0f, "The spawn heading must not move the tracks.");
            view.UpdatePosition(new Vector3(0, 0, 0));
            view.UpdatePosition(new Vector3(0, 0, -3));
            // 3 world meters at visual scale 0.75 is 4 model meters on both sides.
            Require(Mathf.IsEqualApprox(view.LeftTrackTravel, 4f) && Mathf.IsEqualApprox(view.RightTrackTravel, 4f),
                "Straight driving must move both tracks by the same model distance.");
            Require(Mathf.IsEqualApprox(view.GetShaderTrackTravel(left: true), 4f), "Track travel did not reach the shader.");
            // A gentle left turn: the right track runs farther than the left one.
            view.SetHullHeading(0.1f);
            view.UpdatePosition(new Vector3(-0.3f, 0, -6));
            Require(view.RightTrackTravel - view.LeftTrackTravel > 0.05f, "A left turn must drive the right track farther.");
            float before = view.LeftTrackTravel + view.RightTrackTravel;
            float differentialBefore = view.RightTrackTravel - view.LeftTrackTravel;
            // A 180 degree heading snap is clamped to 0.5 m per side instead of spinning the tracks.
            view.SetHullHeading(0.1f - Mathf.Pi);
            view.UpdatePosition(new Vector3(-0.3f, 0, -5.9f));
            float differentialChange = Mathf.Abs(view.RightTrackTravel - view.LeftTrackTravel - differentialBefore);
            Require(differentialChange <= 1.0001f, "Heading snaps must not spin the tracks.");
            Require(view.LeftTrackTravel + view.RightTrackTravel > before, "Tracks must keep moving forward along the path.");
            view.UpdateLod(75f);
            Require(Mathf.IsEqualApprox(view.GetShaderTrackTravel(left: false), view.RightTrackTravel),
                "Switching LOD must carry the track travel over.");
            // Long drives wrap at the exported period (993.18 m for the M60) instead of growing without bound.
            view.UpdatePosition(new Vector3(-0.3f, 0, -6.0f - 800f));
            Require(view.LeftTrackTravel >= 0f && view.LeftTrackTravel < 993.2f && view.RightTrackTravel < 993.2f,
                "Track travel must wrap at the exported period.");
        }
        finally { view.Root.Free(); }
    }

    private static void CheckImportedMaterials()
    {
        foreach ((string path, bool camo) in new[]
        {
            ("res://Assets/Vehicles/T62Alt/t62alt.glb", false),
            ("res://Assets/Vehicles/M60/m60.glb", true),
            ("res://Assets/Vehicles/BTR70/btr70.glb", false)
        })
        {
            Node3D model = GD.Load<PackedScene>(path).Instantiate<Node3D>();
            try
            {
                bool foundPaint = false;
                bool foundMarkings = false;
                bool foundMotion = false;
                foreach (Node node in model.FindChildren("*", "MeshInstance3D", recursive: true, owned: false))
                {
                    Mesh mesh = ((MeshInstance3D)node).Mesh;
                    bool motion = node.Name.ToString().Contains("_Motion_", StringComparison.Ordinal);
                    for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                    {
                        // Only running gear reserves the per-instance travel parameter.
                        if (mesh.SurfaceGetMaterial(surface) is ShaderMaterial weathered)
                        {
                            Require(weathered.Shader.ResourcePath == (motion ? MotionShader : WeatheringShader),
                                $"{path} {node.Name} uses the wrong weathering shader variant.");
                            foundMotion |= motion;
                        }
                    }
                    for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                    {
                        Material material = mesh.SurfaceGetMaterial(surface);
                        if (material.ResourceName.EndsWith("_Paint", StringComparison.Ordinal))
                        {
                            ShaderMaterial? shader = material as ShaderMaterial;
                            Require(shader is not null, $"{path} paint is not on the weathering shader.");
                            float camoStrength = shader!.GetShaderParameter("camo_strength").AsSingle();
                            Require(camo ? camoStrength == 1f : camoStrength == 0f, $"{path} has the wrong camouflage setting.");
                            foundPaint = true;
                        }
                        if (material.ResourceName.EndsWith("_Markings", StringComparison.Ordinal))
                        {
                            Require(material is StandardMaterial3D { AlbedoTexture: not null }, $"{path} markings lost their texture.");
                            foundMarkings = true;
                        }
                    }
                }
                Require(foundPaint && foundMarkings && foundMotion, $"{path} is missing paint, markings or running gear motion.");
            }
            finally { model.Free(); }
        }
    }

    private static void CheckEnemyTankFaction()
    {
        MatchSetup setup = new(MatchSetup.CurrentProtocolVersion, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Node3D us = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Tank, setup.GetHostileFaction(1))!;
        Node3D soviet = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Tank, setup.GetHostileFaction(2))!;
        Node3D generic = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Tank)!;
        try
        {
            Require(us.Name == "EnemyTankModel" && us.GetChild<Node3D>(0).SceneFilePath.EndsWith("M60/m60_lod1.glb", StringComparison.Ordinal),
                "Player 1 must see identified enemy tanks as M60s.");
            Require(soviet.GetChild<Node3D>(0).SceneFilePath.EndsWith("T62Alt/t62alt_lod1.glb", StringComparison.Ordinal),
                "Player 2 must see identified enemy tanks as T-62s.");
            Require(generic.GetChild<Node3D>(0).SceneFilePath.EndsWith("Vehicles/tank.glb", StringComparison.Ordinal),
                "Without factions enemy tanks keep the generic model.");
            Require(us.FindChild("HostileAffiliationMarker", true, false) is not null, "Faction enemy tank lost its hostile marker.");
        }
        finally
        {
            us.Free();
            soviet.Free();
            generic.Free();
        }
    }

    private static void CheckEnemyContactFactions()
    {
        MatchSetup setup = new(MatchSetup.CurrentProtocolVersion, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Node3D usApc = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Apc, setup.GetHostileFaction(1))!;
        Node3D sovietApc = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Apc, setup.GetHostileFaction(2))!;
        Node3D sovietRecon = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Recon, setup.GetHostileFaction(2))!;
        Node3D generic = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Apc)!;
        try
        {
            Require(sovietApc.Name == "EnemyApcModel" && sovietApc.GetChild<Node3D>(0).SceneFilePath.EndsWith("BTR70/btr70_lod1.glb", StringComparison.Ordinal),
                "Player 2 must see an identified enemy APC as BTR-70 LOD1.");
            Require(usApc.Name == "EnemyApcModel" && usApc.GetChild<Node3D>(0).SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "Player 1 must see a US APC with the placeholder model.");
            Require(sovietRecon.Name == "EnemyReconModel" && sovietRecon.GetChild<Node3D>(0).SceneFilePath.EndsWith("BRDM2/brdm2_lod1.glb", StringComparison.Ordinal),
                "Identified enemy Soviet recon must use the BRDM-2, not the BTR-70.");
            Require(generic.GetChild<Node3D>(0).SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "Without factions an enemy APC keeps the generic model.");
            Require(sovietApc.FindChild("HostileAffiliationMarker", true, false) is not null, "Enemy APC lost its hostile marker.");
            Require(sovietRecon.FindChild("HostileAffiliationMarker", true, false) is not null, "Enemy recon lost its hostile marker.");
        }
        finally
        {
            usApc.Free();
            sovietApc.Free();
            sovietRecon.Free();
            generic.Free();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
