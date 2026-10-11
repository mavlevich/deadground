namespace DeadGround.Engine;

/// <summary>
/// Stable identifier for a static map structure.
/// Distinct from dynamic simulation EntityId and transient contact IDs.
/// </summary>
public readonly record struct StructureId(int Value) : IComparable<StructureId>
{
    public int CompareTo(StructureId other) => Value.CompareTo(other.Value);

    public override string ToString() => $"Structure({Value})";
}
