using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative vehicle separation and distance keeping system.
/// Handles two-phase motion integration: lookahead clearance braking and continuous swept collision resolution.
/// </summary>
public sealed class VehicleSeparationSystem
{
    public const float DefaultSeparationBuffer = 1.0f;
    public const float DefaultLateralMargin = 0.5f;
    public const float MinLookaheadDistance = 10.0f;
    public const float MaxLookaheadDistance = 45.0f;

    private readonly NavigationGrid _navGrid;
    private readonly VehicleSpatialGrid _spatialGrid;
    private readonly float _separationBuffer;
    private readonly float _lateralMargin;

    // Preallocated candidate buffers for zero-allocation queries
    private int[] _candidateBuffer;

    public VehicleSpatialGrid SpatialGrid => _spatialGrid;
    public float SeparationBuffer => _separationBuffer;

    public int CandidatesChecked { get; private set; }
    public int ExactChecks { get; private set; }
    public int WaitingUnits { get; set; }
    public int EmergencyStops { get; set; }
    public int BufferOverflows => _spatialGrid.BufferOverflows;

    public VehicleSeparationSystem(
        NavigationGrid navGrid,
        float mapWidth,
        float mapDepth,
        int maxUnits = 1024,
        float cellSize = 16.0f,
        float separationBuffer = DefaultSeparationBuffer,
        float lateralMargin = DefaultLateralMargin)
    {
        _navGrid = navGrid ?? throw new ArgumentNullException(nameof(navGrid));
        _spatialGrid = new VehicleSpatialGrid(mapWidth, mapDepth, maxUnits, cellSize);
        _separationBuffer = separationBuffer;
        _lateralMargin = lateralMargin;
        _candidateBuffer = new int[Math.Max(maxUnits, 2048)];
    }

    public void BeginTick(WorldState world, IUnitMovementCatalog catalog)
    {
        _spatialGrid.Build(world, catalog);
        if (_candidateBuffer.Length < world.Capacity)
        {
            Array.Resize(ref _candidateBuffer, Math.Max(world.Capacity, 2048));
        }
        CandidatesChecked = _spatialGrid.CandidatesChecked;
        ExactChecks = 0;
        WaitingUnits = 0;
        EmergencyStops = 0;
    }

