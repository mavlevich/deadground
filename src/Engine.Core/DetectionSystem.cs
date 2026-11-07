using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-side visual perception system advancing detection on simulation ticks.
/// Executes scheduled line-of-sight checks against StaticGeometry, maintains spatial
/// candidate indices, and updates persistent per-side contact knowledge.
///
/// Fully allocation-free during simulation ticks.
/// </summary>
public sealed class DetectionSystem : ISimulationSystem
{
    private readonly StaticGeometry _geometry;
    private readonly IUnitPerceptionCatalog _catalog;
    private readonly DetectionTuning _tuning;
    private readonly SideContactTracker[] _trackers;
    private readonly SideWreckTracker[] _wreckTrackers;
    private readonly UnitSpatialGrid _spatialGrid;
    private readonly LineOfSightContext _losContext;
    private readonly int[] _candidateBuffer;
    private readonly uint[] _observedBitmask;
    private readonly SideContactTracker.ObservedTarget[] _observedTargetsBuffer;
    private readonly int[] _observedCounts;
    private readonly int[] _activeObserverIndices;
    private readonly int[] _activeObserversCounts;
    private readonly ContactId[] _emptyContactBuffer;
    private readonly Vector2[] _unitPositions;
    private readonly float[] _unitGroundHeights;
    private readonly int[] _unitOwners;
    private readonly int[] _unitTeams;
    private readonly ushort[] _unitDefinitions;
    private readonly EntityId[] _unitIds;
    private readonly bool[] _unitHasSensor;
    private readonly float[] _unitSensorRange;
    private readonly Vector3[] _unitEyePos;
    private readonly ContactClassification[] _unitClassifications;
    private readonly TargetObservationPoints?[] _unitTargetPoints;
    private readonly Vector3[] _unitTopTargetPoint;
    private readonly float[] _unitTurretYaws;
    private readonly float[] _unitHullHeadings;
    private readonly uint[] _unitSupportSurfaces;
    private readonly int _maxUnits;
    private readonly int _wordsPerSide;

    public DetectionTuning Tuning => _tuning;
    public StaticGeometry Geometry => _geometry;
    public int CandidateQueriesCount { get; private set; }
    public int LosQueriesCount { get; private set; }
    public int ObservationsCount { get; private set; }
    public int ConfirmedEmptyPositionsCount { get; private set; }

    public delegate void ObservationEstablishedDelegate(
        int tick,
        int observerSide,
        EntityId observerId,
        Vector2 observerPos,
        EntityId targetId,
        Vector2 targetPos,
        ContactClassification classification,
        ushort targetType);

    public ObservationEstablishedDelegate? OnObservationEstablished { get; set; }

    public DetectionSystem(
        StaticGeometry geometry,
        IUnitPerceptionCatalog catalog,
        DetectionTuning? tuning = null,
        int maxUnits = 1024,
        int maxSides = Simulation.MaxPlayers)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(catalog);

        _geometry = geometry;
        _catalog = catalog;
        _tuning = tuning ?? new DetectionTuning();
        _trackers = new SideContactTracker[maxSides];
        _wreckTrackers = new SideWreckTracker[maxSides];

        for (int i = 0; i < maxSides; i++)
        {
            _trackers[i] = new SideContactTracker(i + 1, _tuning);
            _wreckTrackers[i] = new SideWreckTracker();
        }

