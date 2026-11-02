using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Input data for a single unit participating in group destination distribution.
/// </summary>
public readonly record struct GroupUnitInput(
    EntityId Id,
    Vector2 Position,
    float Heading,
    float Radius,
    float CollisionRadius,
    TraversabilityCapabilities Capabilities,
    bool CanReverse,
    float ReverseSpeed = 2.5f)
{
    public float EffectiveCollisionRadius => CollisionRadius > 0f ? CollisionRadius : Radius;
}

/// <summary>
/// Visual and logical preview for a single unit in a group move command.
/// </summary>
public readonly record struct GroupPreviewSlot(
    EntityId UnitId,
    Vector2 Target,
    Vector2 OriginalTarget,
    DestinationValidity Validity,
    DestinationBlockReason Reason,
    float? FinalFacing);

/// <summary>
/// Aggregated result of pre-click group destination evaluation.
/// </summary>
public readonly record struct GroupPreviewResult(
    int PlacedCount,
    int AdjustedCount,
    int BlockedCount,
    DestinationBlockReason PrimaryBlockedReason,
    ReadOnlyMemory<GroupPreviewSlot> Slots,
    Vector2 SnappedTarget = default,
    bool IsTargetAdjusted = false);

/// <summary>
/// Authoritative and allocation-free distribution engine for group move orders.
/// Calculates compact, non-overlapping destination slots around an arrival target,
/// preserving spatial ordering and respecting unit dimensions, capabilities, and surfaces.
/// </summary>
public static class GroupDestinationDistribution
{
    public const float DestinationGap = 1.0f;
    public const int MaxCandidates = 1024;
    public const float MaxSearchRadius = 300.0f;
    public const float CentroidMinDistance = 0.5f;
    public const float SnapMaxDistance = 3.0f;

    // Pre-computed concentric grid candidate offsets sorted by distance from formation center.
    // Lateral coordinates (c) are weighted lower than longitudinal depth (r) so the formation
    // forms a line abreast before expanding into depth.
    private static readonly (sbyte C, sbyte R)[] s_orderedOffsets = InitializeOffsets();

    private static (sbyte C, sbyte R)[] InitializeOffsets()
    {
        const int extent = 32;
        var list = new List<(sbyte C, sbyte R, float Score)>();
        for (int r = -extent; r <= extent; r++)
        {
            for (int c = -extent; c <= extent; c++)
            {
                // Score: lateral expansion preferred (c^2 + 1.5 * r^2)
                float score = c * c + 1.5f * r * r;
                list.Add(((sbyte)c, (sbyte)r, score));
            }
        }

        list.Sort((a, b) =>
        {
            int cmp = a.Score.CompareTo(b.Score);
            if (cmp != 0) return cmp;
            int rAbs = Math.Abs((int)a.R).CompareTo(Math.Abs((int)b.R));
            if (rAbs != 0) return rAbs;
            int cAbs = Math.Abs((int)a.C).CompareTo(Math.Abs((int)b.C));
            if (cAbs != 0) return cAbs;
            int cCmp = a.C.CompareTo(b.C);
            if (cCmp != 0) return cCmp;
            return a.R.CompareTo(b.R);
        });

        var result = new (sbyte C, sbyte R)[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            result[i] = (list[i].C, list[i].R);
        }
        return result;
    }

    private struct CandidateSlot
    {
        public Vector2 Position;
        public Vector2 OriginalPosition;
        public bool IsAdjusted;
        public float Lateral;
        public float Longitudinal;
        public bool Used;
    }

    private struct SortedUnit
    {
        public int OriginalIndex;
        public EntityId Id;
        public Vector2 Position;
        public float Heading;
        public float Radius;
        public float CollisionRadius;
        public TraversabilityCapabilities Capabilities;
        public bool CanReverse;
        public float ReverseSpeed;
        public int Lane;
        public float Lateral;
        public float Longitudinal;
        public int AssignedSlot;
    }

