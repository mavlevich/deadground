namespace DeadGround.Engine;

/// <summary>
/// Identifies what kind of obstacle obstructed line of sight.
/// </summary>
public enum ObstructionKind : byte
{
    None = 0,
    Terrain = 1,
    Structure = 2
}
