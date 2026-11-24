using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Reusable context for line-of-sight queries.
/// Holds allocation-free query deduplication buffers.
/// Safe to reuse across queries on a single thread.
/// </summary>
public sealed class LineOfSightContext
{
    internal int QueryId;
    internal int[] Mailbox;
    internal VegetationInterval[]? VegetationIntervalsBuffer;
    internal Vector2[]? ZoneIntervalsBuffer;
    internal int[]? ClearingsBuffer;
    internal SmokeVolume[]? SmokeVolumes;
    internal int SmokeVolumeCount;
    internal float[]? StructureMaxYOverrides;
    internal int StructureMaxYOverrideCount;

    public ReadOnlySpan<SmokeVolume> ActiveSmoke =>
        SmokeVolumes is not null && SmokeVolumeCount > 0
            ? SmokeVolumes.AsSpan(0, SmokeVolumeCount)
            : [];

    public void SetSmokeVolumes(SmokeVolume[]? volumes, int count)
    {
        SmokeVolumes = volumes;
        SmokeVolumeCount = volumes is null || count <= 0 ? 0 : Math.Min(count, volumes.Length);
    }

    public void SetStructureMaxYOverrides(float[]? overrides, int count)
    {
        StructureMaxYOverrides = overrides;
        StructureMaxYOverrideCount = overrides is null || count <= 0 ? 0 : Math.Min(count, overrides.Length);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal float GetEffectiveStructureMaxY(int structureIndex, float defaultMaxY)
    {
        if (StructureMaxYOverrides is not null && (uint)structureIndex < (uint)StructureMaxYOverrideCount)
        {
            float ov = StructureMaxYOverrides[structureIndex];
            if (!float.IsNaN(ov))
            {
                return ov;
            }
        }
        return defaultMaxY;
    }

    public LineOfSightContext(int initialCapacity = 64)
    {
        Mailbox = new int[Math.Max(initialCapacity, 16)];
        QueryId = 1;
    }

    public void EnsureCapacity(int count)
    {
        if (Mailbox.Length < count)
        {
            Array.Resize(ref Mailbox, Math.Max(count, Mailbox.Length * 2));
        }
    }

    public void EnsureVegetationCapacity(int maxIntervals, int maxZoneIntervals, int maxClearings = 0)
    {
        if (VegetationIntervalsBuffer is null || VegetationIntervalsBuffer.Length < maxIntervals)
        {
            VegetationIntervalsBuffer = new VegetationInterval[Math.Max(maxIntervals, 64)];
        }

        if (ZoneIntervalsBuffer is null || ZoneIntervalsBuffer.Length < maxZoneIntervals)
        {
            ZoneIntervalsBuffer = new Vector2[Math.Max(maxZoneIntervals, 16)];
        }

        if (maxClearings > 0 && (ClearingsBuffer is null || ClearingsBuffer.Length < maxClearings))
        {
            ClearingsBuffer = new int[Math.Max(maxClearings, 64)];
        }
    }

    internal int NextQueryId()
    {
        QueryId++;
        if (QueryId == 0)
        {
            Array.Clear(Mailbox);
            QueryId = 1;
        }
        return QueryId;
    }
}