    // Thread-static reusable buffers for allocation-free operation in hot loops
    [ThreadStatic]
    private static CandidateSlot[]? t_candidateSlots;
    [ThreadStatic]
    private static SortedUnit[]? t_sortedUnits;
    [ThreadStatic]
    private static GroupPreviewSlot[]? t_previewSlots;
    [ThreadStatic]
    private static GroupMoveAssignment[]? t_placedAssignments;
    [ThreadStatic]
    private static GroupMoveUnplaced[]? t_unplacedRecords;
    [ThreadStatic]
    private static int[]? t_adjustedPlacedIndices;

    private static void EnsureBuffers(int unitCapacity)
    {
        int cap = Math.Max(MaxCandidates, unitCapacity);
        if (t_candidateSlots == null || t_candidateSlots.Length < cap)
        {
            t_candidateSlots = new CandidateSlot[cap];
        }
        if (t_sortedUnits == null || t_sortedUnits.Length < cap)
        {
            t_sortedUnits = new SortedUnit[cap];
        }
        if (t_previewSlots == null || t_previewSlots.Length < cap)
        {
            t_previewSlots = new GroupPreviewSlot[cap];
        }
        if (t_placedAssignments == null || t_placedAssignments.Length < cap)
        {
            t_placedAssignments = new GroupMoveAssignment[cap];
        }
        if (t_unplacedRecords == null || t_unplacedRecords.Length < cap)
        {
            t_unplacedRecords = new GroupMoveUnplaced[cap];
        }
        if (t_adjustedPlacedIndices == null || t_adjustedPlacedIndices.Length < cap)
        {
            t_adjustedPlacedIndices = new int[cap];
        }
    }

    /// <summary>
    /// Computes authoritative group destination assignments and unplaced diagnostics.
    /// </summary>
    public static GroupMoveResult Distribute(
        uint sequence,
        int serverTick,
        ReadOnlySpan<GroupUnitInput> units,
        Vector2 target,
        uint targetSurface,
        float? finalFacing,
        MoveMode mode,
        DestinationEvaluator evaluator)
    {
        int totalUnits = units.Length;
        if (totalUnits == 0)
        {
            return new GroupMoveResult(
                sequence,
                serverTick,
                target,
                targetSurface,
                mode,
                ReadOnlyMemory<GroupMoveAssignment>.Empty,
                ReadOnlyMemory<GroupMoveUnplaced>.Empty);
        }

        EnsureBuffers(totalUnits);
        CandidateSlot[] candidates = t_candidateSlots!;
        SortedUnit[] sortedUnits = t_sortedUnits!;

        int candidateCount = 0;
        int placedCount = 0;
        int unplacedCount = 0;

        ExecuteDistributionCore(
            units,
            target,
            targetSurface,
            finalFacing,
            mode,
            evaluator,
            candidates,
            ref candidateCount,
            sortedUnits,
            out DestinationBlockReason defaultBlockedReason,
            out _,
            out _);

        GroupMoveAssignment[] placedList = t_placedAssignments!;
        GroupMoveUnplaced[] unplacedList = t_unplacedRecords!;

        for (int i = 0; i < totalUnits; i++)
        {
            ref readonly SortedUnit u = ref sortedUnits[i];
            if (u.AssignedSlot >= 0)
            {
                ref readonly CandidateSlot slot = ref candidates[u.AssignedSlot];
                placedList[placedCount++] = new GroupMoveAssignment(
                    u.Id,
                    slot.Position,
                    targetSurface,
                    finalFacing);
            }
            else
            {
                DestinationBlockReason reason = defaultBlockedReason;
                if (mode == MoveMode.Reverse && (!u.CanReverse || u.ReverseSpeed <= 0f))
                {
                    reason = DestinationBlockReason.NoReverseGear;
                }
                else if (reason == DestinationBlockReason.None)
                {
                    reason = evaluator.DiagnoseBlockReason(target, u.Radius, u.Capabilities, targetSurface);
                    if (reason == DestinationBlockReason.None)
                    {
                        reason = DestinationBlockReason.InsufficientClearance;
                    }
                }
                unplacedList[unplacedCount++] = new GroupMoveUnplaced(u.Id, reason);
            }
        }

        GroupMoveAssignment[] placedArray = placedCount > 0 ? new GroupMoveAssignment[placedCount] : Array.Empty<GroupMoveAssignment>();
        if (placedCount > 0)
        {
            Array.Copy(placedList, placedArray, placedCount);
        }

        GroupMoveUnplaced[] unplacedArray = unplacedCount > 0 ? new GroupMoveUnplaced[unplacedCount] : Array.Empty<GroupMoveUnplaced>();
        if (unplacedCount > 0)
        {
            Array.Copy(unplacedList, unplacedArray, unplacedCount);
        }

        return new GroupMoveResult(
            sequence,
            serverTick,
            target,
            targetSurface,
            mode,
            placedArray,
            unplacedArray);
    }

