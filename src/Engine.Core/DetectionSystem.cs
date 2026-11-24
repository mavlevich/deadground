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
    public const int HiddenLaunchConcealmentTicks = 14;
    public const float MissileMotorVisualSignature = 1.35f;
    public const float MissileCoastingVisualSignature = 0.65f;

    private readonly StaticGeometry _geometry;
    private readonly IUnitPerceptionCatalog _catalog;
    private readonly DetectionTuning _tuning;
    private readonly SmokeSystem? _smokeSystem;
    private BuildingDestructionSystem? _buildingDestruction;
    private readonly SideContactTracker[] _trackers;
    private readonly SideWreckTracker[] _wreckTrackers;
    private readonly SideSmokeTracker[] _smokeTrackers;
    private readonly SideStructureTracker[] _structureTrackers;
    private readonly UnitSpatialGrid _spatialGrid;
    private readonly LineOfSightContext _losContext;
    private readonly LineOfSightContext _staticOnlyLosContext;
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
    private readonly float[] _unitVisualSignatures;
    private readonly Vector3[] _unitEyePos;
    private readonly ContactClassification[] _unitClassifications;
    private readonly TargetObservationPoints?[] _unitTargetPoints;
    private readonly Vector3[] _unitTopTargetPoint;
    private readonly float[] _unitTurretYaws;
    private readonly float[] _unitHullHeadings;
    private readonly uint[] _unitSupportSurfaces;
    private readonly float[] _unitAltitudes;
    private readonly FlightPhase[] _unitFlightPhases;
    private readonly float[] _unitGunPitches;
    private readonly int _maxUnits;
    private readonly int _wordsPerSide;
    private int _lastUnitCount;

    public DetectionTuning Tuning => _tuning;
    public StaticGeometry Geometry => _geometry;
    internal SmokeSystem? SmokeSystem => _smokeSystem;
    internal BuildingDestructionSystem? BuildingDestruction => _buildingDestruction;
    public void SetBuildingDestructionSystem(BuildingDestructionSystem? buildingDestruction) =>
        _buildingDestruction = buildingDestruction;
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
        int maxSides = Simulation.MaxPlayers,
        SmokeSystem? smokeSystem = null,
        BuildingDestructionSystem? buildingDestruction = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(catalog);

        _geometry = geometry;
        _catalog = catalog;
        _tuning = tuning ?? new DetectionTuning();
        _smokeSystem = smokeSystem;
        _buildingDestruction = buildingDestruction;
        _trackers = new SideContactTracker[maxSides];
        _wreckTrackers = new SideWreckTracker[maxSides];
        _smokeTrackers = new SideSmokeTracker[maxSides];
        _structureTrackers = new SideStructureTracker[maxSides];

        for (int i = 0; i < maxSides; i++)
        {
            _trackers[i] = new SideContactTracker(i + 1, _tuning);
            _wreckTrackers[i] = new SideWreckTracker();
            _smokeTrackers[i] = new SideSmokeTracker();
            _structureTrackers[i] = new SideStructureTracker();
        }

        _maxUnits = maxUnits;
        _wordsPerSide = (maxUnits + 31) / 32;
        _spatialGrid = new UnitSpatialGrid(geometry.HeightMap.Width, geometry.HeightMap.Depth, maxUnits);
        _losContext = new LineOfSightContext(geometry.Structures.Count);
        _staticOnlyLosContext = new LineOfSightContext(geometry.Structures.Count);
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
        _unitVisualSignatures = new float[maxUnits];
        _unitClassifications = new ContactClassification[maxUnits];
        _unitEyePos = new Vector3[maxUnits];
        _unitTargetPoints = new TargetObservationPoints?[maxUnits];
        _unitTopTargetPoint = new Vector3[maxUnits];
        _unitTurretYaws = new float[maxUnits];
        _unitHullHeadings = new float[maxUnits];
        _unitSupportSurfaces = new uint[maxUnits];
        _unitAltitudes = new float[maxUnits];
        _unitFlightPhases = new FlightPhase[maxUnits];
        _unitGunPitches = new float[maxUnits];
    }

    public void Update(WorldState world, int tick)
    {
        CandidateQueriesCount = 0;
        LosQueriesCount = 0;
        ObservationsCount = 0;
        ConfirmedEmptyPositionsCount = 0;

        if (_smokeSystem is not null)
        {
            _smokeSystem.CopyActiveVolumesTo(_losContext);
        }
        else
        {
            _losContext.SetSmokeVolumes(null, 0);
        }

        if (_buildingDestruction is not null)
        {
            _buildingDestruction.CopyHeightOverridesTo(_losContext);
            _buildingDestruction.CopyHeightOverridesTo(_staticOnlyLosContext);
        }
        else
        {
            _losContext.SetStructureMaxYOverrides(null, 0);
            _staticOnlyLosContext.SetStructureMaxYOverrides(null, 0);
        }

        int unitCount = world.Count;
        _lastUnitCount = unitCount;

        // Copy unit state and precompute perception endpoints once per tick
        float maxVisualSignature = 1.0f;
        for (int i = 0; i < unitCount; i++)
        {
            UnitState u = world.GetUnitAt(i);
            _unitPositions[i] = u.Position;
            _unitSupportSurfaces[i] = u.SupportSurface;
            float groundY = _geometry.GetSurfaceHeight(u.Position, u.SupportSurface);
            float altitude = 0f;
            FlightPhase flightPhase = FlightPhase.Ground;
            if (world.TryGetFlightState(u.Id, out WorldState.UnitFlightState fs) && fs.HasCapability)
            {
                altitude = fs.Altitude;
                flightPhase = fs.Phase;
            }
            _unitAltitudes[i] = altitude;
            _unitFlightPhases[i] = flightPhase;
            float baseElevationY = groundY + altitude;
            _unitGroundHeights[i] = baseElevationY;
            _unitOwners[i] = u.Owner;
            _unitTeams[i] = world.GetPlayerTeam(u.Owner);
            _unitDefinitions[i] = u.Definition;
            _unitIds[i] = u.Id;
            bool hasCombat = world.TryGetCombatState(u.Id, out WorldState.CombatState cs);
            _unitTurretYaws[i] = hasCombat ? cs.TurretYaw : 0f;
            _unitHullHeadings[i] = hasCombat ? cs.HullHeading : 0f;
            _unitGunPitches[i] = world.TryGetArtilleryState(u.Id, out WorldState.UnitArtilleryState artState) ? artState.GunPitchRad : 0f;

            if (world.IsContained(u.Id))
            {
                _unitHasSensor[i] = false;
                _unitVisualSignatures[i] = 1.0f;
                _unitClassifications[i] = ContactClassification.Infantry;
                _unitTargetPoints[i] = null;
                continue;
            }

            if (_catalog.TryGetProfile(u.Definition, out UnitPerceptionProfile profile))
            {
                _unitClassifications[i] = profile.Classification;
                float signature = float.IsFinite(profile.VisualSignature) && profile.VisualSignature > 0f
                    ? profile.VisualSignature
                    : 1.0f;
                _unitVisualSignatures[i] = signature;
                if (signature > maxVisualSignature)
                {
                    maxVisualSignature = signature;
                }

                if (profile.Sensor is not null && flightPhase != FlightPhase.Crashing)
                {
                    _unitHasSensor[i] = true;
                    _unitSensorRange[i] = profile.Sensor.Range;
                    _unitEyePos[i] = new Vector3(u.Position.X, baseElevationY + profile.Sensor.EyeHeight, u.Position.Y);
                }
                else
                {
                    _unitHasSensor[i] = false;
                }

                if (profile.TargetPoints is not null)
                {
                    _unitTargetPoints[i] = profile.TargetPoints;
                    float topRelY = profile.TargetPoints.RelativeHeights[^1];
                    _unitTopTargetPoint[i] = new Vector3(u.Position.X, baseElevationY + topRelY, u.Position.Y);
                }
                else
                {
                    _unitTargetPoints[i] = null;
                }
            }
            else
            {
                _unitHasSensor[i] = false;
                _unitVisualSignatures[i] = 1.0f;
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

            // Query hostile candidate targets within maximum signature-scaled visual range using spatial grid
            int candidates = _spatialGrid.QueryHostileCandidates(
                obsPos,
                sensorRange * maxVisualSignature,
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

                // Apply target visual signature scaling before executing line-of-sight geometry checks
                float effectiveRange = sensorRange * _unitVisualSignatures[targetUnitIndex];
                if (Vector2.DistanceSquared(obsPos, _unitPositions[targetUnitIndex]) > effectiveRange * effectiveRange)
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
                            _unitSupportSurfaces[targetUnitIndex],
                            _unitAltitudes[targetUnitIndex],
                            _unitFlightPhases[targetUnitIndex],
                            _unitGunPitches[targetUnitIndex]);
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

        // 8. Filter observed smoke clouds per side (own/allied clouds always visible; hostile clouds require LOS)
        UpdateVisibleSmokeClouds(world, unitCount, tick);

        // 9. Update per-side last-observed state for damaged/collapsed destructible structures
        UpdateKnownStructures(world, unitCount);
    }

    private void UpdateKnownStructures(WorldState world, int unitCount)
    {
        int structCount = world.DestructibleStructureCount;
        if (structCount == 0)
            return;

        for (int s = 0; s < _structureTrackers.Length; s++)
        {
            SideStructureTracker tracker = _structureTrackers[s];
            int sidePlayer = s + 1;
            int sideTeam = world.GetPlayerTeam(sidePlayer);

            for (int i = 0; i < structCount; i++)
            {
                ref readonly DestructibleStructureState st = ref world.GetDestructibleStructureRef(i);
                if (!st.IsDamaged)
                    continue;

                if (IsStructureVisibleToTeam(in st, unitCount, sideTeam))
                {
                    tracker.TryUpsert(st.ToObservation());
                }
            }
        }
    }

    public bool IsStructureObservedBy(int player, in DestructibleStructureState structure, WorldState world)
    {
        int index = player - 1;
        if (index < 0 || index >= _structureTrackers.Length)
            return false;

        int team = world.GetPlayerTeam(player);
        int unitCount = Math.Min(_lastUnitCount, _maxUnits);
        return IsStructureVisibleToTeam(in structure, unitCount, team);
    }

    private bool IsStructureVisibleToTeam(
        in DestructibleStructureState structure,
        int unitCount,
        int observerTeam)
    {
        float cx = (structure.Min.X + structure.Max.X) * 0.5f;
        float cz = (structure.Min.Z + structure.Max.Z) * 0.5f;
        float halfX = (structure.Max.X - structure.Min.X) * 0.5f;
        float halfZ = (structure.Max.Z - structure.Min.Z) * 0.5f;
        float approxRadius = MathF.Sqrt(halfX * halfX + halfZ * halfZ);
        float currentMaxY = structure.CurrentMaxY;
        float sampleY = currentMaxY > structure.Min.Y + 0.5f
            ? MathF.Max(structure.Min.Y + 0.5f, currentMaxY - 0.35f)
            : structure.Min.Y + 0.35f;
        ReadOnlySpan<SmokeVolume> activeSmoke = _losContext.ActiveSmoke;

        for (int u = 0; u < unitCount; u++)
        {
            if (!_unitHasSensor[u] || _unitTeams[u] != observerTeam)
                continue;

            Vector2 obsPos = _unitPositions[u];
            float maxDist = _unitSensorRange[u] + approxRadius;
            float dx = obsPos.X - cx;
            float dz = obsPos.Y - cz;
            float distSq = dx * dx + dz * dz;
            if (distSq > maxDist * maxDist)
                continue;

            // Test nearest observer-facing point just outside the structure AABB so the structure
            // itself does not self-occlude the sightline check.
            float clampedX = Math.Clamp(obsPos.X, structure.Min.X, structure.Max.X);
            float clampedZ = Math.Clamp(obsPos.Y, structure.Min.Z, structure.Max.Z);
            float fdx = obsPos.X - clampedX;
            float fdz = obsPos.Y - clampedZ;
            float faceDistSq = fdx * fdx + fdz * fdz;
            Vector3 targetPt;
            if (faceDistSq > 1e-4f)
            {
                float inv = 1f / MathF.Sqrt(faceDistSq);
                targetPt = new Vector3(clampedX + fdx * inv * 0.25f, sampleY, clampedZ + fdz * inv * 0.25f);
            }
            else
            {
                targetPt = new Vector3(cx, currentMaxY + 0.25f, cz);
            }

            LosQueriesCount++;
            if (!_geometry.IsLineOfSightClear(_unitEyePos[u], targetPt, _staticOnlyLosContext, out _))
                continue;

            // Intervening tactical smoke screens still conceal the structure, but a dust cloud
            // centered on this structure's own footprint does not hide its own collapse from observers.
            if (activeSmoke.Length > 0 && IsBlockedByInterveningSmoke(_unitEyePos[u], targetPt, in structure, activeSmoke))
                continue;

            return true;
        }

        return false;
    }

    private static bool IsBlockedByInterveningSmoke(
        Vector3 eyePos,
        Vector3 targetPt,
        in DestructibleStructureState structure,
        ReadOnlySpan<SmokeVolume> activeSmoke)
    {
        for (int i = 0; i < activeSmoke.Length; i++)
        {
            ref readonly SmokeVolume vol = ref activeSmoke[i];
            if (vol.CenterX >= structure.Min.X - 1f && vol.CenterX <= structure.Max.X + 1f &&
                vol.CenterZ >= structure.Min.Z - 1f && vol.CenterZ <= structure.Max.Z + 1f)
            {
                continue;
            }

            if (!SmokeLos.IsSmokeClear(eyePos, targetPt, activeSmoke.Slice(i, 1)))
            {
                return true;
            }
        }
        return false;
    }

    private void UpdateVisibleSmokeClouds(WorldState world, int unitCount, int tick)
    {
        ReadOnlySpan<SmokeCloudState> clouds = world.SmokeClouds;
        ReadOnlySpan<SmokeVolume> activeVolumes = _smokeSystem is not null ? _smokeSystem.ActiveVolumes : [];

        for (int s = 0; s < _smokeTrackers.Length; s++)
        {
            SideSmokeTracker tracker = _smokeTrackers[s];
            tracker.Clear();

            if (clouds.Length == 0)
            {
                continue;
            }

            int sidePlayer = s + 1;
            int sideTeam = world.GetPlayerTeam(sidePlayer);

            for (int c = 0; c < clouds.Length; c++)
            {
                ref readonly SmokeCloudState cloud = ref clouds[c];
                if (cloud.IsExpired(tick))
                {
                    continue;
                }

                if (!world.AreHostile(sidePlayer, cloud.Owner))
                {
                    tracker.TryAdd(cloud.ToObservation());
                    continue;
                }

                if (IsSmokeCloudVisibleToTeam(in cloud, unitCount, sideTeam, tick, activeVolumes))
                {
                    tracker.TryAdd(cloud.ToObservation());
                }
            }
        }
    }

    private bool IsSmokeCloudVisibleToTeam(
        in SmokeCloudState cloud,
        int unitCount,
        int observerTeam,
        int tick,
        ReadOnlySpan<SmokeVolume> activeVolumes)
    {
        cloud.EvaluateAtTick(tick, out float effectiveRadius, out _);
        float visibleRadius = MathF.Max(effectiveRadius, cloud.Radius * SmokeCloudEvaluation.MinBloomRadiusFraction);
        float baseY = _geometry.GetSurfaceHeight(cloud.Position, cloud.SupportSurface);
        float topY = baseY + cloud.Height;
        float sampleY = baseY + cloud.Height * 0.6f;
        Vector3 cloudCenterPt = new(cloud.Position.X, sampleY, cloud.Position.Y);

        for (int u = 0; u < unitCount; u++)
        {
            if (!_unitHasSensor[u] || _unitTeams[u] != observerTeam)
            {
                continue;
            }

            Vector2 obsPos = _unitPositions[u];
            float maxDist = _unitSensorRange[u] + visibleRadius;
            float dx = obsPos.X - cloud.Position.X;
            float dz = obsPos.Y - cloud.Position.Y;
            float distSq = dx * dx + dz * dz;
            if (distSq > maxDist * maxDist)
            {
                continue;
            }

            Vector3 eyePos = _unitEyePos[u];
            float surfaceDistThreshold = visibleRadius + 0.1f;

            if (distSq <= surfaceDistThreshold * surfaceDistThreshold)
            {
                // Observer is horizontally inside or adjacent to the cloud footprint:
                // always require unobstructed static LOS to the cloud body (blocks through walls and bridge decks).
                float clampedY = Math.Clamp(eyePos.Y, baseY + 0.35f, MathF.Max(baseY + 0.35f, topY - 0.15f));
                Vector3 insidePt = new(cloud.Position.X, clampedY, cloud.Position.Y);
                LosQueriesCount++;
                if (!_geometry.IsLineOfSightClear(eyePos, insidePt, _staticOnlyLosContext, out _))
                {
                    continue;
                }

                // If observer eye is vertically within the smoke column, it is directly in the smoke.
                if (eyePos.Y >= baseY - 0.5f && eyePos.Y <= topY + 0.5f)
                {
                    return true;
                }

                // Otherwise (e.g., elevated above or below an open cloud), test outer top/bottom surface against other smoke.
                float outerSurfaceY = eyePos.Y > topY ? topY + 0.05f : baseY - 0.05f;
                Vector3 verticalSurfacePt = new(cloud.Position.X, outerSurfaceY, cloud.Position.Y);
                if (activeVolumes.Length == 0 || SmokeLos.IsSmokeClear(eyePos, verticalSurfacePt, activeVolumes, excludeId: cloud.Id))
                {
                    return true;
                }
                continue;
            }

            // Observer is outside the cloud horizontally: test visibility to the outer observer-facing surface
            // so overlapping clouds at the same position do not mutually occlude each other.
            float invDist = 1.0f / MathF.Sqrt(distSq);
            float outerOffset = visibleRadius + 0.05f;
            Vector3 surfacePt = new(
                cloud.Position.X + dx * invDist * outerOffset,
                sampleY,
                cloud.Position.Y + dz * invDist * outerOffset);

            LosQueriesCount++;
            if (!_geometry.IsLineOfSightClear(eyePos, surfacePt, _staticOnlyLosContext, out _))
            {
                continue;
            }

            // Prevent a cloud centered behind an opaque wall from leaking through the wall to its outer radius.
            LosQueriesCount++;
            if (!_geometry.IsLineOfSightClear(cloudCenterPt, surfacePt, _staticOnlyLosContext, out _))
            {
                continue;
            }

            if (activeVolumes.Length == 0 || SmokeLos.IsSmokeClear(eyePos, surfacePt, activeVolumes, excludeId: cloud.Id))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsPositionInLineOfSight(Vector2 lkPos, uint supportSurface, ushort unitType, int sideIndex)
    {
        int activeObsCount = _activeObserversCounts[sideIndex];
        if (activeObsCount == 0)
            return false;

        float groundY = _geometry.GetSurfaceHeight(lkPos, supportSurface);
        bool hasProfile = _catalog.TryGetProfile(unitType, out UnitPerceptionProfile profile);
        TargetObservationPoints? points = hasProfile ? profile.TargetPoints : null;
        float targetSignature = hasProfile && float.IsFinite(profile.VisualSignature) && profile.VisualSignature > 0f
            ? profile.VisualSignature
            : 1.0f;

        for (int o = 0; o < activeObsCount; o++)
        {
            int obsIndex = _activeObserverIndices[sideIndex * _maxUnits + o];
            float range = _unitSensorRange[obsIndex] * targetSignature;
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

    public ReadOnlySpan<SmokeCloudObservation> GetVisibleSmokeClouds(int player)
    {
        int index = player - 1;
        if (index < 0 || index >= _smokeTrackers.Length)
            return [];

        return _smokeTrackers[index].ExportSmokeClouds();
    }

    public ReadOnlySpan<StructureObservation> GetKnownStructures(int player)
    {
        int index = player - 1;
        if (index < 0 || index >= _structureTrackers.Length)
            return [];

        return _structureTrackers[index].ExportStructures();
    }

    /// <summary>
    /// Copies guided missiles currently visible to <paramref name="player"/> into <paramref name="destination"/>.
    /// Friendly missiles and missiles fired by directly spotted enemy launchers are visible immediately.
    /// Missiles fired from unspotted launchers stay concealed during initial launch clearance
    /// (<see cref="HiddenLaunchConcealmentTicks"/>) and require genuine 3D visual line of sight
    /// so hidden SAM batteries do not leak their exact ground launch position through fog of war.
    /// </summary>
    public int CopyVisibleGuidedMissiles(
        WorldState world,
        int player,
        int currentTick,
        Span<GuidedMissileObservation> destination)
    {
        int total = world.GuidedMissileCount;
        if (total == 0 || destination.Length == 0)
            return 0;

        int observerTeam = world.GetPlayerTeam(player);
        int unitCount = Math.Min(_lastUnitCount, _maxUnits);
        int written = 0;

        for (int i = 0; i < total && written < destination.Length; i++)
        {
            ref readonly GuidedMissileState m = ref world.GetGuidedMissileRef(i);
            bool motorBurning = currentTick - m.LaunchedTick < m.MotorBurnTicks;

            if (!IsGuidedMissileVisibleTo(world, player, observerTeam, unitCount, in m, currentTick, motorBurning))
                continue;

            GuidedMissileFlags flags = GuidedMissileFlags.None;
            if (motorBurning)
                flags |= GuidedMissileFlags.MotorBurning;
            if (m.Decoyed)
                flags |= GuidedMissileFlags.Decoyed;

            destination[written++] = new GuidedMissileObservation(
                m.MissileId,
                m.PresentationId,
                m.Position,
                m.Velocity,
                flags);
        }

        return written;
    }

    private bool IsGuidedMissileVisibleTo(
        WorldState world,
        int player,
        int observerTeam,
        int unitCount,
        in GuidedMissileState missile,
        int currentTick,
        bool motorBurning)
    {
        if (!world.AreHostile(player, missile.Owner))
            return true;

        if (missile.ShooterId.Value != 0 && IsEntityObservedBy(player, missile.ShooterId))
            return true;

        // Unspotted launcher: conceal initial launch segment so the contrail cannot trace
        // all the way back to the hidden launcher's exact ground coordinates.
        if (currentTick - missile.LaunchedTick < HiddenLaunchConcealmentTicks)
            return false;

        float visualSignature = motorBurning ? MissileMotorVisualSignature : MissileCoastingVisualSignature;
        Vector2 missilePos2D = new(missile.Position.X, missile.Position.Z);

        for (int u = 0; u < unitCount; u++)
        {
            if (!_unitHasSensor[u] || _unitTeams[u] != observerTeam)
                continue;

            float effectiveRange = _unitSensorRange[u] * visualSignature;
            if (Vector2.DistanceSquared(_unitPositions[u], missilePos2D) > effectiveRange * effectiveRange)
                continue;

            if (_geometry.IsLineOfSightClear(_unitEyePos[u], missile.Position, _losContext, out _))
                return true;
        }

        return false;
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
            _smokeTrackers[index].Clear();
            _structureTrackers[index].Clear();
        }
    }
}
