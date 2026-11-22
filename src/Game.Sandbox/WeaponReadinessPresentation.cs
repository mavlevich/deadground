namespace DeadGround.Sandbox;

public enum WeaponReadinessState { Unarmed, Ready, Reloading, Empty }

// Presentation of own-unit observations, never a prediction of a combat outcome.
public readonly record struct WeaponReadinessPresentation(WeaponReadinessState State, float Progress)
{
    public static WeaponReadinessPresentation From(bool armed, int ammo, int remainingTicks, int durationTicks)
    {
        if (!armed) return new(WeaponReadinessState.Unarmed, 0);
        if (ammo <= 0) return new(WeaponReadinessState.Empty, 0);
        if (remainingTicks <= 0) return new(WeaponReadinessState.Ready, 1);
        float progress = durationTicks > 0 ? Math.Clamp(1f - remainingTicks / (float)durationTicks, 0f, 1f) : 0;
        return new(WeaponReadinessState.Reloading, progress);
    }

    public string Label => State switch
    {
        WeaponReadinessState.Ready => "READY",
        WeaponReadinessState.Reloading => "RELOADING",
        WeaponReadinessState.Empty => "NO AMMO",
        _ => "UNARMED"
    };
}