    /// <summary>
    /// Computes pre-click hover/drag preview slots without committing changes.
    /// </summary>
    public static GroupPreviewResult ComputePreview(
        ReadOnlySpan<GroupUnitInput> units,
        Vector2 target,
        uint targetSurface,
        float? finalFacing,
        MoveMode mode,
        DestinationEvaluator evaluator)
    {
        int totalUnits = units.Length;
        if (totalUnits == 0)
        {
            return new GroupPreviewResult(0, 0, 0, DestinationBlockReason.None, ReadOnlyMemory<GroupPreviewSlot>.Empty);
        }

        EnsureBuffers(totalUnits);
        CandidateSlot[] candidates = t_candidateSlots!;
        SortedUnit[] sortedUnits = t_sortedUnits!;

        int candidateCount = 0;
        ExecuteDistributionCore(
            units,
            target,
            targetSurface,
            finalFacing,
            mode,
            evaluator,
            candidates,
            ref candidateCount,
            sortedUnits,
            out DestinationBlockReason defaultBlockedReason,
            out Vector2 effectiveTarget,
            out bool isTargetAdjusted);

        GroupPreviewSlot[] previewSlots = t_previewSlots!;
        int placedCount = 0;
        int adjustedCount = 0;
        int blockedCount = 0;
        DestinationBlockReason primaryBlockedReason = DestinationBlockReason.None;

        for (int i = 0; i < totalUnits; i++)
        {
            ref readonly SortedUnit u = ref sortedUnits[i];
            if (u.AssignedSlot >= 0)
            {
                ref readonly CandidateSlot slot = ref candidates[u.AssignedSlot];
                if (isTargetAdjusted || slot.IsAdjusted)
                {
                    adjustedCount++;
                    previewSlots[u.OriginalIndex] = new GroupPreviewSlot(
                        u.Id,
                        slot.Position,
                        slot.OriginalPosition,
                        DestinationValidity.Adjusted,
                        DestinationBlockReason.None,
                        finalFacing);
                }
                else
                {
                    placedCount++;
                    previewSlots[u.OriginalIndex] = new GroupPreviewSlot(
                        u.Id,
                        slot.Position,
                        slot.OriginalPosition,
                        DestinationValidity.Valid,
                        DestinationBlockReason.None,
                        finalFacing);
                }
            }
            else
            {
                blockedCount++;
                DestinationBlockReason reason = defaultBlockedReason;
                if (mode == MoveMode.Reverse && (!u.CanReverse || u.ReverseSpeed <= 0f))
                {
                    reason = DestinationBlockReason.NoReverseGear;
                }
                else if (reason == DestinationBlockReason.None)
                {
                    reason = evaluator.DiagnoseBlockReason(target, u.Radius, u.Capabilities, targetSurface);
                    if (reason == DestinationBlockReason.None)
                    {
                        reason = DestinationBlockReason.InsufficientClearance;
                    }
                }

                if (primaryBlockedReason == DestinationBlockReason.None)
                {
                    primaryBlockedReason = reason;
                }

                previewSlots[u.OriginalIndex] = new GroupPreviewSlot(
                    u.Id,
                    target,
                    target,
                    DestinationValidity.Blocked,
                    reason,
                    finalFacing);
            }
        }

        return new GroupPreviewResult(
            placedCount,
            adjustedCount,
            blockedCount,
            primaryBlockedReason,
            new ReadOnlyMemory<GroupPreviewSlot>(previewSlots, 0, totalUnits),
            effectiveTarget,
            isTargetAdjusted);
    }

