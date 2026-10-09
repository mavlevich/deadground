namespace DeadGround.Engine;

public sealed class PlayerKnowledge
{
    private readonly OwnUnitObservation[] _ownUnits;
    public int Player { get; }
    public int Count { get; private set; }
    public ReadOnlySpan<OwnUnitObservation> OwnUnits => _ownUnits.AsSpan(0, Count);

    public PlayerKnowledge(int player, int capacity = 1024)
    {
        if (player < 1) throw new ArgumentOutOfRangeException(nameof(player));
        Player = player;
        _ownUnits = new OwnUnitObservation[capacity];
    }

    internal void Refresh(WorldState world)
    {
        int count = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (unit.Owner != Player) continue;
            if (count == _ownUnits.Length) throw new InvalidOperationException("Knowledge capacity exceeded.");
            _ownUnits[count++] = new OwnUnitObservation(unit.Id, unit.Definition, unit.Position, 0);
        }
        Count = count;
    }
}
