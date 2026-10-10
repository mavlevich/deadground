using System.Buffers.Binary;

namespace DeadGround.Engine;

public sealed record MatchSetup(
    byte ProtocolVersion,
    ulong ContentHash,
    ulong MapHash,
    string RuleSetId)
{
    public const byte CurrentProtocolVersion = 1;

    public bool Matches(MatchSetup other) =>
        ProtocolVersion == other.ProtocolVersion &&
        ContentHash == other.ContentHash &&
        MapHash == other.MapHash &&
        string.Equals(RuleSetId, other.RuleSetId, StringComparison.Ordinal);

    public static ulong ComputeMapHash(HeightMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        ulong hash = 14695981039346656037UL;
        hash = HashInt(hash, map.Columns);
        hash = HashInt(hash, map.Rows);
        hash = HashFloat(hash, map.CellSize);
        hash = HashFloat(hash, map.HeightScale);
        for (int z = 0; z < map.Rows; z++)
        for (int x = 0; x < map.Columns; x++)
            hash = HashFloat(hash, map.GetHeight(x * map.CellSize, z * map.CellSize));
        return hash;
    }

    public static ulong ComputeStringHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ulong hash = 14695981039346656037UL;
        foreach (char c in value)
        {
            hash ^= (byte)c;
            hash *= 1099511628211UL;
            hash ^= (byte)(c >> 8);
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong HashInt(ulong hash, int val)
    {
        hash ^= (ulong)(uint)val;
        return hash * 1099511628211UL;
    }

    private static ulong HashFloat(ulong hash, float val)
    {
        hash ^= (ulong)BitConverter.SingleToUInt32Bits(val);
        return hash * 1099511628211UL;
    }
}