    private static void ExecuteDistributionCore(
        ReadOnlySpan<GroupUnitInput> units,
        Vector2 target,
        uint targetSurface,
        float? finalFacing,
        MoveMode mode,
        DestinationEvaluator evaluator,
        CandidateSlot[] candidates,
        ref int candidateCount,
        SortedUnit[] sortedUnits,
        out DestinationBlockReason defaultBlockedReason,
        out Vector2 effectiveTarget,
        out bool isTargetAdjusted)
    {
        defaultBlockedReason = DestinationBlockReason.None;
        effectiveTarget = target;
        isTargetAdjusted = false;
        int totalUnits = units.Length;

        // Step 1: Compute group bounds, centroid, and max collision radius
        Vector2 centerSum = Vector2.Zero;
        float maxColRadius = 0f;
        float maxRadius = 0f;
        TraversabilityCapabilities combinedCaps = TraversabilityCapabilities.None;
        float sinSum = 0f;
        float cosSum = 0f;

        for (int i = 0; i < totalUnits; i++)
        {
            ref readonly GroupUnitInput u = ref units[i];
            centerSum += u.Position;
            float effCol = u.EffectiveCollisionRadius;
            if (effCol > maxColRadius) maxColRadius = effCol;
            if (u.Radius > maxRadius) maxRadius = u.Radius;
            combinedCaps |= u.Capabilities;
            sinSum += MathF.Sin(u.Heading);
            cosSum += MathF.Cos(u.Heading);
        }

        Vector2 groupCentroid = centerSum / totalUnits;
        float cellSize = 2f * maxColRadius + DestinationGap;

        // Step 2: Determine formation orientation (Forward and Lateral Right)
        Vector2 forward;
        Vector2 right;

        if (finalFacing.HasValue)
        {
            float yaw = finalFacing.Value;
            forward = new Vector2(-MathF.Sin(yaw), -MathF.Cos(yaw));
            right = new Vector2(-forward.Y, forward.X);
        }
        else
        {
            Vector2 toTarget = target - groupCentroid;
            float dist = toTarget.Length();
            if (dist > CentroidMinDistance)
            {
                forward = toTarget / dist;
                right = new Vector2(-forward.Y, forward.X);
            }
            else
            {
                // Fallback to average hull heading
                if (sinSum * sinSum + cosSum * cosSum > 0.001f)
                {
                    float avgYaw = MathF.Atan2(sinSum, cosSum);
                    forward = new Vector2(-MathF.Sin(avgYaw), -MathF.Cos(avgYaw));
                }
                else
                {
                    forward = new Vector2(0f, -1f);
                }
                right = new Vector2(-forward.Y, forward.X);
            }
        }

        // Step 3: Populate sorted units and lateral projections
        for (int i = 0; i < totalUnits; i++)
        {
            ref readonly GroupUnitInput u = ref units[i];
            float lateral = Vector2.Dot(u.Position - groupCentroid, right);
            float longitudinal = Vector2.Dot(u.Position - groupCentroid, forward);
            sortedUnits[i] = new SortedUnit
            {
                OriginalIndex = i,
                Id = u.Id,
                Position = u.Position,
                Heading = u.Heading,
                Radius = u.Radius,
                CollisionRadius = u.EffectiveCollisionRadius,
                Capabilities = u.Capabilities,
                CanReverse = u.CanReverse,
                ReverseSpeed = u.ReverseSpeed,
                Lane = 0,
                Lateral = lateral,
                Longitudinal = longitudinal,
                AssignedSlot = -1
            };
        }

        // Assign discrete lateral lanes and sort deterministically
        AssignLanesAndSort(sortedUnits, totalUnits, maxColRadius);

        // Step 4: Validate target point itself for deep obstructions
        bool targetStrictlyOutside = target.X < 0f || target.X > evaluator.MapWidth ||
                                     target.Y < 0f || target.Y > evaluator.MapDepth;
        if (targetStrictlyOutside)
        {
            defaultBlockedReason = DestinationBlockReason.OutsideMapBounds;
            return;
        }

        if (targetSurface > 0 && (combinedCaps & TraversabilityCapabilities.Bridge) == 0)
        {
            defaultBlockedReason = DestinationBlockReason.InsufficientClearance;
            return;
        }

        DestinationBlockReason targetReason = evaluator.DiagnoseBlockReason(target, maxRadius, combinedCaps, targetSurface);
        if (targetReason is DestinationBlockReason.Building)
        {
            if (!evaluator.TrySnapDestination(groupCentroid, target, maxRadius, combinedCaps, targetSurface, SnapMaxDistance, out Vector2 snapped))
            {
                defaultBlockedReason = DestinationBlockReason.Building;
                return;
            }
            effectiveTarget = snapped;
            isTargetAdjusted = true;
        }
        else if (targetReason is DestinationBlockReason.Water && (combinedCaps & TraversabilityCapabilities.Water) == 0)
        {
            if (!evaluator.TrySnapDestination(groupCentroid, target, maxRadius, combinedCaps, targetSurface, SnapMaxDistance, out Vector2 snapped))
            {
                defaultBlockedReason = DestinationBlockReason.Water;
                return;
            }
            effectiveTarget = snapped;
            isTargetAdjusted = true;
        }

        // Step 5: Generate candidate slots
        candidateCount = 0;
        bool hasAdjustedSlots = false;
        int maxCandidateLimit = Math.Min(MaxCandidates, Math.Max((int)(totalUnits * 1.5f), 32));

        for (int i = 0; i < s_orderedOffsets.Length; i++)
        {
            if (candidateCount >= maxCandidateLimit) break;

            var (c, r) = s_orderedOffsets[i];
            Vector2 candPos = effectiveTarget + c * cellSize * right - r * cellSize * forward;

            if (Vector2.Distance(candPos, effectiveTarget) > MaxSearchRadius) continue;
            if (candPos.X < 0f || candPos.X > evaluator.MapWidth || candPos.Y < 0f || candPos.Y > evaluator.MapDepth) continue;

            DestinationEvaluationResult eval = evaluator.Evaluate(
                candPos,
                maxRadius,
                combinedCaps,
                targetSurface,
                start: groupCentroid,
                maxSnapDistance: SnapMaxDistance);

            Vector2 finalPos;
            bool isAdjusted;

            if (eval.Validity == DestinationValidity.Valid)
            {
                finalPos = candPos;
                isAdjusted = false;
            }
            else if (eval.Validity == DestinationValidity.Adjusted)
            {
                finalPos = eval.AdjustedTarget;
                isAdjusted = true;
                hasAdjustedSlots = true;

                // Anti-collapse check: snapped slot must maintain clearance to already accepted slots
                bool collapsed = false;
                float checkDist = cellSize - 0.05f;
                float checkDistSq = checkDist * checkDist;
                for (int k = 0; k < candidateCount; k++)
                {
                    Vector2 existingPos = candidates[k].Position;
                    if (MathF.Abs(existingPos.X - finalPos.X) >= checkDist || MathF.Abs(existingPos.Y - finalPos.Y) >= checkDist)
                    {
                        continue;
                    }
                    if (Vector2.DistanceSquared(existingPos, finalPos) < checkDistSq)
                    {
                        collapsed = true;
                        break;
                    }
                }
                if (collapsed) continue;
            }
            else
            {
                // Slot is blocked
                continue;
            }

            candidates[candidateCount] = new CandidateSlot
            {
                Position = finalPos,
                OriginalPosition = candPos,
                IsAdjusted = isAdjusted,
                Lateral = Vector2.Dot(finalPos - target, right),
                Longitudinal = Vector2.Dot(finalPos - target, forward),
                Used = false
            };
            candidateCount++;
        }

        if (candidateCount == 0)
        {
            defaultBlockedReason = targetReason != DestinationBlockReason.None
                ? targetReason
                : DestinationBlockReason.InsufficientClearance;
            return;
        }

        // Step 6: Unit-to-slot assignment
        // Preserves spatial lateral order, minimizes travel distance, and guarantees collision clearance.
        int adjustedPlacedCount = 0;
        int[] adjustedPlacedIndices = t_adjustedPlacedIndices!;

        for (int i = 0; i < totalUnits; i++)
        {
            ref SortedUnit u = ref sortedUnits[i];

            // Filter unsupported reverse move
            if (mode == MoveMode.Reverse && (!u.CanReverse || u.ReverseSpeed <= 0f))
            {
                continue;
            }

            // Unit must have bridge capability if targeting a bridge surface
            if (targetSurface > 0 && (u.Capabilities & TraversabilityCapabilities.Bridge) == 0)
            {
                continue;
            }

            // Unit must be able to traverse the target location terrain type (e.g. water, forest)
            DestinationBlockReason unitTargetBlock = evaluator.DiagnoseBlockReason(target, u.Radius, u.Capabilities, targetSurface);
            if (unitTargetBlock is DestinationBlockReason.Water or DestinationBlockReason.DenseForest)
            {
                continue;
            }

            int bestSlot = -1;
            float bestCost = float.MaxValue;

            for (int j = 0; j < candidateCount; j++)
            {
                if (candidates[j].Used) continue;

                Vector2 slotPos = candidates[j].Position;

                // Verify specific unit clearance and capabilities at this candidate
                if (!evaluator.IsPointClear(slotPos, u.Radius, u.Capabilities, targetSurface))
                {
                    continue;
                }

                // Verify pairwise distance to previously placed units only if any slot was adjusted
                // Regular grid candidate slots are mathematically guaranteed to have >= cellSize spacing.
                if (hasAdjustedSlots)
                {
                    bool clearanceViolated = false;
                    if (candidates[j].IsAdjusted)
                    {
                        // Candidate itself is adjusted: check against ALL previously placed units
                        for (int p = 0; p < i; p++)
                        {
                            ref readonly SortedUnit prevUnit = ref sortedUnits[p];
                            if (prevUnit.AssignedSlot >= 0)
                            {
                                Vector2 prevPos = candidates[prevUnit.AssignedSlot].Position;
                                if (MathF.Abs(slotPos.X - prevPos.X) >= cellSize || MathF.Abs(slotPos.Y - prevPos.Y) >= cellSize)
                                {
                                    continue;
                                }
                                float minDist = u.CollisionRadius + prevUnit.CollisionRadius + DestinationGap;
                                float minCheck = minDist - 0.05f;
                                if (Vector2.DistanceSquared(slotPos, prevPos) < minCheck * minCheck)
                                {
                                    clearanceViolated = true;
                                    break;
                                }
                            }
                        }
                    }
                    else if (adjustedPlacedCount > 0)
                    {
                        // Candidate is regular: only check against placed units that were adjusted
                        for (int a = 0; a < adjustedPlacedCount; a++)
                        {
                            int p = adjustedPlacedIndices[a];
                            ref readonly SortedUnit prevUnit = ref sortedUnits[p];
                            Vector2 prevPos = candidates[prevUnit.AssignedSlot].Position;
                            if (MathF.Abs(slotPos.X - prevPos.X) >= cellSize || MathF.Abs(slotPos.Y - prevPos.Y) >= cellSize)
                            {
                                continue;
                            }
                            float minDist = u.CollisionRadius + prevUnit.CollisionRadius + DestinationGap;
                            float minCheck = minDist - 0.05f;
                            if (Vector2.DistanceSquared(slotPos, prevPos) < minCheck * minCheck)
                            {
                                clearanceViolated = true;
                                break;
                            }
                        }
                    }

                    if (clearanceViolated) continue;
                }

                float targetDist = Vector2.Distance(slotPos, target);
                float travelDist = Vector2.Distance(u.Position, slotPos);
                float lateralDiff = MathF.Abs(u.Lateral - candidates[j].Lateral);
                float longitudinalDiff = MathF.Abs(u.Longitudinal - candidates[j].Longitudinal);
                float cost = 1.5f * lateralDiff + 1.2f * longitudinalDiff + 0.2f * targetDist + 0.05f * travelDist;

                if (cost < bestCost - 1e-4f)
                {
                    bestCost = cost;
                    bestSlot = j;
                }
            }

            if (bestSlot >= 0)
            {
                u.AssignedSlot = bestSlot;
                candidates[bestSlot].Used = true;
                if (candidates[bestSlot].IsAdjusted)
                {
                    adjustedPlacedIndices[adjustedPlacedCount++] = i;
                }
            }
        }
    }

