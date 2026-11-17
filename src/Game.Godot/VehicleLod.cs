using Godot;

namespace DeadGround.Client;

/// <summary>
/// Shared vehicle detail level selection, animation handoff and transition budgeting.
/// </summary>
internal static class VehicleLod
{
    internal const float Lod1CameraSize = 45f;
    internal const float Lod2CameraSize = 95f;
    internal const float Hysteresis = 2.5f;
    internal const int DefaultMaxInstantiationsPerFrame = 8;

    internal const float EnterLod1Size = Lod1CameraSize + Hysteresis;
    internal const float ExitLod1Size = Lod1CameraSize - Hysteresis;
    internal const float EnterLod2Size = Lod2CameraSize + Hysteresis;
    internal const float ExitLod2Size = Lod2CameraSize - Hysteresis;

    internal static int SelectInitialLod(float cameraSize, int lodCount)
    {
        if (lodCount <= 1 || cameraSize <= 0f) return 0;
        int lod = cameraSize < Lod1CameraSize ? 0 : cameraSize < Lod2CameraSize ? 1 : 2;
        return Math.Min(lod, lodCount - 1);
    }

    internal static int SelectNextLod(float cameraSize, int currentLod, int lodCount)
    {
        if (lodCount <= 1 || cameraSize <= 0f) return 0;
        if (currentLod < 0 || currentLod >= lodCount) return SelectInitialLod(cameraSize, lodCount);

        int target = currentLod switch
        {
            0 => cameraSize >= EnterLod2Size ? 2 : (cameraSize >= EnterLod1Size ? 1 : 0),
            1 => cameraSize < ExitLod1Size ? 0 : (cameraSize >= EnterLod2Size ? 2 : 1),
            _ => cameraSize < ExitLod1Size ? 0 : (cameraSize < ExitLod2Size ? 1 : 2)
        };
        return Math.Min(target, lodCount - 1);
    }

    internal static void TransferAnimationState(
        AnimationPlayer? from,
        AnimationPlayer? to,
        StringName? fallbackClip = null,
        float fallbackSpeedScale = 1.0f)
    {
        StringName? clip = null;
        double position = 0.0;
        float speedScale = fallbackSpeedScale;
        bool shouldPause = false;

        if (from is not null && !string.IsNullOrEmpty(from.CurrentAnimation))
        {
            clip = from.CurrentAnimation;
            position = from.CurrentAnimationPosition;
            speedScale = from.SpeedScale;
            shouldPause = !from.IsPlaying();
        }
        else if (fallbackClip is not null && !fallbackClip.IsEmpty)
        {
            clip = fallbackClip;
        }

        if (from is not null && !ReferenceEquals(from, to) && from.IsPlaying())
        {
            from.Stop(keepState: false);
        }

        if (to is null || ReferenceEquals(from, to) || clip is null || clip.IsEmpty || !to.HasAnimation(clip))
        {
            return;
        }

        to.Play(clip);
        to.Seek(position, update: true);
        to.SpeedScale = speedScale;
        if (shouldPause)
        {
            to.Pause();
        }
    }
}