    /// <summary>
    /// Computes the free distance ahead along the movement corridor before encountering another vehicle.
    /// Returns float.MaxValue if the corridor is completely clear.
    /// </summary>
    public float ComputeLookaheadClearance(
        WorldState world,
        IUnitMovementCatalog catalog,
        int selfUnitIndex,
        Vector2 selfPos,
        uint selfSurface,
        float selfCollisionRadius,
        float selfHeight,
        Vector2 corridorDir,
        ReadOnlySpan<Vector2> waypoints,
        float currentSpeed,
        float deceleration,
        float dt,
        out int blockingUnitIndex,
        out float blockingForwardSpeed)
    {
        blockingUnitIndex = -1;
        blockingForwardSpeed = 0f;

        float decel = MathF.Max(0.5f, deceleration);
        float stoppingDistance = (currentSpeed * currentSpeed) / (2f * decel);
        float searchRadius = Math.Clamp(2f * stoppingDistance + selfCollisionRadius + 20.0f, MinLookaheadDistance, MaxLookaheadDistance);

        int candidateCount = _spatialGrid.QueryNeighbors(selfPos, searchRadius, ref _candidateBuffer);
        CandidatesChecked += candidateCount;

        if (candidateCount == 0)
        {
            return float.MaxValue;
        }

        float minFreeDistance = float.MaxValue;
        float selfSurfaceY = _navGrid.Evaluator.GetSurfaceHeight(selfPos, selfSurface);
        float selfTopY = selfSurfaceY + selfHeight;

        for (int i = 0; i < candidateCount; i++)
        {
            int otherIndex = _candidateBuffer[i];
            if (otherIndex == selfUnitIndex) continue;

            UnitState other = world.GetUnitAt(otherIndex);
            float otherRadius = 0f;
            float otherHeight = 2.5f;
            float otherMaxSpeed = 0f;
            if (catalog.TryGetProfile(other.Definition, out UnitMovementProfile otherProfile))
            {
                otherRadius = otherProfile.EffectiveCollisionRadius;
                otherHeight = otherProfile.Height;
                otherMaxSpeed = otherProfile.Speed;
            }
            if (otherRadius <= 0f) continue;

            // Vertical overlap test
            float otherSurfaceY = _navGrid.Evaluator.GetSurfaceHeight(other.Position, other.SupportSurface);
            float otherTopY = otherSurfaceY + otherHeight;
            if (MathF.Max(selfSurfaceY, otherSurfaceY) >= MathF.Min(selfTopY, otherTopY))
            {
                // Different vertical layers without overlap (e.g. over vs under bridge)
                continue;
            }

            ExactChecks++;

            // Evaluate corridor projection
            float longitudinalDist;
            float lateralDist;

            if (waypoints.Length > 0)
            {
                // Project onto route waypoints
                ProjectOntoRoute(selfPos, waypoints, other.Position, out longitudinalDist, out lateralDist);
            }
            else
            {
                // Project onto straight-line movement direction
                Vector2 toOther = other.Position - selfPos;
                longitudinalDist = Vector2.Dot(toOther, corridorDir);
                Vector2 perp = toOther - corridorDir * longitudinalDist;
                lateralDist = perp.Length();
            }

            float allowedLateral = selfCollisionRadius + otherRadius + _lateralMargin;
            if (lateralDist <= allowedLateral && longitudinalDist > 0.05f)
            {
                float otherBrakingDist = 0f;
                float otherSpeed = other.Moving ? otherMaxSpeed : 0f;
                if (otherSpeed > 1e-4f)
                {
                    float otherHeading = world.TryGetCombatState(other.Id, out WorldState.CombatState ocs) ? ocs.HullHeading : 0f;
                    Vector2 otherForward = new(-MathF.Sin(otherHeading), -MathF.Cos(otherHeading));
                    float closingSpeed = -Vector2.Dot(otherForward * otherSpeed, corridorDir);
                    if (closingSpeed > 0.05f)
                    {
                        float otherDecel = otherProfile.Deceleration > 0f ? otherProfile.Deceleration : decel;
                        otherBrakingDist = (closingSpeed * closingSpeed) / (2f * MathF.Max(0.5f, otherDecel));
                    }
                }

                float clearance = longitudinalDist - (selfCollisionRadius + otherRadius + _separationBuffer) - otherBrakingDist;
                clearance = MathF.Max(0f, clearance);
                if (clearance < minFreeDistance)
                {
                    minFreeDistance = clearance;
                    blockingUnitIndex = otherIndex;
                    blockingForwardSpeed = otherSpeed;
                }
            }
        }

        return minFreeDistance;
    }

    private static void ProjectOntoRoute(
        Vector2 startPos,
        ReadOnlySpan<Vector2> waypoints,
        Vector2 targetPos,
        out float longitudinalDist,
        out float lateralDist)
    {
        longitudinalDist = -1f;
        lateralDist = float.MaxValue;

        float accumulatedDist = 0f;
        Vector2 prevPt = startPos;

        for (int i = 0; i < waypoints.Length; i++)
        {
            Vector2 nextPt = waypoints[i];
            Vector2 seg = nextPt - prevPt;
            float segLen = seg.Length();

            if (segLen > 1e-4f)
            {
                Vector2 toTarget = targetPos - prevPt;
                float t = Math.Clamp(Vector2.Dot(toTarget, seg) / (segLen * segLen), 0f, 1f);
                Vector2 proj = prevPt + seg * t;
                float dPerp = Vector2.Distance(targetPos, proj);

                if (dPerp < lateralDist)
                {
                    lateralDist = dPerp;
                    longitudinalDist = accumulatedDist + t * segLen;
                }

                accumulatedDist += segLen;
            }

            prevPt = nextPt;
        }
    }

    /// <summary>
    /// Computes the exact discrete braking speed ceiling to stop within free distance sFree.
    /// </summary>
    public static float ComputeBrakingCeiling(float sFree, float deceleration, float dt)
    {
        if (sFree <= 0.05f)
        {
            return 0f;
        }

        float decel = MathF.Max(0.1f, deceleration);
        float decelStep = decel * dt;
        float aDt2 = decelStep * dt;

        float vStop;
        if (aDt2 <= 1e-6f)
        {
            vStop = sFree / dt;
        }
        else
        {
            float disc = 1f + (8f * sFree / aDt2);
            int n = (int)MathF.Ceiling(0.5f * (-1f + MathF.Sqrt(disc)));
            if (n <= 1)
            {
                vStop = sFree / dt;
            }
            else
            {
                vStop = (sFree / (n * dt)) + 0.5f * (n - 1) * decelStep;
            }
        }

        return vStop;
    }

