using Godot;

namespace DeadGround.Client;

// Validate the imported resource, including editor post-import settings, in the real runtime.
internal static class InfantryAssetRegressionTests
{
    private static readonly string[] Clips = ["Idle", "AimStanding", "AimKneeling", "AimProne", "Walk", "Run"];

    private static readonly (string Path, string MeshName, string Label)[] Assets =
    [
        ("res://Assets/Infantry/US/us_rifleman.glb", "USRifleman", "US rifleman"),
        ("res://Assets/Infantry/USSR/soviet_rifleman.glb", "SovietRifleman", "Soviet rifleman"),
        ("res://Assets/Infantry/USSR/soviet_rpg.glb", "SovietRpg", "Soviet RPG"),
    ];

    internal static void Run(Node3D scene)
    {
        foreach ((string path, string meshName, string label) in Assets)
            ValidateAsset(scene, path, meshName, label);
        GD.Print("INFANTRY_ASSET_REGRESSIONS_OK vertex_colors cyclic_clips skeleton_playback");
    }

    private static void ValidateAsset(Node3D scene, string path, string meshName, string label)
    {
        PackedScene? packed = GD.Load<PackedScene>(path);
        Require(packed is not null, $"{label} scene did not import.");
        Node3D model = packed!.Instantiate<Node3D>();
        scene.AddChild(model);
        try
        {
            MeshInstance3D? mesh = model.FindChild(meshName, true, false) as MeshInstance3D;
            Require(mesh?.Mesh is not null && mesh.Skin is not null, $"{label} lost its skinned mesh.");
            Require(mesh!.Mesh is ArrayMesh, $"{label} did not import as an array mesh.");
            ArrayMesh importedMesh = (ArrayMesh)mesh.Mesh;
            int coloredSurfaces = 0;
            for (int surface = 0; surface < importedMesh.GetSurfaceCount(); surface++)
            {
                if ((importedMesh.SurfaceGetFormat(surface) & Mesh.ArrayFormat.FormatColor) == 0) continue;
                coloredSurfaces++;
                BaseMaterial3D? material = mesh.GetActiveMaterial(surface) as BaseMaterial3D;
                Require(material is not null && material.VertexColorUseAsAlbedo,
                    $"{label} surface {surface} ignores its authored vertex colors.");
            }
            Require(coloredSurfaces > 0, $"{label} has no authored vertex colors.");

            AnimationPlayer? player = model.FindChild("AnimationPlayer", true, false) as AnimationPlayer;
            Skeleton3D? skeleton = FindSkeleton(model);
            Require(player is not null && skeleton is not null, $"{label} lost its animations or skeleton.");
            foreach (string clip in Clips)
            {
                Require(player!.HasAnimation(clip), $"{label} is missing {clip}.");
                Animation animation = player.GetAnimation(clip);
                Require(animation.Length > 0 && animation.LoopMode == Animation.LoopModeEnum.Linear,
                    $"{label} {clip} must loop after import.");
                player.Play(clip);
                player.Advance(0);
                player.Advance(animation.Length * 2.25);
                Require(player.IsPlaying(), $"{label} {clip} stopped after two cycles.");
                Require(Math.Abs(player.CurrentAnimationPosition - animation.Length * 0.25) < 0.002,
                    $"{label} {clip} did not wrap its playback position.");
                if (clip is "Walk" or "Run")
                {
                    int thigh = skeleton!.FindBone("thigh_l");
                    Require(thigh >= 0, $"{label} lost its leg bone.");
                    Quaternion before = skeleton.GetBonePoseRotation(thigh);
                    player.Advance(animation.Length * 0.25);
                    Require(before.AngleTo(skeleton.GetBonePoseRotation(thigh)) > 0.01f,
                        $"{label} {clip} does not animate the skeleton.");
                }
                player.Stop();
            }
        }
        finally
        {
            scene.RemoveChild(model);
            model.Free();
        }
    }

    private static Skeleton3D? FindSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton) return skeleton;
        foreach (Node child in node.GetChildren())
        {
            Skeleton3D? found = FindSkeleton(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
