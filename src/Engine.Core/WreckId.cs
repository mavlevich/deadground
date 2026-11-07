namespace DeadGround.Engine;

public readonly record struct WreckId(uint Value) : IComparable<WreckId>
{
    public override string ToString() => $"Wreck#{Value}";
    public int CompareTo(WreckId other) => Value.CompareTo(other.Value);
}