    private static void AssignLanesAndSort(SortedUnit[] array, int count, float maxCollisionRadius)
    {
        if (count <= 1)
        {
            if (count == 1) array[0].Lane = 0;
            return;
        }

        // Pass 1: Canonical sort strictly by Lateral, tie-break by EntityId
        QuickSortByLateral(array, 0, count - 1);

        // Pass 2: Assign discrete lanes based on lateral clustering in canonical order.
        // In a column (following), lateral jitter is small (< 0.5 m).
        // In line abreast, vehicles are separated by at least 2 * collisionRadius (typically >= 3 m).
        float laneThreshold = 0.5f;
        float maxLaneSpan = MathF.Max(1.0f, maxCollisionRadius);

        int currentLane = 0;
        float laneStartLateral = array[0].Lateral;
        array[0].Lane = 0;

        for (int i = 1; i < count; i++)
        {
            float delta = array[i].Lateral - array[i - 1].Lateral;
            float span = array[i].Lateral - laneStartLateral;
            if (delta > laneThreshold || span > maxLaneSpan)
            {
                currentLane++;
                laneStartLateral = array[i].Lateral;
            }
            array[i].Lane = currentLane;
        }

        // Pass 3: Final sort by Lane (left-to-right), then Longitudinal descending (front-to-back), then EntityId
        QuickSortByLaneAndLongitudinal(array, 0, count - 1);
    }

