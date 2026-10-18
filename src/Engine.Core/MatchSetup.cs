using System.Buffers.Binary;

namespace DeadGround.Engine;

public sealed record MatchSetup(
    byte ProtocolVersion,
    ulong ContentHash,
    ulong MapHash,
    string RuleSetId,
    int[]? PlayerTeams = null,
    string[]? PlayerFactions = null)
{
    public const byte CurrentProtocolVersion = 10;

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

        if (PlayerFactions is not null && other.PlayerFactions is not null)
        {
            if (PlayerFactions.Length != other.PlayerFactions.Length)
                return false;
            for (int i = 0; i < PlayerFactions.Length; i++)
            {
                if (!string.Equals(PlayerFactions[i], other.PlayerFactions[i], StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    // Faction content ID for a player (1-based), or null when the match does not assign factions.
    public string? GetPlayerFaction(int player) =>
        PlayerFactions is not null && player >= 1 && player <= PlayerFactions.Length ? PlayerFactions[player - 1] : null;

    // The single faction fielded by every hostile player, or null when there is none or more than one.
    // Factions are public match information, so this reveals nothing beyond the setup header.
    public string? GetHostileFaction(int player)
    {
        if (PlayerFactions is null || player < 1 || player > PlayerFactions.Length)
            return null;
        int ownTeam = GetTeam(player);
        string? hostile = null;
        for (int other = 1; other <= PlayerFactions.Length; other++)
        {
            if (other == player || GetTeam(other) == ownTeam)
                continue;
            string faction = PlayerFactions[other - 1];
            if (hostile is null)
                hostile = faction;
            else if (!string.Equals(hostile, faction, StringComparison.Ordinal))
                return null;
        }
        return hostile;
    }

    private int GetTeam(int player) => ResolveTeam(PlayerTeams, player);

    // Team of a player (1-based): the assigned team when positive, otherwise the player's own team.
    // WorldState uses the same rule, so client presentation and server hostility always agree.
    public static int ResolveTeam(ReadOnlySpan<int> teams, int player) =>
        player >= 1 && player <= teams.Length && teams[player - 1] > 0 ? teams[player - 1] : player;

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