    /// <summary>
    /// Swept quadratic collision test between two units over the interval tau in [0, 1].
    /// Resolves conflicts deterministically and clamps self displacement if self must yield.
    /// </summary>
    public bool CheckAndResolveSweptStep(
        WorldState world,
        IUnitMovementCatalog catalog,
        int selfUnitIndex,
        Vector2 selfStartPos,
        ref Vector2 selfStep,
        ref float selfSpeed,
        uint selfSurface,
        float selfCollisionRadius,
        float selfHeight,
        ReadOnlySpan<Vector2> neighborCandidateSteps,
        float dt)
    {
        if (selfStep.LengthSquared() < 1e-8f)
        {
            return false;
        }

        float selfStepLen = selfStep.Length();
        float queryRadius = selfCollisionRadius + selfStepLen + 10.0f;

        int candidateCount = _spatialGrid.QueryNeighbors(selfStartPos, queryRadius, ref _candidateBuffer);
        CandidatesChecked += candidateCount;

        if (candidateCount == 0)
        {
            return false;
        }

        float selfSurfaceY = _navGrid.Evaluator.GetSurfaceHeight(selfStartPos, selfSurface);
        float selfTopY = selfSurfaceY + selfHeight;

        bool stepModified = false;
        float earliestTau = 1.0f;

        for (int i = 0; i < candidateCount; i++)
        {
            int otherIndex = _candidateBuffer[i];
            if (otherIndex == selfUnitIndex) continue;

            UnitState other = world.GetUnitAt(otherIndex);
            float otherRadius = 0f;
            float otherHeight = 2.5f;
            float otherMaxSpeed = 0f;
            if (catalog.TryGetProfile(other.Definition, out UnitMovementProfile otherProfile))
            {
                otherRadius = otherProfile.EffectiveCollisionRadius;
                otherHeight = otherProfile.Height;
                otherMaxSpeed = otherProfile.Speed;
            }
            if (otherRadius <= 0f) continue;

            // Vertical overlap test
            float otherSurfaceY = _navGrid.Evaluator.GetSurfaceHeight(other.Position, other.SupportSurface);
            float otherTopY = otherSurfaceY + otherHeight;
            if (MathF.Max(selfSurfaceY, otherSurfaceY) >= MathF.Min(selfTopY, otherTopY))
            {
                continue;
            }

            ExactChecks++;

            Vector2 otherPos = _spatialGrid.GetPosition(otherIndex);
            Vector2 otherStep = otherIndex < neighborCandidateSteps.Length ? neighborCandidateSteps[otherIndex] : Vector2.Zero;
            Vector2 p0 = selfStartPos - otherPos;
            Vector2 deltaRel = selfStep - otherStep;
            float rSep = selfCollisionRadius + otherRadius;

            float c = p0.LengthSquared() - (rSep * rSep);

            // Case 1: Initial Overlap
            if (c < 0f)
            {
                float relativeRadialVelocity = Vector2.Dot(p0, deltaRel);
                if (relativeRadialVelocity >= 0f)
                {
                    // Separating: allow movement to decouple
                    continue;
                }
                else
                {
                    // Converging: do not permit deeper penetration
                    selfStep = Vector2.Zero;
                    selfSpeed = 0f;
                    EmergencyStops++;
                    return true;
                }
            }

            // Case 2: Continuous Swept Quadratic Check
            float a = deltaRel.LengthSquared();
            if (a < 1e-8f)
            {
                continue;
            }

            float b = 2f * Vector2.Dot(p0, deltaRel);
            if (b >= 0f)
            {
                // Distance is increasing over time
                continue;
            }

            float disc = b * b - 4f * a * c;
            if (disc <= 0f)
            {
                // Trajectory misses sphere
                continue;
            }

            float tauHit = (-b - MathF.Sqrt(disc)) / (2f * a);
            if (tauHit >= 0f && tauHit <= 1.0f)
            {
                // Collision predicted! Determine who yields deterministically.
                bool selfYields = ShouldSelfYield(
                    world.GetUnitAt(selfUnitIndex).Id,
                    selfStartPos,
                    selfStep,
                    other.Id,
                    otherPos,
                    otherStep);

                float selfLen = selfStep.Length();
                float otherLen = otherStep.Length();
                float dotDir = (selfLen > 1e-4f && otherLen > 1e-4f)
                    ? Vector2.Dot(selfStep / selfLen, otherStep / otherLen)
                    : 0f;

                if (selfYields)
                {
                    float safeScale = FindSafeScaleAlpha(selfStartPos, selfStep, otherPos, otherStep, rSep + 0.005f);
                    earliestTau = MathF.Min(earliestTau, safeScale);
                    stepModified = true;
                }
                else
                {
                    // Priority vehicle: moving yields to stationary or stopped obstacle
                    if (otherLen <= 1e-4f || !IsPairSafe(selfStartPos, selfStep, otherPos, otherStep, rSep + 0.005f, out _))
                    {
                        float safeScale = FindSafeScaleAlpha(selfStartPos, selfStep, otherPos, otherStep, rSep + 0.005f);
                        earliestTau = MathF.Min(earliestTau, safeScale);
                        stepModified = true;
                    }
                }
            }
        }

        if (stepModified)
        {
            float safeTau = MathF.Max(0f, earliestTau);
            if (safeTau <= 1e-4f)
            {
                selfStep = Vector2.Zero;
                selfSpeed = 0f;
                EmergencyStops++;
            }
            else
            {
                selfStep *= safeTau;
                selfSpeed = selfStep.Length() / dt;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reconciles candidate displacements across all vehicles via multi-pass relaxation.
    /// Iterates until all active pairs satisfy continuous swept separation over tau in [0, 1].
    /// </summary>
    public void ReconcileDisplacements(
        WorldState world,
        IUnitMovementCatalog catalog,
        Span<Vector2> displacements,
        Span<float> speeds,
        float dt)
    {
        int unitCount = world.Count;
        const int maxPasses = 4;

        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool anyModified = false;

            for (int i = 0; i < unitCount; i++)
            {
                Vector2 stepI = displacements[i];
                if (stepI.LengthSquared() < 1e-8f) continue;

                UnitState unitI = world.GetUnitAt(i);
                float radiusI = 0f;
                float heightI = 2.5f;
                if (catalog.TryGetProfile(unitI.Definition, out UnitMovementProfile profI))
                {
                    radiusI = profI.EffectiveCollisionRadius;
                    heightI = profI.Height;
                }
                if (radiusI <= 0f) continue;

                Vector2 posI = _spatialGrid.GetPosition(i);
                float stepLenI = stepI.Length();
                float queryRadius = radiusI + stepLenI + 10.0f;

                int candidateCount = _spatialGrid.QueryNeighbors(posI, queryRadius, ref _candidateBuffer);
                CandidatesChecked += candidateCount;

                float surfaceY_I = _navGrid.Evaluator.GetSurfaceHeight(posI, unitI.SupportSurface);
                float topY_I = surfaceY_I + heightI;

                for (int c = 0; c < candidateCount; c++)
                {
                    int j = _candidateBuffer[c];
                    if (j == i) continue;

                    UnitState unitJ = world.GetUnitAt(j);
                    float radiusJ = 0f;
                    float heightJ = 2.5f;
                    if (catalog.TryGetProfile(unitJ.Definition, out UnitMovementProfile profJ))
                    {
                        radiusJ = profJ.EffectiveCollisionRadius;
                        heightJ = profJ.Height;
                    }
                    if (radiusJ <= 0f) continue;

                    Vector2 posJ = _spatialGrid.GetPosition(j);
                    float surfaceY_J = _navGrid.Evaluator.GetSurfaceHeight(posJ, unitJ.SupportSurface);
                    float topY_J = surfaceY_J + heightJ;
                    if (MathF.Max(surfaceY_I, surfaceY_J) >= MathF.Min(topY_I, topY_J))
                    {
                        continue;
                    }

                    Vector2 stepJ = displacements[j];
                    float rSep = radiusI + radiusJ;
                    float rSepSafe = rSep + 0.005f;

                    ExactChecks++;
                    if (!IsPairSafe(posI, stepI, posJ, stepJ, rSepSafe, out _))
                    {
                        bool iYields = ShouldSelfYield(unitI.Id, posI, stepI, unitJ.Id, posJ, stepJ);
                        bool jYields = ShouldSelfYield(unitJ.Id, posJ, stepJ, unitI.Id, posI, stepI);

                        if (iYields && !jYields)
                        {
                            float alpha = FindSafeScaleAlpha(posI, stepI, posJ, stepJ, rSepSafe);
                            Vector2 newStepI = stepI * alpha;
                            if ((stepI - newStepI).LengthSquared() > 1e-8f)
                            {
                                stepI = newStepI;
                                displacements[i] = stepI;
                                speeds[i] = stepI.Length() / dt;
                                anyModified = true;
                            }
                            if (!IsPairSafe(posI, stepI, posJ, stepJ, rSepSafe, out _))
                            {
                                float beta = FindSafeScaleAlpha(posJ, stepJ, posI, Vector2.Zero, rSepSafe);
                                Vector2 newStepJ = stepJ * beta;
                                if ((stepJ - newStepJ).LengthSquared() > 1e-8f)
                                {
                                    displacements[j] = newStepJ;
                                    speeds[j] = newStepJ.Length() / dt;
                                    anyModified = true;
                                }
                            }
                        }
                        else if (!iYields && jYields)
                        {
                            float beta = FindSafeScaleAlpha(posJ, stepJ, posI, stepI, rSepSafe);
                            Vector2 newStepJ = stepJ * beta;
                            if ((stepJ - newStepJ).LengthSquared() > 1e-8f)
                            {
                                displacements[j] = newStepJ;
                                speeds[j] = newStepJ.Length() / dt;
                                anyModified = true;
                            }
                            if (!IsPairSafe(posI, stepI, posJ, stepJ, rSepSafe, out _))
                            {
                                float alpha = FindSafeScaleAlpha(posI, stepI, posJ, Vector2.Zero, rSepSafe);
                                Vector2 newStepI = stepI * alpha;
                                if ((stepI - newStepI).LengthSquared() > 1e-8f)
                                {
                                    stepI = newStepI;
                                    displacements[i] = stepI;
                                    speeds[i] = stepI.Length() / dt;
                                    anyModified = true;
                                }
                            }
                        }
                        else
                        {
                            float mutualAlpha = FindMutualSafeScaleAlpha(posI, stepI, posJ, stepJ, rSepSafe);
                            Vector2 newStepI = stepI * mutualAlpha;
                            Vector2 newStepJ = stepJ * mutualAlpha;
                            if ((stepI - newStepI).LengthSquared() > 1e-8f)
                            {
                                stepI = newStepI;
                                displacements[i] = stepI;
                                speeds[i] = stepI.Length() / dt;
                                anyModified = true;
                            }
                            if ((stepJ - newStepJ).LengthSquared() > 1e-8f)
                            {
                                displacements[j] = newStepJ;
                                speeds[j] = newStepJ.Length() / dt;
                                anyModified = true;
                            }
                        }
                    }
                }
            }

            if (!anyModified) break;
        }
    }

    public static bool IsPairSafe(Vector2 posA, Vector2 stepA, Vector2 posB, Vector2 stepB, float rSep, out float minDist)
    {
        Vector2 r0 = posA - posB;
        float r0DistSq = r0.LengthSquared();
        Vector2 delta = stepA - stepB;
        float deltaSq = delta.LengthSquared();
        float tau = 0f;
        if (deltaSq > 1e-9f)
        {
            tau = Math.Clamp(-Vector2.Dot(r0, delta) / deltaSq, 0f, 1f);
        }
        Vector2 rClosest = r0 + tau * delta;
        float distSq = rClosest.LengthSquared();
        minDist = MathF.Sqrt(distSq);

        if (r0DistSq < rSep * rSep)
        {
            return Vector2.Dot(r0, delta) >= 0f && distSq >= r0DistSq - 1e-4f;
        }

        return distSq >= rSep * rSep;
    }

    public static float FindSafeScaleAlpha(Vector2 posA, Vector2 stepA, Vector2 posB, Vector2 stepB, float rSepSafe)
    {
        if (stepA.LengthSquared() < 1e-8f) return 0f;

        if (!IsPairSafe(posA, Vector2.Zero, posB, stepB, rSepSafe, out _))
        {
            return 0f;
        }

        float low = 0f;
        float high = 1f;
        for (int iter = 0; iter < 12; iter++)
        {
            float mid = 0.5f * (low + high);
            if (IsPairSafe(posA, stepA * mid, posB, stepB, rSepSafe, out _))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }

    public static float FindMutualSafeScaleAlpha(Vector2 posA, Vector2 stepA, Vector2 posB, Vector2 stepB, float rSepSafe)
    {
        if (!IsPairSafe(posA, Vector2.Zero, posB, Vector2.Zero, rSepSafe, out _))
        {
            return 0f;
        }

        float low = 0f;
        float high = 1f;
        for (int iter = 0; iter < 12; iter++)
        {
            float mid = 0.5f * (low + high);
            if (IsPairSafe(posA, stepA * mid, posB, stepB * mid, rSepSafe, out _))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }

    public static bool ShouldSelfYield(
        EntityId selfId,
        Vector2 selfPos,
        Vector2 selfStep,
        EntityId otherId,
        Vector2 otherPos,
        Vector2 otherStep)
    {
        float selfStepSq = selfStep.LengthSquared();
        float otherStepSq = otherStep.LengthSquared();

        // 1. Moving yields to stationary
        if (otherStepSq < 1e-8f)
        {
            return true;
        }
        if (selfStepSq < 1e-8f)
        {
            return false;
        }

        float selfLen = MathF.Sqrt(selfStepSq);
        float otherLen = MathF.Sqrt(otherStepSq);
        float dotDir = Vector2.Dot(selfStep / selfLen, otherStep / otherLen);

        // 2. Following: follower yields to leader
        if (dotDir > 0.5f)
        {
            Vector2 toOther = otherPos - selfPos;
            bool isBehind = Vector2.Dot(toOther, selfStep) > 0f;
            return isBehind;
        }

        // 3. Head-on: both yield (both clamp)
        if (dotDir < -0.5f)
        {
            return true;
        }

        // 4. Crossing: lower EntityId has priority; higher EntityId yields
        return selfId.Value > otherId.Value;
    }

    public static float DistancePointToSegment(Vector2 pt, Vector2 segA, Vector2 segB)
    {
        Vector2 seg = segB - segA;
        float segLenSq = seg.LengthSquared();
        if (segLenSq < 1e-8f)
        {
            return Vector2.Distance(pt, segA);
        }
        float t = Math.Clamp(Vector2.Dot(pt - segA, seg) / segLenSq, 0f, 1f);
        Vector2 proj = segA + seg * t;
        return Vector2.Distance(pt, proj);
    }

    public static float RayCircleHit(Vector2 rayOrigin, Vector2 rayDir, Vector2 circleCenter, float radius)
    {
        Vector2 d = rayOrigin - circleCenter;
        float a = rayDir.LengthSquared();
        if (a < 1e-8f) return 0f;
        float b = 2f * Vector2.Dot(d, rayDir);
        float c = d.LengthSquared() - (radius * radius);
        float disc = b * b - 4f * a * c;
        if (disc <= 0f) return 1f;
        float t = (-b - MathF.Sqrt(disc)) / (2f * a);
        return Math.Clamp(t, 0f, 1f);
    }

    public static float FindCrossingYieldAlpha(
        Vector2 selfPos, Vector2 selfStep,
        Vector2 otherPos, Vector2 otherStep,
        float rSep)
    {
        float dStart = DistancePointToSegment(selfPos, otherPos, otherPos + otherStep);
        if (dStart < rSep)
        {
            return 0f;
        }

        float low = 0f;
        float high = 1f;
        for (int iter = 0; iter < 12; iter++)
        {
            float mid = 0.5f * (low + high);
            if (DistancePointToSegment(selfPos + mid * selfStep, otherPos, otherPos + otherStep) < rSep)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        float stepLen = selfStep.Length();
        float marginAlpha = stepLen > 1e-4f ? 0.05f / stepLen : 0.05f;
        float alpha = MathF.Max(0f, low - marginAlpha);

        Vector2 candStep = selfStep * alpha;
        Vector2 relP0 = selfPos - otherPos;
        Vector2 relDelta = candStep - otherStep;
        float relA = relDelta.LengthSquared();
        if (relA > 1e-8f)
        {
            float relB = 2f * Vector2.Dot(relP0, relDelta);
            float relC = relP0.LengthSquared() - (rSep * rSep);
            float relDisc = relB * relB - 4f * relA * relC;
            if (relDisc > 0f)
            {
                float tSim = (-relB - MathF.Sqrt(relDisc)) / (2f * relA);
                if (tSim >= 0f && tSim <= 1f)
                {
                    alpha = MathF.Min(alpha, MathF.Max(0f, tSim - 0.05f));
                }
            }
        }

        return alpha;
    }
}