        _maxUnits = maxUnits;
        _wordsPerSide = (maxUnits + 31) / 32;
        _spatialGrid = new UnitSpatialGrid(geometry.HeightMap.Width, geometry.HeightMap.Depth, maxUnits);
        _losContext = new LineOfSightContext(geometry.Structures.Count);
        _candidateBuffer = new int[maxUnits];
        _observedBitmask = new uint[maxSides * _wordsPerSide];
        _observedTargetsBuffer = new SideContactTracker.ObservedTarget[maxSides * maxUnits];
        _observedCounts = new int[maxSides];
        _activeObserverIndices = new int[maxSides * maxUnits];
        _activeObserversCounts = new int[maxSides];
        _emptyContactBuffer = new ContactId[maxUnits];
        _unitPositions = new Vector2[maxUnits];
        _unitGroundHeights = new float[maxUnits];
        _unitOwners = new int[maxUnits];
        _unitTeams = new int[maxUnits];
        _unitDefinitions = new ushort[maxUnits];
        _unitIds = new EntityId[maxUnits];
        _unitHasSensor = new bool[maxUnits];
        _unitSensorRange = new float[maxUnits];
        _unitClassifications = new ContactClassification[maxUnits];
        _unitEyePos = new Vector3[maxUnits];
        _unitTargetPoints = new TargetObservationPoints?[maxUnits];
        _unitTopTargetPoint = new Vector3[maxUnits];
        _unitTurretYaws = new float[maxUnits];
        _unitHullHeadings = new float[maxUnits];
        _unitSupportSurfaces = new uint[maxUnits];
    }

    public void Update(WorldState world, int tick)
    {
        CandidateQueriesCount = 0;
        LosQueriesCount = 0;
        ObservationsCount = 0;
        ConfirmedEmptyPositionsCount = 0;

        int unitCount = world.Count;

        // Copy unit state and precompute perception endpoints once per tick
        for (int i = 0; i < unitCount; i++)
        {
            UnitState u = world.GetUnitAt(i);
            _unitPositions[i] = u.Position;
            _unitSupportSurfaces[i] = u.SupportSurface;
            float groundY = _geometry.GetSurfaceHeight(u.Position, u.SupportSurface);
            _unitGroundHeights[i] = groundY;
            _unitOwners[i] = u.Owner;
            _unitTeams[i] = world.GetPlayerTeam(u.Owner);
            _unitDefinitions[i] = u.Definition;
            _unitIds[i] = u.Id;
            bool hasCombat = world.TryGetCombatState(u.Id, out WorldState.CombatState cs);
            _unitTurretYaws[i] = hasCombat ? cs.TurretYaw : 0f;
            _unitHullHeadings[i] = hasCombat ? cs.HullHeading : 0f;

            if (_catalog.TryGetProfile(u.Definition, out UnitPerceptionProfile profile))
            {
                _unitClassifications[i] = profile.Classification;
                if (profile.Sensor is not null)
                {
                    _unitHasSensor[i] = true;
                    _unitSensorRange[i] = profile.Sensor.Range;
                    _unitEyePos[i] = new Vector3(u.Position.X, groundY + profile.Sensor.EyeHeight, u.Position.Y);
                }
                else
                {
                    _unitHasSensor[i] = false;
                }

                if (profile.TargetPoints is not null)
                {
                    _unitTargetPoints[i] = profile.TargetPoints;
                    float topRelY = profile.TargetPoints.RelativeHeights[^1];
                    _unitTopTargetPoint[i] = new Vector3(u.Position.X, groundY + topRelY, u.Position.Y);
                }
                else
                {
                    _unitTargetPoints[i] = null;
                }
            }
            else
            {
                _unitHasSensor[i] = false;
                _unitClassifications[i] = ContactClassification.Vehicle;
                _unitTargetPoints[i] = null;
            }
        }

        // 1. Rebuild spatial grid of living units
        _spatialGrid.Build(_unitPositions.AsSpan(0, unitCount));

        // 2. Prepare side trackers and clear per-step observation buffers
        Array.Clear(_observedBitmask);
        Array.Clear(_observedCounts);
        Array.Clear(_activeObserversCounts);
        for (int s = 0; s < _trackers.Length; s++)
        {
            _trackers[s].BeginDetectionStep();
        }

        uint interval = (uint)_tuning.EvaluationIntervalTicks;
        uint scheduledPhase = (uint)(tick % _tuning.EvaluationIntervalTicks);

        // 3. Process scheduled observers
        for (int i = 0; i < unitCount; i++)
        {
            int observerOwner = _unitOwners[i];
            int sideIndex = observerOwner - 1;
            if (sideIndex < 0 || sideIndex >= _trackers.Length)
                continue;

            // Deterministic observer schedule spreading across evaluation interval ticks
            if ((_unitIds[i].Value % interval) != scheduledPhase)
                continue;

            if (!_unitHasSensor[i])
                continue;

            int actCount = _activeObserversCounts[sideIndex];
            if (actCount < _maxUnits)
            {
                _activeObserverIndices[sideIndex * _maxUnits + actCount] = i;
                _activeObserversCounts[sideIndex] = actCount + 1;
            }

            Vector3 eyePos = _unitEyePos[i];
            float sensorRange = _unitSensorRange[i];
            Vector2 obsPos = _unitPositions[i];

            // Query hostile candidate targets within visual sensor range using spatial grid
            int candidates = _spatialGrid.QueryHostileCandidates(
                obsPos,
                sensorRange,
                _unitTeams[i],
                _unitPositions.AsSpan(0, unitCount),
                _unitTeams.AsSpan(0, unitCount),
                _candidateBuffer);

            CandidateQueriesCount += candidates;
            int bitmaskSideOffset = sideIndex * _wordsPerSide;

            for (int c = 0; c < candidates; c++)
            {
                int targetUnitIndex = _candidateBuffer[c];

                // Skip targets already confirmed observed by this side in this tick
                int word = bitmaskSideOffset + (targetUnitIndex >> 5);
                uint bit = 1u << (targetUnitIndex & 31);
                if ((_observedBitmask[word] & bit) != 0)
                    continue;

                TargetObservationPoints? points = _unitTargetPoints[targetUnitIndex];
                if (points is null)
                    continue;

                Vector3 topPoint = _unitTopTargetPoint[targetUnitIndex];
                LosQueriesCount++;

                bool isObserved = false;
                if (_geometry.IsLineOfSightClear(eyePos, topPoint, _losContext, out bool blockedByTerrain))
                {
                    isObserved = true;
                }
                else if (!blockedByTerrain && points.RelativeHeights.Length > 1)
                {
                    Vector2 targetPos = _unitPositions[targetUnitIndex];
                    float tx = targetPos.X;
                    float tz = targetPos.Y;
                    float targetGroundY = _unitGroundHeights[targetUnitIndex];
                    for (int p = points.RelativeHeights.Length - 2; p >= 0; p--)
                    {
                        Vector3 targetPointPos = new(tx, targetGroundY + points.RelativeHeights[p], tz);
                        LosQueriesCount++;

                        if (_geometry.IsLineOfSightClear(eyePos, targetPointPos, _losContext, out bool lowerBlockedByTerrain))
                        {
                            isObserved = true;
                            break;
                        }

                        if (lowerBlockedByTerrain)
                        {
                            break;
                        }
                    }
                }

                if (isObserved)
                {
                    ObservationsCount++;
                    _observedBitmask[word] |= bit;
                    ContactClassification targetClass = _unitClassifications[targetUnitIndex];
                    ushort targetType = _unitDefinitions[targetUnitIndex];
                    int count = _observedCounts[sideIndex];
                    if (count < _maxUnits)
                    {
                        _observedTargetsBuffer[sideIndex * _maxUnits + count] = new SideContactTracker.ObservedTarget(
                            _unitIds[targetUnitIndex],
                            _unitPositions[targetUnitIndex],
                            targetClass,
                            targetType,
                            _unitTurretYaws[targetUnitIndex],
                            _unitHullHeadings[targetUnitIndex],
                            _unitSupportSurfaces[targetUnitIndex]);
                        _observedCounts[sideIndex] = count + 1;
                    }

                    OnObservationEstablished?.Invoke(
                        tick,
                        observerOwner,
                        _unitIds[i],
                        obsPos,
                        _unitIds[targetUnitIndex],
                        _unitPositions[targetUnitIndex],
                        targetClass,
                        targetType);
                }
            }
        }

        // 4. Verify line of sight to old positions of LastKnown entries, and batch-register observations
        for (int s = 0; s < _trackers.Length; s++)
        {
            int activeObsCount = _activeObserversCounts[s];
            SideContactTracker tracker = _trackers[s];
            if (activeObsCount > 0)
            {
                ReadOnlySpan<SideContactTracker.Entry> internalEntries = tracker.InternalEntries;
                for (int e = 0; e < internalEntries.Length; e++)
                {
                    ref readonly SideContactTracker.Entry entry = ref internalEntries[e];
                    if (entry.State == ContactState.LastKnown &&
                        IsPositionInLineOfSight(entry.Position, entry.SupportSurface, entry.Type, s))
                    {
                        tracker.MarkPositionObserved(e);
                    }
                }
            }

            int count = _observedCounts[s];
            ReadOnlySpan<SideContactTracker.ObservedTarget> targets = count > 0
                ? _observedTargetsBuffer.AsSpan(s * _maxUnits, count)
                : [];
            tracker.RegisterObservations(targets, tick);
        }

        // 5. Re-check LastKnown contacts for confirmed empty positions
        for (int s = 0; s < _trackers.Length; s++)
        {
            SideContactTracker tracker = _trackers[s];
            ReadOnlySpan<SideContactTracker.Entry> entries = tracker.InternalEntries;
            int emptyCount = 0;
            int obsCount = _observedCounts[s];
            ReadOnlySpan<SideContactTracker.ObservedTarget> obsTargets = obsCount > 0
                ? _observedTargetsBuffer.AsSpan(s * _maxUnits, obsCount)
                : [];
            float toleranceSq = _tuning.SamePositionTolerance * _tuning.SamePositionTolerance;

            for (int e = 0; e < entries.Length; e++)
            {
                ref readonly SideContactTracker.Entry entry = ref entries[e];
                if (entry.State != ContactState.LastKnown || entry.ObservedThisStep)
                    continue;

                // Only positions with confirmed line of sight can be confirmed empty
                if (!entry.PositionObservedThisStep)
                    continue;

                // Check if position is occupied by any current observation of this side on the same support surface
                bool isPositionOccupied = false;
                for (int t = 0; t < obsTargets.Length; t++)
                {
                    ref readonly SideContactTracker.ObservedTarget target = ref obsTargets[t];
                    if (target.SupportSurface == entry.SupportSurface &&
                        Vector2.DistanceSquared(target.Position, entry.Position) <= toleranceSq)
                    {
                        isPositionOccupied = true;
                        break;
                    }
                }

                if (isPositionOccupied)
                    continue;

                if (emptyCount < _maxUnits)
                {
                    _emptyContactBuffer[emptyCount++] = entry.Id;
                }
            }

            if (emptyCount > 0)
            {
                ConfirmedEmptyPositionsCount += emptyCount;
                tracker.RemoveConfirmedEmpty(_emptyContactBuffer.AsSpan(0, emptyCount));
            }
        }

        // 6. Finalize side trackers: transition unobserved current contacts and expire
        for (int s = 0; s < _trackers.Length; s++)
        {
            _trackers[s].EndDetectionStep(tick);
        }

        // 7. Check line of sight to unobserved wrecks
        ReadOnlySpan<WreckState> wrecks = world.Wrecks;
        if (wrecks.Length > 0)
        {
            for (int s = 0; s < _wreckTrackers.Length; s++)
            {
                if (_activeObserversCounts[s] == 0)
                    continue;

                SideWreckTracker tracker = _wreckTrackers[s];
                for (int w = 0; w < wrecks.Length; w++)
                {
                    ref readonly WreckState wreck = ref wrecks[w];
                    if (tracker.Contains(wreck.Id))
                        continue;

                    if (IsPositionInLineOfSight(wreck.Position, wreck.SupportSurface, wreck.Definition, s))
                    {
                        tracker.TryAdd(wreck.ToObservation());
                    }
                }
            }
        }
    }

    private bool IsPositionInLineOfSight(Vector2 lkPos, uint supportSurface, ushort unitType, int sideIndex)
    {
        int activeObsCount = _activeObserversCounts[sideIndex];
        if (activeObsCount == 0)
            return false;

        float groundY = _geometry.GetSurfaceHeight(lkPos, supportSurface);
        bool hasProfile = _catalog.TryGetProfile(unitType, out UnitPerceptionProfile profile);
        TargetObservationPoints? points = hasProfile ? profile.TargetPoints : null;

        for (int o = 0; o < activeObsCount; o++)
        {
            int obsIndex = _activeObserverIndices[sideIndex * _maxUnits + o];
            float range = _unitSensorRange[obsIndex];
            float rangeSq = range * range;

            if (Vector2.DistanceSquared(_unitPositions[obsIndex], lkPos) > rangeSq)
                continue;

            Vector3 eyePos = _unitEyePos[obsIndex];

            if (points is not null && points.RelativeHeights.Length > 0)
            {
                Vector3 topPt = new(lkPos.X, groundY + points.RelativeHeights[^1], lkPos.Y);
                LosQueriesCount++;
                if (_geometry.IsLineOfSightClear(eyePos, topPt, _losContext, out bool blockedByTerrain))
                {
                    return true;
                }

                if (!blockedByTerrain && points.RelativeHeights.Length > 1)
                {
                    for (int p = points.RelativeHeights.Length - 2; p >= 0; p--)
                    {
                        Vector3 lowerPt = new(lkPos.X, groundY + points.RelativeHeights[p], lkPos.Y);
                        LosQueriesCount++;
                        if (_geometry.IsLineOfSightClear(eyePos, lowerPt, _losContext, out bool lowerBlocked))
                        {
                            return true;
                        }
                        if (lowerBlocked) break;
                    }
                }
            }
            else
            {
                Vector3 defaultPt = new(lkPos.X, groundY + 1.8f, lkPos.Y);
                LosQueriesCount++;
                if (_geometry.IsLineOfSightClear(eyePos, defaultPt, _losContext, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the current perceived contacts for a given player/side.
    /// Does not advance timers, allocate IDs, or mutate contact states.
    /// </summary>
    public ReadOnlySpan<ContactObservation> GetContacts(int player)
    {
        int index = player - 1;
        if (index < 0 || index >= _trackers.Length)
            return [];

        return _trackers[index].ExportContacts();
    }

    public SideContactTracker GetTracker(int player)
    {
        int index = player - 1;
        if (index < 0 || index >= _trackers.Length)
            throw new ArgumentOutOfRangeException(nameof(player));

        return _trackers[index];
    }

    public bool IsEntityObservedBy(int player, EntityId entityId)
    {
        int index = player - 1;
        if (index < 0 || index >= _trackers.Length) return false;
        return _trackers[index].IsEntityCurrentlyObserved(entityId);
    }

    public bool TryResolveContact(int player, ContactId contactId, out EntityId entityId)
    {
        int index = player - 1;
        if (index < 0 || index >= _trackers.Length) { entityId = default; return false; }
        return _trackers[index].TryGetBoundEntity(contactId, out entityId);
    }

    public bool TryGetContact(int player, ContactId contactId, out ContactObservation contact)
    {
        int index = player - 1;
        if (index < 0 || index >= _trackers.Length) { contact = default; return false; }
        return _trackers[index].TryGetContact(contactId, out contact);
    }

    public bool TryGetContactByEntity(int player, EntityId entityId, out ContactObservation contact)
    {
        int index = player - 1;
        if (index < 0 || index >= _trackers.Length) { contact = default; return false; }
        return _trackers[index].TryGetContactByEntity(entityId, out contact);
    }

    public void NotifyEntityDestroyed(EntityId victimId)
    {
        for (int i = 0; i < _trackers.Length; i++)
        {
            _trackers[i].NotifyEntityDestroyed(victimId);
        }
    }

    public ReadOnlySpan<WreckObservation> GetKnownWrecks(int player)
    {
        int index = player - 1;
        if (index < 0 || index >= _wreckTrackers.Length)
            return [];

        return _wreckTrackers[index].ExportWrecks();
    }

    public void NotifyWreckCreated(WreckState wreck, int victimOwner, EntityId victimId)
    {
        WreckObservation obs = wreck.ToObservation();
        int victimIndex = victimOwner - 1;
        if (victimIndex >= 0 && victimIndex < _wreckTrackers.Length)
        {
            _wreckTrackers[victimIndex].TryAdd(obs);
        }

        for (int p = 1; p <= _wreckTrackers.Length; p++)
        {
            if (p == victimOwner) continue;
            if (IsEntityObservedBy(p, victimId))
            {
                _wreckTrackers[p - 1].TryAdd(obs);
            }
        }
    }

    public void ResetPlayer(int player)
    {
        int index = player - 1;
        if (index >= 0 && index < _wreckTrackers.Length)
        {
            _wreckTrackers[index].Clear();
        }
    }
}
