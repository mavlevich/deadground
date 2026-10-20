using System.Runtime.CompilerServices;

namespace DeadGround.Engine;

/// <summary>
/// Domain streams derived from the single match seed (TDD section 6.5).
/// Drawing from one stream never shifts the state of another domain stream.
/// </summary>
public enum RandomDomain : byte
{
    Combat = 0,
    Artillery = 1,
    Ai = 2,
    MapEvents = 3
}

/// <summary>
/// Provides domain-specific deterministic random streams derived from a match seed.
/// </summary>
public interface IRandomStreamProvider
{
    DeterministicRandom GetStream(RandomDomain domain);
}

/// <summary>
/// Implemented by simulation systems or rulesets that consume deterministic random streams.
/// </summary>
public interface IRandomStreamsAware
{
    void InitializeRandomStreams(IRandomStreamProvider provider);
}

/// <summary>
/// Fast, deterministic, allocation-free 64-bit PRNG based on SplitMix64.
/// State is explicit and can be read or restored for replays and reconnects.
/// </summary>
public sealed class DeterministicRandom
{
    private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    public ulong State { get; set; }

    public DeterministicRandom(ulong seed)
    {
        State = seed;
    }

    /// <summary>
    /// Creates an independent domain stream derived from a single match seed.
    /// Uses domain-specific prime salts so streams are uncorrelated.
    /// </summary>
    public static DeterministicRandom CreateDomainStream(ulong matchSeed, RandomDomain domain)
    {
        ulong domainSalt = domain switch
        {
            RandomDomain.Combat => 0xD6E8FEB86659FD93UL,
            RandomDomain.Artillery => 0xA2AA033B32904E85UL,
            RandomDomain.Ai => 0x9E3779B97F4A7C15UL,
            RandomDomain.MapEvents => 0xC6A4A7935BD1E995UL,
            _ => (ulong)domain * 0x5851F42D4C957F2DUL
        };

        ulong s = matchSeed ^ domainSalt;
        s = (s ^ (s >> 30)) * 0xBF58476D1CE4E5B9UL;
        s = (s ^ (s >> 27)) * 0x94D049BB133111EBUL;
        return new DeterministicRandom(s ^ (s >> 31));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong NextULong()
    {
        ulong z = (State += GoldenGamma);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextUInt() => (uint)(NextULong() >> 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Next(int maxValue)
    {
        if (maxValue <= 0) return 0;
        return (int)(NextUInt() % (uint)maxValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Next(int minValue, int maxValue)
    {
        if (minValue >= maxValue) return minValue;
        uint range = (uint)(maxValue - minValue);
        return minValue + (int)(NextUInt() % range);
    }

    /// <summary>
    /// Returns a single-precision float in [0.0f, 1.0f).
    /// Uses 24 bits of precision.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat() => (NextUInt() >> 8) * (1.0f / 16777216.0f);

    /// <summary>
    /// Returns a double-precision float in [0.0, 1.0).
    /// Uses 53 bits of precision.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);
}
