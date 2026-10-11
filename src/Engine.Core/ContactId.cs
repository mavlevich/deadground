namespace DeadGround.Engine;

/// <summary>
/// Strongly typed identifier for a perceived contact issued per side.
/// Distinct from EntityId to guarantee server truth identity never leaks to clients.
/// </summary>
public readonly record struct ContactId(uint Value)
{
    public bool IsValid => Value != 0;

    public override string ToString() => $"Contact#{Value}";
}