    private static void QuickSortByLateral(SortedUnit[] array, int left, int right)
    {
        if (right - left < 16)
        {
            for (int i = left + 1; i <= right; i++)
            {
                SortedUnit key = array[i];
                int j = i - 1;
                while (j >= left && CompareByLateral(in array[j], in key) > 0)
                {
                    array[j + 1] = array[j];
                    j--;
                }
                array[j + 1] = key;
            }
            return;
        }

        int pivotIndex = left + (right - left) / 2;
        SortedUnit pivot = array[pivotIndex];
        int iIdx = left;
        int jIdx = right;

        while (iIdx <= jIdx)
        {
            while (CompareByLateral(in array[iIdx], in pivot) < 0) iIdx++;
            while (CompareByLateral(in array[jIdx], in pivot) > 0) jIdx--;
            if (iIdx <= jIdx)
            {
                (array[iIdx], array[jIdx]) = (array[jIdx], array[iIdx]);
                iIdx++;
                jIdx--;
            }
        }

        if (left < jIdx) QuickSortByLateral(array, left, jIdx);
        if (iIdx < right) QuickSortByLateral(array, iIdx, right);
    }

    private static int CompareByLateral(in SortedUnit x, in SortedUnit y)
    {
        int latCmp = x.Lateral.CompareTo(y.Lateral);
        if (latCmp != 0) return latCmp;
        return x.Id.Value.CompareTo(y.Id.Value);
    }

