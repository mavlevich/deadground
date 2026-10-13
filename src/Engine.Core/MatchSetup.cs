using System.Buffers.Binary;

namespace DeadGround.Engine;

public sealed record MatchSetup(
    byte ProtocolVersion,
    ulong ContentHash,
    ulong MapHash,
    string RuleSetId,
    int[]? PlayerTeams = null)
{
    public const byte CurrentProtocolVersion = 8;

    public bool Matches(MatchSetup other)
    {
        if (ProtocolVersion != other.ProtocolVersion ||
            ContentHash != other.ContentHash ||
            MapHash != other.MapHash ||
            !string.Equals(RuleSetId, other.RuleSetId, StringComparison.Ordinal))
        {
            return false;
        }

        if (PlayerTeams is not null && other.PlayerTeams is not null)
        {
            if (!PlayerTeams.AsSpan().SequenceEqual(other.PlayerTeams))
                return false;
        }

        return true;
    }

    public static ulong ComputeMapHash(HeightMap map) => ComputeMapHash(map, ReadOnlySpan<OpaqueStructure>.Empty);

    public static ulong ComputeMapHash(StaticGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        OpaqueStructure[] array = geometry.Structures.ToArray();
        return ComputeMapHash(geometry.HeightMap, array.AsSpan());
    }

    public static ulong ComputeMapHash(HeightMap map, ReadOnlySpan<OpaqueStructure> structures)
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

        if (structures.Length > 0)
        {
            // Canonical ordering by StructureId to prevent input record permutation differences
            OpaqueStructure[] sorted = structures.ToArray();
            Array.Sort(sorted, static (a, b) => a.Id.Value.CompareTo(b.Id.Value));

            hash = HashInt(hash, sorted.Length);
            for (int i = 0; i < sorted.Length; i++)
            {
                OpaqueStructure s = sorted[i];
                hash = HashInt(hash, (int)s.Id.Value);
                hash = HashFloat(hash, s.MinX);
                hash = HashFloat(hash, s.MinY);
                hash = HashFloat(hash, s.MinZ);
                hash = HashFloat(hash, s.MaxX);
                hash = HashFloat(hash, s.MaxY);
                hash = HashFloat(hash, s.MaxZ);
                hash = HashInt(hash, s.IsGrounded ? 1 : 0);
            }
        }

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
