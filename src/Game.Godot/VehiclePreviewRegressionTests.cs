using System.Text.Json;
using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Regression tests validating T-62 model geometry, functional hierarchy,
/// historical reference anchors, LOD integrity, and vehicle preview view lifecycle.
/// </summary>
internal static class VehiclePreviewRegressionTests
{
    internal static bool Run(Node3D scene)
    {
        try
        {
            CheckNodeHierarchyAndPivots(scene);
            CheckReferenceAnchorsAndDimensions(scene);
            CheckArticulationAndRecoilIsolation(scene);
            CheckLodPolicyAndManifest(scene);
            CheckImportedGeometryAndMaterials(scene);
            CheckPreviewViewLifecycle(scene);
            GD.Print("VEHICLE_PREVIEW_REGRESSIONS_OK 6/6 passed: hierarchy anchors articulation lods geometry_materials view_lifecycle");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            return false;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckNodeHierarchyAndPivots(Node3D scene)
    {
        string[] lodPaths =
        [
            "res://Assets/Vehicles/T62/t62.glb",
            "res://Assets/Vehicles/T62/t62_lod1.glb",
            "res://Assets/Vehicles/T62/t62_lod2.glb"
        ];

        for (int lod = 0; lod < lodPaths.Length; lod++)
        {
            PackedScene? packed = GD.Load<PackedScene>(lodPaths[lod]);
            Require(packed is not null, $"Failed to load T-62 model LOD {lod} from {lodPaths[lod]}");

            Node3D model = packed!.Instantiate<Node3D>();
            scene.AddChild(model);
            try
            {
                Node3D? hull = model.FindChild("Hull", true, false) as Node3D;
                Node3D? leftTrack = model.FindChild("LeftTrack", true, false) as Node3D;
                Node3D? rightTrack = model.FindChild("RightTrack", true, false) as Node3D;
                Node3D? turretYaw = model.FindChild("TurretYaw", true, false) as Node3D;

                Require(hull is not null, $"LOD {lod} is missing Hull functional node.");
                Require(leftTrack is not null, $"LOD {lod} is missing LeftTrack functional node.");
                Require(rightTrack is not null, $"LOD {lod} is missing RightTrack functional node.");
                Require(turretYaw is not null, $"LOD {lod} is missing TurretYaw functional node.");

                Node3D? gunPitch = turretYaw!.FindChild("GunPitch", true, false) as Node3D;
                Require(gunPitch is not null, $"LOD {lod} is missing GunPitch under TurretYaw.");
                Require(ReferenceEquals(gunPitch!.GetParent(), turretYaw),
                    $"LOD {lod} GunPitch must be a direct child of TurretYaw.");

                Node3D? barrelRecoil = gunPitch!.FindChild("BarrelRecoil", true, false) as Node3D;
                Require(barrelRecoil is not null, $"LOD {lod} is missing BarrelRecoil under GunPitch.");
                Require(ReferenceEquals(barrelRecoil!.GetParent(), gunPitch),
                    $"LOD {lod} BarrelRecoil must be a direct child of GunPitch.");

                Node3D? muzzle = barrelRecoil!.FindChild("Muzzle", true, false) as Node3D;
                Require(muzzle is not null, $"LOD {lod} is missing Muzzle under BarrelRecoil.");
                Require(ReferenceEquals(muzzle!.GetParent(), barrelRecoil),
                    $"LOD {lod} Muzzle must be a direct child of BarrelRecoil.");
            }
            finally
            {
                model.QueueFree();
            }
        }
    }

    private static void CheckReferenceAnchorsAndDimensions(Node3D scene)
    {
        PackedScene? packed = GD.Load<PackedScene>("res://Assets/Vehicles/T62/t62.glb");
        Require(packed is not null, "Failed to load LOD0 t62.glb for dimensional validation.");

        Node3D model = packed!.Instantiate<Node3D>();
        scene.AddChild(model);
        try
        {
            Node3D turretYaw = (Node3D)model.FindChild("TurretYaw", true, false)!;
            Node3D gunPitch = (Node3D)turretYaw.FindChild("GunPitch", true, false)!;
            Node3D barrelRecoil = (Node3D)gunPitch.FindChild("BarrelRecoil", true, false)!;
            Node3D muzzle = (Node3D)barrelRecoil.FindChild("Muzzle", true, false)!;

            // 1. Turret ring interface position
            // Documented: Y = 1.48 m, Z = -0.38 m
            Vector3 turretPos = turretYaw.Position;
            Require(MathF.Abs(turretPos.X) < 0.01f, $"TurretYaw X was {turretPos.X}, expected 0.0");
            Require(MathF.Abs(turretPos.Y - 1.480f) < 0.05f, $"TurretYaw Y was {turretPos.Y}, expected 1.480 m");
            Require(MathF.Abs(turretPos.Z - (-0.380f)) < 0.05f, $"TurretYaw Z was {turretPos.Z}, expected -0.380 m");

            // 2. Gun trunnion pivot position relative to TurretYaw
            // Documented: local Y = 0.37 m, local Z = -1.05 m (world Y = 1.85 m, world Z = -1.43 m)
            Vector3 pitchPos = gunPitch.Position;
            Require(MathF.Abs(pitchPos.X) < 0.01f, $"GunPitch local X was {pitchPos.X}, expected 0.0");
            Require(MathF.Abs(pitchPos.Y - 0.370f) < 0.05f, $"GunPitch local Y was {pitchPos.Y}, expected 0.370 m");
            Require(MathF.Abs(pitchPos.Z - (-1.050f)) < 0.05f, $"GunPitch local Z was {pitchPos.Z}, expected -1.050 m");

            // 3. Muzzle position relative to GunPitch
            // Documented: local Z = -4.59 m (overall length gun forward = 9.335 m, muzzle world Z = -6.02 m)
            Vector3 muzzlePos = muzzle.Position;
            Require(MathF.Abs(muzzlePos.X) < 0.01f, $"Muzzle local X was {muzzlePos.X}, expected 0.0");
            Require(MathF.Abs(muzzlePos.Y) < 0.01f, $"Muzzle local Y was {muzzlePos.Y}, expected 0.0");
            Require(MathF.Abs(muzzlePos.Z - (-4.590f)) < 0.05f, $"Muzzle local Z was {muzzlePos.Z}, expected -4.590 m");

            // 4. Muzzle world position check
            Vector3 muzzleGlobal = muzzle.GlobalPosition;
            Require(MathF.Abs(muzzleGlobal.Y - 1.850f) < 0.05f,
                $"Muzzle world height Y was {muzzleGlobal.Y}, expected 1.850 m");
            Require(MathF.Abs(muzzleGlobal.Z - (-6.020f)) < 0.05f,
                $"Muzzle world Z was {muzzleGlobal.Z}, expected -6.020 m");

            // 5. Total vehicle length with gun forward: 9.335 m
            // Rear hull plate is at Z = +3.315 m. Distance from rear plate to muzzle tip = 3.315 - (-6.020) = 9.335 m
            float totalLength = 3.315f - muzzleGlobal.Z;
            Require(MathF.Abs(totalLength - 9.335f) < 0.05f,
                $"Total vehicle length with gun forward was {totalLength} m, expected 9.335 m");
        }
        finally
        {
            model.QueueFree();
        }
    }

    private static void CheckArticulationAndRecoilIsolation(Node3D scene)
    {
        PackedScene? packed = GD.Load<PackedScene>("res://Assets/Vehicles/T62/t62.glb");
        Require(packed is not null, "Failed to load t62.glb for articulation validation.");

        Node3D model = packed!.Instantiate<Node3D>();
        scene.AddChild(model);
        try
        {
            Node3D hull = (Node3D)model.FindChild("Hull", true, false)!;
            Node3D leftTrack = (Node3D)model.FindChild("LeftTrack", true, false)!;
            Node3D rightTrack = (Node3D)model.FindChild("RightTrack", true, false)!;
            Node3D turretYaw = (Node3D)model.FindChild("TurretYaw", true, false)!;
            Node3D gunPitch = (Node3D)turretYaw.FindChild("GunPitch", true, false)!;
            Node3D barrelRecoil = (Node3D)gunPitch.FindChild("BarrelRecoil", true, false)!;
            Node3D muzzle = (Node3D)barrelRecoil.FindChild("Muzzle", true, false)!;

            Vector3 hullInitialRot = hull.Rotation;
            Vector3 leftTrackInitialRot = leftTrack.Rotation;
            Vector3 rightTrackInitialRot = rightTrack.Rotation;
            Vector3 barrelBindPos = barrelRecoil.Position;

            // 1. Rotate turret yaw 45 degrees
            turretYaw.RotationDegrees = new Vector3(0, 45f, 0);
            Require(hull.Rotation == hullInitialRot, "Rotating turret rotated the hull.");
            Require(leftTrack.Rotation == leftTrackInitialRot, "Rotating turret rotated the left track.");
            Require(rightTrack.Rotation == rightTrackInitialRot, "Rotating turret rotated the right track.");

            // 2. Rotate gun pitch to elevation (+12 deg) and depression (-5 deg)
            gunPitch.RotationDegrees = new Vector3(12f, 0, 0);
            Require(turretYaw.RotationDegrees == new Vector3(0, 45f, 0), "Elevating gun rotated the turret.");
            gunPitch.RotationDegrees = new Vector3(-5f, 0, 0);
            Require(turretYaw.RotationDegrees == new Vector3(0, 45f, 0), "Depressing gun rotated the turret.");

            // 3. Apply barrel recoil (+0.35m along local +Z)
            barrelRecoil.Position = barrelBindPos + new Vector3(0, 0, 0.35f);
            Require(hull.Position == Vector3.Zero, "Barrel recoil displaced the hull.");
            Require(turretYaw.Position == new Vector3(0, 1.48f, -0.38f), "Barrel recoil displaced the turret ring.");

            // 4. Restore bind pose exactly
            turretYaw.RotationDegrees = Vector3.Zero;
            gunPitch.RotationDegrees = Vector3.Zero;
            barrelRecoil.Position = barrelBindPos;

            Require(turretYaw.RotationDegrees == Vector3.Zero, "Turret did not return to bind pose.");
            Require(gunPitch.RotationDegrees == Vector3.Zero, "Gun pitch did not return to bind pose.");
            Require(barrelRecoil.Position == barrelBindPos, "Barrel recoil did not return to bind pose.");
            Require(MathF.Abs(muzzle.GlobalPosition.Z - (-6.020f)) < 0.05f, "Muzzle did not return to bind position.");
        }
        finally
        {
            model.QueueFree();
        }
    }

    private static void CheckLodPolicyAndManifest(Node3D scene)
    {
        string manifestJson = Godot.FileAccess.GetFileAsString("res://Assets/Vehicles/T62/manifest.json");
        Require(!string.IsNullOrEmpty(manifestJson), "T-62 manifest.json is missing or empty.");

        using JsonDocument doc = JsonDocument.Parse(manifestJson);
        JsonElement root = doc.RootElement;

        Require(root.TryGetProperty("lods", out JsonElement lodsArray), "Manifest is missing 'lods' array.");
        Require(lodsArray.GetArrayLength() == 3, $"Expected 3 LODs in manifest, found {lodsArray.GetArrayLength()}");

        JsonElement lod0 = lodsArray[0];
        JsonElement lod1 = lodsArray[1];
        JsonElement lod2 = lodsArray[2];

        int lod0Tris = lod0.GetProperty("triangles").GetInt32();
        int lod1Tris = lod1.GetProperty("triangles").GetInt32();
        int lod2Tris = lod2.GetProperty("triangles").GetInt32();

        // Check against soft budget ranges
        Require(lod0Tris > 0 && lod0Tris <= 9000,
            $"LOD0 triangles ({lod0Tris}) outside expected budget ceiling (9000).");
        Require(lod1Tris > 0 && lod1Tris <= 4800,
            $"LOD1 triangles ({lod1Tris}) outside expected budget ceiling (4800).");
        Require(lod2Tris > 0 && lod2Tris <= 2400,
            $"LOD2 triangles ({lod2Tris}) outside expected budget ceiling (2400).");

        Require(lod0Tris > lod1Tris, "LOD0 triangle count must be greater than LOD1.");
        Require(lod1Tris > lod2Tris, "LOD1 triangle count must be greater than LOD2.");
    }

    private static void CheckImportedGeometryAndMaterials(Node3D scene)
    {
        using JsonDocument manifest = JsonDocument.Parse(Godot.FileAccess.GetFileAsString("res://Assets/Vehicles/T62/manifest.json"));
        foreach (JsonElement entry in manifest.RootElement.GetProperty("lods").EnumerateArray())
        {
            string path = $"res://Assets/Vehicles/T62/{entry.GetProperty("file").GetString()}";
            Node3D model = GD.Load<PackedScene>(path).Instantiate<Node3D>();
            scene.AddChild(model);
            try
            {
                int triangles = 0;
                int surfaces = 0;
                bool sawTint = false;
                Aabb? bounds = null;
                foreach (Node node in model.FindChildren("*", "MeshInstance3D", true, false))
                {
                    MeshInstance3D view = (MeshInstance3D)node;
                    Transform3D transform = model.GlobalTransform.AffineInverse() * view.GlobalTransform;
                    Mesh mesh = view.Mesh;
                    for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                    {
                        surfaces++;
                        using Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
                        Vector3[] positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                        Color[] colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
                        triangles += indices.Length / 3;
                        Require(normals.Length == positions.Length, "Imported normals are missing.");
                        Require(colors.Length == positions.Length, "Imported material tints are missing.");
                        for (int vertex = 0; vertex < positions.Length; vertex++)
                        {
                            Require(positions[vertex].IsFinite(), "Imported model contains a non-finite vertex.");
                            Require(MathF.Abs(normals[vertex].LengthSquared() - 1) < 0.01f, "Imported normal is not a unit vector.");
                            sawTint |= colors[vertex].R < 0.99f;
                            Vector3 point = transform * positions[vertex];
                            bounds = bounds is null ? new Aabb(point, Vector3.Zero) : bounds.Value.Expand(point);
                        }
                        for (int triangle = 0; triangle < indices.Length; triangle += 3)
                        {
                            int a = indices[triangle], b = indices[triangle + 1], c = indices[triangle + 2];
                            Vector3 face = (positions[b] - positions[a]).Cross(positions[c] - positions[a]);
                            Require(face.LengthSquared() > 0.000000000001f, "Imported model contains a degenerate triangle.");
                            // Godot's imported front faces are clockwise.
                            Require(face.Dot(normals[a] + normals[b] + normals[c]) <= 0.00001f,
                                "Imported triangle winding disagrees with its normals.");
                        }
                        if (view.GetActiveMaterial(surface) is StandardMaterial3D material && material.ResourceName == "T62_Paint")
                        {
                            Require(material.VertexColorUseAsAlbedo, "Per-part paint tints are not enabled by the importer.");
                            Color expected = new("77745e");
                            Require(material.AlbedoColor.IsEqualApprox(expected), "Paint changed color during the sRGB/glTF round trip.");
                            Require(material.AlbedoTexture is NoiseTexture2D && material.Uv1Triplanar,
                                "Procedural paint variation was lost during import/export.");
                            Require(material.RoughnessTexture is NoiseTexture2D,
                                "Paint roughness variation was lost during import/export.");
                            bool closeDetail = entry.GetProperty("lod").GetInt32() == 0;
                            Require(material.NormalEnabled == closeDetail,
                                "Cast surface grain must be confined to the close model.");
                            if (closeDetail)
                                Require(material.NormalTexture is NoiseTexture2D,
                                    "Close-model surface grain was lost during import/export.");
                        }
                    }
                }
                Require(triangles == entry.GetProperty("triangles").GetInt32(), "Manifest triangle count differs from imported geometry.");
                Require(surfaces == entry.GetProperty("mesh_parts").GetInt32(), "Manifest surface count differs from imported geometry.");
                Require(sawTint, "Paint has lost its per-part tonal variation.");
                Require(bounds is not null && MathF.Abs(bounds.Value.Size.X - 3.3f) < 0.02f, "Model width changed from meter-scale reference.");
                Require(MathF.Abs(bounds!.Value.Position.Y) < 0.01f, "Tracks do not meet the ground plane.");
            }
            finally { model.QueueFree(); }
        }
    }

    private static void CheckPreviewViewLifecycle(Node3D scene)
    {
        VehiclePreviewView preview = new();
        scene.AddChild(preview);
        try
        {
            Require(preview.T62ModelInstance is not null, "Preview view did not instantiate T-62 model.");
            Require(preview.TurretYawNode is not null, "Preview view missing TurretYawNode reference.");
            Require(preview.GunPitchNode is not null, "Preview view missing GunPitchNode reference.");
            Require(preview.BarrelRecoilNode is not null, "Preview view missing BarrelRecoilNode reference.");
            Require(preview.MuzzleNode is not null, "Preview view missing MuzzleNode reference.");

            // Test camera preset switching
            preview.SelectPreset(0); // Front
            preview.SelectPreset(1); // Side Profile
            preview.SelectPreset(4); // Three-Quarter Hero
            preview.SelectPreset(5); // RTS Close
            preview.SelectPreset(6); // RTS Normal
            Require(preview.InspectionCamera.Projection == Camera3D.ProjectionType.Orthogonal && preview.InspectionCamera.Size == 75,
                "Normal RTS preview must use the gameplay orthographic scale.");
            preview.SelectPreset(7); // RTS Distant
            Require(preview.InspectionCamera.Size == 160, "Distant preview camera scale is incorrect.");

            // Test LOD switching
            preview.ApplyLod(1);
            Require(preview.EffectiveLod == 1 && preview.T62ModelInstance is not null, "Switching to LOD1 failed.");
            preview.ApplyLod(2);
            Require(preview.EffectiveLod == 2 && preview.T62ModelInstance is not null, "Switching to LOD2 failed.");
            preview.ApplyLod(0);
            Require(preview.EffectiveLod == 0 && preview.T62ModelInstance is not null, "Switching to LOD0 failed.");

            // Test articulation setters
            preview.SetTurretYaw(30f);
            preview.SetGunPitch(8f);
            preview.ResetPose();

            // Test firing combat effects
            preview.FireTestShot();
            preview._Process(0.05);
            preview._Process(0.15);
        }
        finally
        {
            preview.QueueFree();
        }
    }
}