    private static void QuickSortByLaneAndLongitudinal(SortedUnit[] array, int left, int right)
    {
        if (right - left < 16)
        {
            for (int i = left + 1; i <= right; i++)
            {
                SortedUnit key = array[i];
                int j = i - 1;
                while (j >= left && CompareSortedUnits(in array[j], in key) > 0)
                {
                    array[j + 1] = array[j];
                    j--;
                }
                array[j + 1] = key;
            }
            return;
        }

        int pivotIndex = left + (right - left) / 2;
        SortedUnit pivot = array[pivotIndex];
        int iIdx = left;
        int jIdx = right;

        while (iIdx <= jIdx)
        {
            while (CompareSortedUnits(in array[iIdx], in pivot) < 0) iIdx++;
            while (CompareSortedUnits(in array[jIdx], in pivot) > 0) jIdx--;
            if (iIdx <= jIdx)
            {
                (array[iIdx], array[jIdx]) = (array[jIdx], array[iIdx]);
                iIdx++;
                jIdx--;
            }
        }

        if (left < jIdx) QuickSortByLaneAndLongitudinal(array, left, jIdx);
        if (iIdx < right) QuickSortByLaneAndLongitudinal(array, iIdx, right);
    }

    private static int CompareSortedUnits(in SortedUnit x, in SortedUnit y)
    {
        int laneCmp = x.Lane.CompareTo(y.Lane);
        if (laneCmp != 0) return laneCmp;
        int longCmp = y.Longitudinal.CompareTo(x.Longitudinal);
        if (longCmp != 0) return longCmp;
        return x.Id.Value.CompareTo(y.Id.Value);
    }
}
