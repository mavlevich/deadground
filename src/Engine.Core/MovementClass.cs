namespace DeadGround.Engine;

/// <summary>
/// Coarse movement and clearance class used for hierarchical navigation graphs.
/// </summary>
public enum MovementClass : byte
{
    Foot = 0,     // Small / infantry: clearance radius 0.4 m (corridor width >= 0.8 m)
    Wheeled = 1,  // Light / wheeled recon: clearance radius 0.8 m (corridor width >= 1.6 m)
    Medium = 2,   // Medium / standard vehicle: clearance radius 1.0 m (corridor width >= 2.0 m)
    Tracked = 3   // Heavy / tracked tank: clearance radius 1.4 m (corridor width >= 2.8 m)
}

public static class MovementClassExtensions
{
    public const float FootRadius = 0.4f;
    public const float WheeledRadius = 0.8f;
    public const float MediumRadius = 1.0f;
    public const float TrackedRadius = 1.4f;

    public static float GetClearanceRadius(this MovementClass movementClass) => movementClass switch
    {
        MovementClass.Foot => FootRadius,
        MovementClass.Wheeled => WheeledRadius,
        MovementClass.Medium => MediumRadius,
        MovementClass.Tracked => TrackedRadius,
        _ => TrackedRadius
    };

    public static MovementClass FromRadius(float radius)
    {
        if (radius <= FootRadius) return MovementClass.Foot;
        if (radius <= WheeledRadius) return MovementClass.Wheeled;
        if (radius <= MediumRadius) return MovementClass.Medium;
        return MovementClass.Tracked;
    }
}
