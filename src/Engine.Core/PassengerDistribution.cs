using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Allocation-free deterministic distribution algorithm for disembarking and evacuating
/// passengers from transport containers onto valid, traversable, and unobstructed positions.
/// </summary>
public static class PassengerDistribution
{
    public const float SoldierRadius = 0.4f;
    public const float MinInfantryDistance = 0.8f;
    public const float ClearanceMargin = 0.4f;
    public const int MaxCandidates = 288;

    private static readonly Vector2[] s_candidateOffsets = InitializeOffsets();

    private static Vector2[] InitializeOffsets()
    {
        var offsets = new List<Vector2>(MaxCandidates);
        float[] radii = [2.25f, 3.0f, 3.8f, 4.6f, 5.4f, 6.2f, 7.0f, 8.0f, 9.0f, 10.2f, 11.5f, 13.0f, 14.5f];
        int[] angleCounts = [8, 12, 16, 18, 20, 22, 24, 24, 24, 24, 24, 24, 24];
        for (int r = 0; r < radii.Length; r++)
        {
            float radius = radii[r];
            int count = angleCounts[r];
            float step = MathF.PI * 2.0f / count;
            for (int a = 0; a < count; a++)
            {
                float angle = a == 0 ? 0f : (a % 2 == 1 ? ((a + 1) / 2) * step : -((a / 2) * step));
                offsets.Add(new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius));
            }
        }
        return offsets.ToArray();
    }

    public static int Distribute(
        WorldState world,
        DestinationEvaluator evaluator,
        EntityId transportId,
        Vector2 targetLocation,
        uint targetSurface,
        ReadOnlySpan<EntityId> passengers,
        Span<Vector2> outPositions,
        Span<bool> outPlaced,
        IUnitMovementCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(evaluator);

        int placedCount = 0;
        bool hasTransport = world.TryGetUnit(transportId, out UnitState transport);
        Vector2 transportPos = hasTransport ? transport.Position : targetLocation;

        float transportRadius = (hasTransport && catalog is not null && catalog.TryGetProfile(transport.Definition, out UnitMovementProfile tProf) && tProf.EffectiveCollisionRadius > 0f)
            ? tProf.EffectiveCollisionRadius
            : 1.4f;

        Vector2 center = hasTransport ? transportPos : targetLocation;

        float baseAngle = 0f;
        bool rotate = false;
        if (hasTransport)
        {
            Vector2 delta = targetLocation - transportPos;
            if (delta.LengthSquared() > 0.04f)
            {
                baseAngle = MathF.Atan2(delta.Y, delta.X);
                rotate = true;
            }
        }
        float cos = rotate ? MathF.Cos(baseAngle) : 1f;
        float sin = rotate ? MathF.Sin(baseAngle) : 0f;

        Span<Vector2> placed = stackalloc Vector2[passengers.Length];
        Span<float> placedRadii = stackalloc float[passengers.Length];
        int currentPlaced = 0;

        for (int p = 0; p < passengers.Length; p++)
        {
            outPlaced[p] = false;
            EntityId passengerId = passengers[p];
            float pRad = SoldierRadius;
            TraversabilityCapabilities pCaps = TraversabilityCapabilities.Foot;
            if (catalog is not null &&
                world.TryGetUnit(passengerId, out UnitState passUnit) &&
                catalog.TryGetProfile(passUnit.Definition, out UnitMovementProfile passProf))
            {
                if (passProf.EffectiveCollisionRadius > 0f)
                    pRad = passProf.EffectiveCollisionRadius;
                if (passProf.Capabilities != TraversabilityCapabilities.None)
                    pCaps = passProf.Capabilities;
            }
            float minTransportDist = transportRadius + pRad + ClearanceMargin;

            if (!hasTransport && IsValidPosition(center, transportPos, hasTransport, minTransportDist, evaluator, targetSurface, placed.Slice(0, currentPlaced), placedRadii.Slice(0, currentPlaced), world, passengerId, pRad, pCaps, catalog))
            {
                outPositions[p] = center;
                outPlaced[p] = true;
                placed[currentPlaced] = center;
                placedRadii[currentPlaced] = pRad;
                currentPlaced++;
                placedCount++;
                continue;
            }

            for (int c = 0; c < s_candidateOffsets.Length; c++)
            {
                Vector2 offset = s_candidateOffsets[c];
                Vector2 candidate = rotate
                    ? center + new Vector2(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos)
                    : center + offset;

                if (IsValidPosition(candidate, transportPos, hasTransport, minTransportDist, evaluator, targetSurface, placed.Slice(0, currentPlaced), placedRadii.Slice(0, currentPlaced), world, passengerId, pRad, pCaps, catalog))
                {
                    outPositions[p] = candidate;
                    outPlaced[p] = true;
                    placed[currentPlaced] = candidate;
                    placedRadii[currentPlaced] = pRad;
                    currentPlaced++;
                    placedCount++;
                    break;
                }
            }
        }

        return placedCount;
    }

    /// <summary>
    /// Finds an allocation-free valid ground position beneath a hovering helicopter for fast-rope infantry insertion.
    /// Allows dense forests and steep hills (Foot traversability), while rejecting water, buildings, and occupied spots.
    /// </summary>
    public static bool TryFindFastRopeSpot(
        WorldState world,
        DestinationEvaluator evaluator,
        Vector2 hoverPosition,
        uint targetSurface,
        EntityId passengerId,
        out Vector2 spot)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(evaluator);

        if (IsFastRopePointValid(hoverPosition, evaluator, targetSurface, world, passengerId))
        {
            spot = hoverPosition;
            return true;
        }

        for (int c = 0; c < s_candidateOffsets.Length; c++)
        {
            Vector2 offset = s_candidateOffsets[c];
            if (offset.LengthSquared() > 36f)
                break;
            Vector2 candidate = hoverPosition + offset;
            if (IsFastRopePointValid(candidate, evaluator, targetSurface, world, passengerId))
            {
                spot = candidate;
                return true;
            }
        }

        spot = default;
        return false;
    }

    private static bool IsFastRopePointValid(
        Vector2 pos,
        DestinationEvaluator evaluator,
        uint targetSurface,
        WorldState world,
        EntityId passengerId)
    {
        if (!world.Map.Contains(pos)) return false;
        if (!evaluator.IsPointClear(pos, SoldierRadius, TraversabilityCapabilities.Foot, targetSurface))
            return false;

        for (int i = 0; i < world.Count; i++)
        {
            UnitState other = world.GetUnitAt(i);
            if (other.Id == passengerId) continue;
            if (world.IsContained(other.Id) || world.IsAirborne(other.Id) || world.IsSlung(other.Id)) continue;
            if (Vector2.DistanceSquared(pos, other.Position) < MinInfantryDistance * MinInfantryDistance)
                return false;
        }
        return true;
    }

    private static bool IsValidPosition(
        Vector2 pos,
        Vector2 transportPos,
        bool hasTransport,
        float minTransportDist,
        DestinationEvaluator evaluator,
        uint targetSurface,
        ReadOnlySpan<Vector2> previouslyPlaced,
        ReadOnlySpan<float> previouslyPlacedRadii,
        WorldState world,
        EntityId passengerId,
        float passengerRadius,
        TraversabilityCapabilities capabilities,
        IUnitMovementCatalog? catalog)
    {
        if (!world.Map.Contains(pos)) return false;

        if (!evaluator.IsPointClear(pos, passengerRadius, capabilities, targetSurface))
            return false;

        if (hasTransport && Vector2.Distance(pos, transportPos) < minTransportDist)
            return false;

        for (int i = 0; i < previouslyPlaced.Length; i++)
        {
            float minPair = MathF.Max(MinInfantryDistance, passengerRadius + previouslyPlacedRadii[i] + 0.35f);
            if (Vector2.Distance(pos, previouslyPlaced[i]) < minPair)
                return false;
        }

        EntityId transportId = transportPosId(world, passengerId);
        for (int i = 0; i < world.Count; i++)
        {
            UnitState other = world.GetUnitAt(i);
            if (other.Id == passengerId) continue;
            if (hasTransport && other.Id == transportId) continue;
            if (world.IsContained(other.Id) || world.IsAirborne(other.Id) || world.IsSlung(other.Id)) continue;

            float otherRadius = (catalog is not null && catalog.TryGetProfile(other.Definition, out UnitMovementProfile oProf) && oProf.EffectiveCollisionRadius > 0f)
                ? oProf.EffectiveCollisionRadius
                : 0.8f;
            float minOtherDist = passengerRadius <= 0.6f
                ? 1.2f
                : MathF.Max(1.2f, passengerRadius + otherRadius + 0.35f);
            if (Vector2.Distance(pos, other.Position) < minOtherDist)
                return false;
        }

        return true;
    }

    private static EntityId transportPosId(WorldState world, EntityId passengerId) =>
        world.GetContainerId(passengerId);
}
