using System.Numerics;
using System.Runtime.CompilerServices;

namespace DeadGround.Engine;

/// <summary>
/// Server-side authoritative hierarchical navigation and deterministic path planning system.
/// Combines high-level Region-Portal Graph planning with fine local window A* search.
/// Strictly allocation-free during simulation ticks with bounded per-tick work across all expensive stages.
/// </summary>
public sealed class NavigationSystem : ISimulationSystem
{
    private const float GeometryTolerance = 1e-4f;
    private const int MaxGlobalPlanPortals = 1024;
    private const int MaxLocalWindowCells = 262_144;

    private readonly NavigationGrid _grid;
    private readonly IUnitMovementCatalog _catalog;
    private readonly NavigationTuning _tuning;

    // Fixed neighbor offsets: 4 cardinal followed by 4 diagonal
    private static readonly (int dCol, int dRow, bool isDiagonal, int c1Col, int c1Row, int c2Col, int c2Row)[] s_neighbors =
    [
        ( 0,  1, false,  0, 0,  0, 0), // North
        ( 1,  0, false,  0, 0,  0, 0), // East
        ( 0, -1, false,  0, 0,  0, 0), // South
        (-1,  0, false,  0, 0,  0, 0), // West
        ( 1,  1, true,   1, 0,  0, 1), // North-East
        ( 1, -1, true,   1, 0,  0,-1), // South-East
        (-1, -1, true,  -1, 0,  0,-1), // South-West
        (-1,  1, true,  -1, 0,  0, 1), // North-West
    ];

    // Request queue (ring buffer)
    private readonly PathRequest[] _requestQueue;
    private readonly PathRequest[] _tempQueue;
    private int _queueHead;
    private int _queueTail;
    private int _queueCount;

    // Active in-flight search state machine
    private bool _hasActiveSearch;
    private PathRequest _activeRequest;
    private NavigationSearchStage _activeStage;
    private float _activeRadius;
    private uint _activeSurface;
    private int _activeTotalExpansions;

    // Global planning state
    private readonly Vector2[] _activeGlobalPortals;
    private int _activeGlobalPortalCount;
    private int _currentTick;

    // Local window definition
    private float _localWinMinX;
    private float _localWinMinZ;
    private int _localWinCols;
    private int _localWinRows;
    private int _localWinTotalCells;
    private readonly float[] _localClearance;
    private int _areaPrepCurrentRow;
    private int _areaPrepCurrentCol;

    // Local A* preallocated search buffers
    private readonly int _virtualGoalCell;
    private readonly int[] _heap;
    private int _heapCount;
    private readonly int[] _heapPosition;
    private readonly uint[] _heapGen;
    private readonly float[] _gScore;
    private readonly int[] _cameFrom;
    private readonly uint[] _searchGen;
    private readonly uint[] _closedGen;
    private readonly uint[] _goalAnchorGen;
    private readonly uint[] _cellSurface;
    private uint _searchGeneration;

    // Path reconstruction and smoothing buffers
    private readonly Vector2[] _rawPathBuffer;
    private readonly uint[] _rawPathSurfaceBuffer;
    private int _rawPathCount;
    private readonly Vector2[] _smoothedPathBuffer;
    private int _smoothingAnchorIdx;
    private int _smoothingCandidateIdx;
    private int _smoothedCount;
    private Vector2 _smoothingCurrentAnchor;
    private uint _smoothingCurrentAnchorSurface;

    // Bounded live unit storage slots and lookup table
    private readonly int _maxUnits;
    private readonly int[] _freeSlots;
    private int _freeSlotCount;
    private readonly EntityId[] _slotUnitIds;
    private readonly uint[] _idTableKeys;
    private readonly int[] _idTableSlots;
    private readonly int _idTableMask;
    private uint _lastWorldRemovalCount;

    // Preallocated per-slot route storage and chunk chaining
    private readonly Vector2[] _routeWaypoints;
    private readonly int[] _routeCounts;
    private readonly int[] _routeIndices;
    private readonly Vector2[] _unitFinalTargets;
    private readonly bool[] _unitHasFinalFacing;
    private readonly float[] _unitFinalFacings;
    private readonly PathRequestStatus[] _unitStatuses;
    private readonly uint[] _unitOrderGenerations;
    private readonly uint[] _unitOrderSequences;
    private readonly bool[] _unitHasActiveRoutes;
    private readonly bool[] _unitChunkPending;
    private readonly int[] _unitChunkWaitTicks;
    private uint _nextOrderGeneration;

    // Next chunk staging buffers per unit slot
    private readonly Vector2[] _slotNextChunkWaypoints;
    private readonly int[] _slotNextChunkCounts;

    // Global portal plans stored per unit slot
    private readonly Vector2[] _slotGlobalPortals;
    private readonly int[] _slotGlobalPortalCounts;
    private readonly int[] _slotGlobalPortalIndices;

    // Static-graph corridor reuse. Fine paths and exact destinations remain per unit.
    private readonly record struct GlobalPlanKey(
        int StartSector,
        int StartComponent,
        int TargetSector,
        int TargetComponent,
        float Radius,
        TraversabilityCapabilities Capabilities = TraversabilityCapabilities.DefaultVehicle)
    {
        public bool Matches(GlobalPlanKey other) =>
            StartSector == other.StartSector &&
            StartComponent == other.StartComponent &&
            TargetSector == other.TargetSector &&
            TargetComponent == other.TargetComponent &&
            Radius == other.Radius &&
            Capabilities == other.Capabilities;
    }
    private readonly GlobalPlanKey[] _cachedPlanKeys;
    private readonly int[] _cachedPlanCounts;
    private readonly Vector2[] _cachedPlanPortals;
    private int _nextCachedPlan;
    private GlobalPlanKey _activePlanKey;
    public int GlobalPlanCacheHits { get; private set; }
    public int GlobalPlanCacheMisses { get; private set; }

    public NavigationGrid Grid => _grid;
    public NavigationTuning Tuning => _tuning;
    public int PendingRequests => _queueCount + (_hasActiveSearch ? 1 : 0);
    public NavigationSearchStage ActiveStage => _activeStage;

    // Metrics and counters
    public int NodesExpandedThisTick => GlobalExpansionsThisTick + LocalExpansionsThisTick;
    public int TotalNodesExpanded => TotalGlobalExpansions + TotalLocalExpansions;

    public int GlobalExpansionsThisTick { get; private set; }
    public int TotalGlobalExpansions { get; private set; }

    public int LocalExpansionsThisTick { get; private set; }
    public int TotalLocalExpansions { get; private set; }

    public int SmoothingRaycastsThisTick { get; private set; }
    public int TotalSmoothingRaycasts { get; private set; }

    public int AreaPrepCellsThisTick { get; private set; }
    public int TotalAreaPrepCells { get; private set; }

    public int CompletedSearches { get; private set; }
    public int FailedSearches { get; private set; }
    public int CapacityExceeded { get; private set; }

    public int ChunkRequestsCompleted { get; private set; }
    public int ChunkWaitTicks { get; private set; }

    public long EstimatedMemoryBytes
    {
        get
        {
            long bytes = _grid.EstimatedMemoryBytes;
            bytes += (long)(_requestQueue.Length + _tempQueue.Length) * Unsafe.SizeOf<PathRequest>();
            bytes += (long)(_activeGlobalPortals.Length + _routeWaypoints.Length + _slotNextChunkWaypoints.Length +
                            _slotGlobalPortals.Length + _unitFinalTargets.Length + _rawPathBuffer.Length + _smoothedPathBuffer.Length) * 8;
            bytes += (long)_localClearance.Length * 4;
            bytes += (long)(_heap.Length + _heapPosition.Length + _heapGen.Length + _gScore.Length + _cameFrom.Length +
                            _searchGen.Length + _closedGen.Length + _goalAnchorGen.Length + _cellSurface.Length + _rawPathSurfaceBuffer.Length) * 4;
            bytes += (long)(_freeSlots.Length + _idTableKeys.Length + _idTableSlots.Length + _routeCounts.Length +
                            _routeIndices.Length + _unitOrderGenerations.Length + _unitChunkWaitTicks.Length +
                            _slotNextChunkCounts.Length + _slotGlobalPortalCounts.Length + _slotGlobalPortalIndices.Length) * 4;
            bytes += (long)_slotUnitIds.Length * Unsafe.SizeOf<EntityId>();
            bytes += (long)_unitStatuses.Length * Unsafe.SizeOf<PathRequestStatus>();
            bytes += _unitHasActiveRoutes.Length + _unitChunkPending.Length;
            bytes += (long)_cachedPlanKeys.Length * Unsafe.SizeOf<GlobalPlanKey>() +
                (long)_cachedPlanCounts.Length * 4 + (long)_cachedPlanPortals.Length * 8;
            return bytes;
        }
    }

    public readonly record struct PathRequest(
        int Player,
        EntityId Unit,
        Vector2 Start,
        Vector2 Target,
        float Radius,
        uint OrderGeneration,
        bool IsChunkContinuation = false,
        TraversabilityCapabilities Capabilities = TraversabilityCapabilities.DefaultVehicle);

    public NavigationSystem(
        NavigationGrid grid,
        IUnitMovementCatalog catalog,
        NavigationTuning? tuning = null,
        int maxUnits = 4096)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(catalog);

        _grid = grid;
        _catalog = catalog;
        _tuning = tuning ?? new NavigationTuning();
        _maxUnits = Math.Max(1024, maxUnits);
        if (_tuning.GlobalPlanCacheEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(tuning), "Global plan cache entries cannot be negative.");
        _cachedPlanKeys = new GlobalPlanKey[_tuning.GlobalPlanCacheEntries];
        _cachedPlanCounts = new int[_tuning.GlobalPlanCacheEntries];
        _cachedPlanPortals = new Vector2[checked(_tuning.GlobalPlanCacheEntries * MaxGlobalPlanPortals)];

        _requestQueue = new PathRequest[_tuning.MaxPendingRequests];
        _tempQueue = new PathRequest[_tuning.MaxPendingRequests];

        int localCellCapacity = _grid.IsFlatGrid
            ? _grid.TotalCells
            : Math.Min(MaxLocalWindowCells, Math.Max(4096, (int)MathF.Ceiling((_tuning.MaxLocalWindowSize / _grid.CellSize) * (_tuning.MaxLocalWindowSize / _grid.CellSize))));

        int nodeCount = localCellCapacity + 1;
        _virtualGoalCell = localCellCapacity;

        _localClearance = new float[localCellCapacity];
        _activeGlobalPortals = new Vector2[MaxGlobalPlanPortals];

        _heap = new int[nodeCount];
        _heapPosition = new int[nodeCount];
        _heapGen = new uint[nodeCount];
        _gScore = new float[nodeCount];
        _cameFrom = new int[nodeCount];
        _searchGen = new uint[nodeCount];
        _closedGen = new uint[nodeCount];
        _goalAnchorGen = new uint[nodeCount];
        _cellSurface = new uint[nodeCount];

        _rawPathBuffer = new Vector2[Math.Max(1024, localCellCapacity + 4)];
        _rawPathSurfaceBuffer = new uint[_rawPathBuffer.Length];
        _smoothedPathBuffer = new Vector2[_tuning.MaxWaypointsPerRoute];

        // Slot management structures
        _freeSlots = new int[_maxUnits];
        _freeSlotCount = _maxUnits;
        for (int i = 0; i < _maxUnits; i++)
        {
            _freeSlots[i] = _maxUnits - 1 - i;
        }
        _slotUnitIds = new EntityId[_maxUnits];

        int tableSize = 1;
        while (tableSize < _maxUnits * 2) tableSize <<= 1;
        _idTableKeys = new uint[tableSize];
        _idTableSlots = new int[tableSize];
        _idTableMask = tableSize - 1;

        _routeWaypoints = new Vector2[_maxUnits * _tuning.MaxWaypointsPerRoute];
        _routeCounts = new int[_maxUnits];
        _routeIndices = new int[_maxUnits];
        _unitFinalTargets = new Vector2[_maxUnits];
        _unitHasFinalFacing = new bool[_maxUnits];
        _unitFinalFacings = new float[_maxUnits];
        _unitStatuses = new PathRequestStatus[_maxUnits];
        _unitOrderGenerations = new uint[_maxUnits];
        _unitOrderSequences = new uint[_maxUnits];
        _unitHasActiveRoutes = new bool[_maxUnits];
        _unitChunkPending = new bool[_maxUnits];
        _unitChunkWaitTicks = new int[_maxUnits];

        _slotNextChunkWaypoints = new Vector2[_maxUnits * _tuning.MaxWaypointsPerRoute];
        _slotNextChunkCounts = new int[_maxUnits];

        _slotGlobalPortals = new Vector2[_maxUnits * MaxGlobalPlanPortals];
        _slotGlobalPortalCounts = new int[_maxUnits];
        _slotGlobalPortalIndices = new int[_maxUnits];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HashId(uint id)
    {
        id ^= id >> 16;
        id *= 0x85ebca6b;
        id ^= id >> 13;
        id *= 0xc2b2ae35;
        id ^= id >> 16;
        return id;
    }

    private bool TryGetSlot(EntityId id, out int slot)
    {
        uint key = id.Value;
        if (key == 0)
        {
            slot = -1;
            return false;
        }

        uint mask = (uint)_idTableMask;
        uint idx = HashId(key) & mask;
        while (true)
        {
            uint tableKey = _idTableKeys[idx];
            if (tableKey == key)
            {
                slot = _idTableSlots[idx];
                return true;
            }
            if (tableKey == 0)
            {
                slot = -1;
                return false;
            }
            idx = (idx + 1) & mask;
        }
    }

    private bool GetOrCreateSlot(EntityId id, out int slot)
    {
        uint key = id.Value;
        if (key == 0)
        {
            slot = -1;
            return false;
        }

        uint mask = (uint)_idTableMask;
        uint idx = HashId(key) & mask;
        while (true)
        {
            uint tableKey = _idTableKeys[idx];
            if (tableKey == key)
            {
                slot = _idTableSlots[idx];
                return true;
            }
            if (tableKey == 0)
            {
                if (_freeSlotCount == 0)
                {
                    slot = -1;
                    return false;
                }
                slot = _freeSlots[--_freeSlotCount];
                _slotUnitIds[slot] = id;
                _idTableKeys[idx] = key;
                _idTableSlots[idx] = slot;
                _unitStatuses[slot] = PathRequestStatus.None;
                _unitHasActiveRoutes[slot] = false;
                _routeCounts[slot] = 0;
                _routeIndices[slot] = 0;
                _unitOrderGenerations[slot] = 0;
                _unitOrderSequences[slot] = 0;
                _unitFinalTargets[slot] = Vector2.Zero;
                _unitHasFinalFacing[slot] = false;
                _unitFinalFacings[slot] = 0f;
                _unitChunkPending[slot] = false;
                _unitChunkWaitTicks[slot] = 0;
                _slotNextChunkCounts[slot] = 0;
                _slotGlobalPortalCounts[slot] = 0;
                _slotGlobalPortalIndices[slot] = 0;
                return true;
            }
            idx = (idx + 1) & mask;
        }
    }

    private void ReleaseSlot(int slot)
    {
        if ((uint)slot >= (uint)_maxUnits) return;
        EntityId id = _slotUnitIds[slot];
        uint key = id.Value;
        if (key == 0) return;

        uint mask = (uint)_idTableMask;
        uint idx = HashId(key) & mask;
        while (_idTableKeys[idx] != key)
        {
            if (_idTableKeys[idx] == 0) return;
            idx = (idx + 1) & mask;
        }

        uint curr = idx;
        while (true)
        {
            _idTableKeys[curr] = 0;
            _idTableSlots[curr] = 0;

            uint next = (curr + 1) & mask;
            while (_idTableKeys[next] != 0)
            {
                uint r = HashId(_idTableKeys[next]) & mask;
                bool cyclicallyBetween = (curr <= next) ? (r <= curr || r > next) : (r <= curr && r > next);
                if (cyclicallyBetween)
                {
                    _idTableKeys[curr] = _idTableKeys[next];
                    _idTableSlots[curr] = _idTableSlots[next];
                    curr = next;
                }
                next = (next + 1) & mask;
            }

            if (curr == idx)
            {
                _idTableKeys[curr] = 0;
                _idTableSlots[curr] = 0;
                break;
            }
            idx = curr;
        }

        _slotUnitIds[slot] = default;
        _unitStatuses[slot] = PathRequestStatus.None;
        _unitHasActiveRoutes[slot] = false;
        _routeCounts[slot] = 0;
        _routeIndices[slot] = 0;
        _unitOrderGenerations[slot] = 0;
        _unitOrderSequences[slot] = 0;
        _unitFinalTargets[slot] = Vector2.Zero;
        _unitHasFinalFacing[slot] = false;
        _unitFinalFacings[slot] = 0f;
        _unitChunkPending[slot] = false;
        _unitChunkWaitTicks[slot] = 0;
        _slotNextChunkCounts[slot] = 0;
        _slotGlobalPortalCounts[slot] = 0;
        _slotGlobalPortalIndices[slot] = 0;
        _freeSlots[_freeSlotCount++] = slot;
    }

    public void SweepRemovedUnits(WorldState world)
    {
        int allocated = _maxUnits - _freeSlotCount;
        if (allocated == 0)
        {
            _lastWorldRemovalCount = world.RemovalCount;
            return;
        }

        if (world.RemovalCount == _lastWorldRemovalCount && allocated <= world.Count)
        {
            return;
        }

        _lastWorldRemovalCount = world.RemovalCount;

        for (int s = 0; s < _maxUnits; s++)
        {
            EntityId id = _slotUnitIds[s];
            if (id.Value != 0 && !world.TryGetUnit(id, out _))
            {
                if (_hasActiveSearch && _activeRequest.Unit == id)
                {
                    CancelActiveSearch();
                }
                ReleaseSlot(s);
            }
        }
    }

    public void ClearUnit(EntityId unit)
    {
        if (TryGetSlot(unit, out int slot))
        {
            if (_hasActiveSearch && _activeRequest.Unit == unit)
            {
                CancelActiveSearch();
            }
            ReleaseSlot(slot);
        }
    }

    private void CancelActiveSearch()
    {
        _hasActiveSearch = false;
        _activeStage = NavigationSearchStage.None;
        _activeSurface = 0;
        _grid.GetHierarchicalGraph(_activeRadius > 0f ? _activeRadius : MovementClassExtensions.TrackedRadius, _activeRequest.Capabilities).CancelGlobalSearch();
    }

    public PathRequestStatus GetUnitStatus(EntityId unit)
    {
        if (TryGetSlot(unit, out int slot))
            return _unitStatuses[slot];
        return PathRequestStatus.None;
    }

    public uint GetUnitOrderSequence(EntityId unit)
    {
        if (TryGetSlot(unit, out int slot))
            return _unitOrderSequences[slot];
        return 0;
    }

    public bool HasActiveRoute(EntityId unit) => TryGetSlot(unit, out int slot) && _unitHasActiveRoutes[slot];

    public bool IsUnitManeuvering(EntityId unit) => TryGetSlot(unit, out int slot) && _unitHasActiveRoutes[slot];

    public bool EnqueueCommand(int player, MoveCommand command, WorldState? world = null)
    {
        if (!GetOrCreateSlot(command.Unit, out int slot))
        {
            CapacityExceeded++;
            return false;
        }

        if (_queueCount == _requestQueue.Length)
        {
            CapacityExceeded++;
            _unitStatuses[slot] = PathRequestStatus.CapacityExceeded;
            return false;
        }

        // Validate destination if world is provided
        TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle;
        float radius = 1.0f;
        if (world is not null && world.TryGetUnit(command.Unit, out UnitState unit))
        {
            if (_catalog.TryGetProfile(unit.Definition, out UnitMovementProfile profile))
            {
                radius = profile.Radius;
                capabilities = profile.Capabilities;
            }

            if (!_grid.IsPointClear(command.Target, radius, capabilities, unit.SupportSurface))
            {
                if (!TrySnapDestination(unit.Position, command.Target, radius, capabilities, unit.SupportSurface, out _))
                {
                    _unitOrderGenerations[slot] = checked(++_nextOrderGeneration);
                    _unitStatuses[slot] = PathRequestStatus.InvalidDestination;
                    FailedSearches++;
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    _unitChunkPending[slot] = false;
                    _slotNextChunkCounts[slot] = 0;
                    world.UpdateUnitNavigation(command.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
                    return false;
                }
            }
        }

        _unitOrderSequences[slot] = command.Sequence;

        // Cancel previous active search if it belonged to this unit
        if (_hasActiveSearch && _activeRequest.Unit == command.Unit)
        {
            CancelActiveSearch();
        }

        uint orderGen = checked(++_nextOrderGeneration);
        _unitOrderGenerations[slot] = orderGen;
        _unitFinalTargets[slot] = command.Target;
        if (command.FinalFacing.HasValue)
        {
            _unitHasFinalFacing[slot] = true;
            _unitFinalFacings[slot] = CombatSystem.NormalizeYaw(command.FinalFacing.Value);
        }
        else
        {
            _unitHasFinalFacing[slot] = false;
            _unitFinalFacings[slot] = 0f;
        }

        _unitStatuses[slot] = PathRequestStatus.Pending;
        _unitChunkPending[slot] = false;
        _unitChunkWaitTicks[slot] = 0;
        _slotNextChunkCounts[slot] = 0;
        _slotGlobalPortalCounts[slot] = 0;
        _slotGlobalPortalIndices[slot] = 0;

        Vector2 start = command.Target;
        _requestQueue[_queueTail] = new PathRequest(player, command.Unit, start, command.Target, radius, orderGen, false, capabilities);
        _queueTail = (_queueTail + 1) % _requestQueue.Length;
        _queueCount++;
        return true;
    }

    public void ResetPlayer(int player)
    {
        if (_hasActiveSearch && _activeRequest.Player == player)
        {
            EntityId unitId = _activeRequest.Unit;
            CancelActiveSearch();
            if (TryGetSlot(unitId, out int s))
            {
                _unitStatuses[s] = PathRequestStatus.None;
                _unitHasActiveRoutes[s] = false;
                _routeCounts[s] = 0;
                _routeIndices[s] = 0;
                _unitFinalTargets[s] = Vector2.Zero;
                _unitHasFinalFacing[s] = false;
                _unitFinalFacings[s] = 0f;
                _unitChunkPending[s] = false;
                _slotNextChunkCounts[s] = 0;
            }
        }

        if (_queueCount > 0)
        {
            int remaining = 0;
            while (_queueCount > 0)
            {
                PathRequest req = _requestQueue[_queueHead];
                _queueHead = (_queueHead + 1) % _requestQueue.Length;
                _queueCount--;

                if (req.Player == player)
                {
                    if (TryGetSlot(req.Unit, out int s) && req.OrderGeneration == _unitOrderGenerations[s])
                    {
                        _unitStatuses[s] = PathRequestStatus.None;
                        _unitHasActiveRoutes[s] = false;
                        _routeCounts[s] = 0;
                        _routeIndices[s] = 0;
                        _unitFinalTargets[s] = Vector2.Zero;
                        _unitHasFinalFacing[s] = false;
                        _unitFinalFacings[s] = 0f;
                        _unitChunkPending[s] = false;
                        _slotNextChunkCounts[s] = 0;
                    }
                    continue;
                }

                _tempQueue[remaining++] = req;
            }

            for (int i = 0; i < remaining; i++)
            {
                _requestQueue[i] = _tempQueue[i];
            }
            _queueHead = 0;
            _queueTail = remaining % _requestQueue.Length;
            _queueCount = remaining;
        }
    }

    public void Update(WorldState world, int tick)
    {
        ArgumentNullException.ThrowIfNull(world);

        _currentTick = tick;
        SweepRemovedUnits(world);

        GlobalExpansionsThisTick = 0;
        LocalExpansionsThisTick = 0;
        SmoothingRaycastsThisTick = 0;
        AreaPrepCellsThisTick = 0;

        // 1. Process pathfinding requests across hierarchical stages
        ProcessPlanning(world);

        // 2. Follow established routes and advance unit positions
        ProcessMovement(world);
    }

    private void ProcessPlanning(WorldState world)
    {
        ProcessImmediateOrders(world);

        while (NodesExpandedThisTick < _tuning.MaxNodeExpansionsPerTick)
        {
            if (!_hasActiveSearch)
            {
                if (_queueCount == 0) break;

                PathRequest req = _requestQueue[_queueHead];
                _queueHead = (_queueHead + 1) % _requestQueue.Length;
                _queueCount--;

                if (!StartActivePlanning(world, req))
                {
                    continue;
                }
            }

            bool stageExhausted = AdvanceActivePlanning(world);
            if (stageExhausted)
            {
                break;
            }
        }
    }

    private void ProcessImmediateOrders(WorldState world)
    {
        if (_queueCount == 0) return;

        int remainingCount = 0;
        while (_queueCount > 0)
        {
            PathRequest req = _requestQueue[_queueHead];
            _queueHead = (_queueHead + 1) % _requestQueue.Length;
            _queueCount--;

            if (!world.TryGetUnit(req.Unit, out UnitState unit))
            {
                if (TryGetSlot(req.Unit, out int s)) ReleaseSlot(s);
                continue;
            }

            if (!TryGetSlot(req.Unit, out int slot))
            {
                continue;
            }

            if (req.OrderGeneration != _unitOrderGenerations[slot])
            {
                continue;
            }

            float radius = 1.0f;
            if (_catalog.TryGetProfile(unit.Definition, out UnitMovementProfile profile))
            {
                radius = profile.Radius;
            }

            Vector2 start = req.IsChunkContinuation ? req.Start : unit.Position;
            Vector2 target = req.Target;

            if (!_grid.IsPointClear(target, radius, req.Capabilities))
            {
                if (!TrySnapDestination(start, target, radius, req.Capabilities, out Vector2 snapped))
                {
                    _unitStatuses[slot] = PathRequestStatus.InvalidDestination;
                    FailedSearches++;
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    world.UpdateUnitNavigation(req.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
                    continue;
                }
                target = snapped;
                req = req with { Target = target };
            }

            if (Vector2.DistanceSquared(start, target) <= 1e-4f)
            {
                if (req.IsChunkContinuation)
                {
                    if (_slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
                    {
                        _slotGlobalPortalIndices[slot]++;
                    }
                    _unitChunkPending[slot] = false;
                    continue;
                }

                _unitStatuses[slot] = PathRequestStatus.PathFound;
                _unitFinalTargets[slot] = target;
                CompletedSearches++;
                _routeCounts[slot] = 0;
                _routeIndices[slot] = 0;
                if (_unitHasFinalFacing[slot])
                {
                    _unitHasActiveRoutes[slot] = true;
                    world.UpdateUnitNavigation(req.Unit, unit.Position, target, true, supportSurface: unit.SupportSurface);
                }
                else
                {
                    _unitHasActiveRoutes[slot] = false;
                    world.UpdateUnitNavigation(req.Unit, unit.Position, target, false, supportSurface: unit.SupportSurface);
                }
                continue;
            }

            if (_grid.IsSegmentClear(start, target, radius, req.Capabilities, unit.SupportSurface))
            {
                if (req.IsChunkContinuation)
                {
                    int offset = slot * _tuning.MaxWaypointsPerRoute;
                    if (_unitHasActiveRoutes[slot] && _routeIndices[slot] >= _routeCounts[slot])
                    {
                        _routeWaypoints[offset] = target;
                        _routeCounts[slot] = 1;
                        _routeIndices[slot] = 0;
                        _unitChunkWaitTicks[slot] = 0;
                        world.UpdateUnitNavigation(req.Unit, unit.Position, _unitFinalTargets[slot], true, supportSurface: unit.SupportSurface);
                    }
                    else
                    {
                        _slotNextChunkWaypoints[offset] = target;
                        _slotNextChunkCounts[slot] = 1;
                    }
                    _unitChunkPending[slot] = false;
                    _unitStatuses[slot] = PathRequestStatus.PathFound;
                    ChunkRequestsCompleted++;
                    if (_slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
                    {
                        _slotGlobalPortalIndices[slot]++;
                    }
                }
                else
                {
                    SetUnitSingleWaypointRoute(slot, target);
                    _unitFinalTargets[slot] = target;
                    _unitStatuses[slot] = PathRequestStatus.PathFound;
                    _unitChunkPending[slot] = false;
                    CompletedSearches++;
                    world.UpdateUnitNavigation(req.Unit, unit.Position, target, true, supportSurface: unit.SupportSurface);
                }
                continue;
            }

            _tempQueue[remainingCount++] = req;
        }

        for (int i = 0; i < remainingCount; i++)
        {
            _requestQueue[i] = _tempQueue[i];
        }
        _queueHead = 0;
        _queueTail = remainingCount % _requestQueue.Length;
        _queueCount = remainingCount;
    }

    private bool StartActivePlanning(WorldState world, PathRequest req)
    {
        if (!world.TryGetUnit(req.Unit, out UnitState unit))
        {
            if (TryGetSlot(req.Unit, out int s)) ReleaseSlot(s);
            return false;
        }

        if (!TryGetSlot(req.Unit, out int slot))
        {
            return false;
        }

        if (req.OrderGeneration != _unitOrderGenerations[slot])
        {
            return false;
        }

        float radius = 1.0f;
        if (_catalog.TryGetProfile(unit.Definition, out UnitMovementProfile profile))
        {
            radius = profile.Radius;
        }

        Vector2 start = req.IsChunkContinuation ? req.Start : unit.Position;
        Vector2 target = req.Target;

        if (!_grid.IsPointClear(target, radius, req.Capabilities))
        {
            if (!TrySnapDestination(start, target, radius, req.Capabilities, out Vector2 snapped))
            {
                _unitStatuses[slot] = PathRequestStatus.InvalidDestination;
                FailedSearches++;
                _unitHasActiveRoutes[slot] = false;
                _routeCounts[slot] = 0;
                _routeIndices[slot] = 0;
                world.UpdateUnitNavigation(req.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
                return false;
            }
            target = snapped;
            req = req with { Target = target };
        }

        if (Vector2.DistanceSquared(start, target) <= 1e-4f)
        {
            if (req.IsChunkContinuation)
            {
                if (_slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
                {
                    _slotGlobalPortalIndices[slot]++;
                }
                _unitChunkPending[slot] = false;
                return false;
            }

            _unitStatuses[slot] = PathRequestStatus.PathFound;
            _unitFinalTargets[slot] = target;
            CompletedSearches++;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            if (_unitHasFinalFacing[slot])
            {
                _unitHasActiveRoutes[slot] = true;
                world.UpdateUnitNavigation(req.Unit, unit.Position, target, true, supportSurface: unit.SupportSurface);
            }
            else
            {
                _unitHasActiveRoutes[slot] = false;
                world.UpdateUnitNavigation(req.Unit, unit.Position, target, false, supportSurface: unit.SupportSurface);
            }
            return false;
        }

        if (_grid.IsSegmentClear(start, target, radius, req.Capabilities, unit.SupportSurface))
        {
            if (req.IsChunkContinuation)
            {
                int offset = slot * _tuning.MaxWaypointsPerRoute;
                if (_unitHasActiveRoutes[slot] && _routeIndices[slot] >= _routeCounts[slot])
                {
                    _routeWaypoints[offset] = target;
                    _routeCounts[slot] = 1;
                    _routeIndices[slot] = 0;
                    _unitChunkWaitTicks[slot] = 0;
                    world.UpdateUnitNavigation(req.Unit, unit.Position, _unitFinalTargets[slot], true, supportSurface: unit.SupportSurface);
                }
                else
                {
                    _slotNextChunkWaypoints[offset] = target;
                    _slotNextChunkCounts[slot] = 1;
                }
                _unitChunkPending[slot] = false;
                _unitStatuses[slot] = PathRequestStatus.PathFound;
                ChunkRequestsCompleted++;
                if (_slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
                {
                    _slotGlobalPortalIndices[slot]++;
                }
            }
            else
            {
                SetUnitSingleWaypointRoute(slot, target);
                _unitFinalTargets[slot] = target;
                _unitStatuses[slot] = PathRequestStatus.PathFound;
                _unitChunkPending[slot] = false;
                CompletedSearches++;
                world.UpdateUnitNavigation(req.Unit, unit.Position, target, true, supportSurface: unit.SupportSurface);
            }
            return false;
        }

        _activeRequest = req with { Start = start, Target = target, Radius = radius };
        _activeRadius = radius;
        _activeSurface = unit.SupportSurface;
        _activeTotalExpansions = 0;
        _hasActiveSearch = true;
        if (!req.IsChunkContinuation)
        {
            _unitStatuses[slot] = PathRequestStatus.Searching;
        }

        if (_grid.IsFlatGrid)
        {
            // On bounded flat prototype grids, use full grid directly without intermediate portal approximation
            _localWinMinX = 0f;
            _localWinMinZ = 0f;
            _localWinCols = _grid.Columns;
            _localWinRows = _grid.Rows;
            _localWinTotalCells = _grid.TotalCells;
            _areaPrepCurrentRow = _localWinRows;
            InitLocalSearch();
            _activeStage = NavigationSearchStage.LocalPlanning;
            return true;
        }

        if (req.IsChunkContinuation)
        {
            SetupLocalWindow(start, target);
            _activeStage = NavigationSearchStage.AreaPreparation;
            return true;
        }

        // On large hierarchical maps, start high-level Region-Portal Graph planning
        HierarchicalNavGraph graph = _grid.GetHierarchicalGraph(radius, req.Capabilities);
        int startSector = graph.GetSectorIndex(start);
        int targetSector = graph.GetSectorIndex(target);
        int startComp = graph.GetComponentId(start, radius);
        int targetComp = graph.GetComponentId(target, radius);

        if (!graph.AreComponentsConnected(startSector, startComp, targetSector, targetComp))
        {
            _unitStatuses[slot] = PathRequestStatus.NoPath;
            FailedSearches++;
            _unitHasActiveRoutes[slot] = false;
            _unitChunkPending[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            _slotGlobalPortalCounts[slot] = 0;
            world.UpdateUnitNavigation(req.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
            CancelActiveSearch();
            return false;
        }

        if (startSector == targetSector && startComp == targetComp && startComp > 0)
        {
            _activeGlobalPortalCount = 0;
            SetupLocalWindow(start, target);
            _activeStage = NavigationSearchStage.AreaPreparation;
            return true;
        }

        _activePlanKey = new GlobalPlanKey(startSector, startComp, targetSector, targetComp, radius, req.Capabilities);
        if (TryLoadGlobalPlan())
        {
            InstallGlobalPlan(slot);
            return true;
        }
        graph.StartGlobalSearch(start, target, radius);
        _activeStage = NavigationSearchStage.GlobalPlanning;
        return true;
    }

    private bool AdvanceActivePlanning(WorldState world)
    {
        EntityId unitId = _activeRequest.Unit;
        if (!world.TryGetUnit(unitId, out UnitState unit))
        {
            CancelActiveSearch();
            if (TryGetSlot(unitId, out int s)) ReleaseSlot(s);
            return false;
        }

        if (!TryGetSlot(unitId, out int slot))
        {
            CancelActiveSearch();
            return false;
        }

        switch (_activeStage)
        {
            case NavigationSearchStage.GlobalPlanning:
                return AdvanceGlobalPlanning(world, slot, unit);

            case NavigationSearchStage.AreaPreparation:
                return AdvanceAreaPreparation(world, slot, unit);

            case NavigationSearchStage.LocalPlanning:
                return AdvanceLocalPlanning(world, slot, unit);

            case NavigationSearchStage.PathSmoothing:
                return AdvancePathSmoothing(world, slot, unit);

            default:
                CancelActiveSearch();
                return false;
        }
    }

    private bool AdvanceGlobalPlanning(WorldState world, int slot, UnitState unit)
    {
        int remainingGlobalBudget = Math.Max(0, _tuning.MaxGlobalExpansionsPerTick - GlobalExpansionsThisTick);
        if (remainingGlobalBudget == 0) return true;

        HierarchicalNavGraph graph = _grid.GetHierarchicalGraph(_activeRadius, _activeRequest.Capabilities);
        PathRequestStatus status = graph.ResumeGlobalSearch(
            remainingGlobalBudget,
            _tuning.MaxTotalExpansionsPerRequest,
            out int used,
            _activeGlobalPortals,
            out _activeGlobalPortalCount);

        GlobalExpansionsThisTick += used;
        TotalGlobalExpansions += used;
        _activeTotalExpansions += used;

        if (status == PathRequestStatus.PathFound)
        {
            CacheGlobalPlan();
            InstallGlobalPlan(slot);
            return false;
        }
        else if (status == PathRequestStatus.NoPath)
        {
            _unitStatuses[slot] = PathRequestStatus.NoPath;
            FailedSearches++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
            CancelActiveSearch();
            return false;
        }
        else if (status == PathRequestStatus.CapacityExceeded)
        {
            _unitStatuses[slot] = PathRequestStatus.CapacityExceeded;
            CapacityExceeded++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
            CancelActiveSearch();
            return false;
        }

        return true;
    }

    private bool TryLoadGlobalPlan()
    {
        for (int i = 0; i < _cachedPlanCounts.Length; i++)
        {
            int count = _cachedPlanCounts[i];
            if (count == 0 || !_cachedPlanKeys[i].Matches(_activePlanKey)) continue;
            _cachedPlanPortals.AsSpan(i * MaxGlobalPlanPortals, count).CopyTo(_activeGlobalPortals);
            _activeGlobalPortalCount = count;
            GlobalPlanCacheHits++;
            return true;
        }
        GlobalPlanCacheMisses++;
        return false;
    }

    private void CacheGlobalPlan()
    {
        if (_cachedPlanCounts.Length == 0 || _activeGlobalPortalCount == 0) return;
        int index = _nextCachedPlan;
        _nextCachedPlan = (index + 1) % _cachedPlanCounts.Length;
        _cachedPlanKeys[index] = _activePlanKey;
        _cachedPlanCounts[index] = _activeGlobalPortalCount;
        _activeGlobalPortals.AsSpan(0, _activeGlobalPortalCount).CopyTo(_cachedPlanPortals.AsSpan(index * MaxGlobalPlanPortals));
    }

    private void InstallGlobalPlan(int slot)
    {
        _activeGlobalPortals.AsSpan(0, _activeGlobalPortalCount).CopyTo(_slotGlobalPortals.AsSpan(slot * MaxGlobalPlanPortals));
        _slotGlobalPortalCounts[slot] = _activeGlobalPortalCount;
        _slotGlobalPortalIndices[slot] = 0;
        Vector2 chunkTarget = _activeGlobalPortalCount > 0 ? _activeGlobalPortals[0] : _activeRequest.Target;
        SetupLocalWindow(_activeRequest.Start, chunkTarget);
        _activeStage = NavigationSearchStage.AreaPreparation;
    }

    private void SetupLocalWindow(Vector2 start, Vector2 target)
    {
        float margin = 15f;
        _localWinMinX = MathF.Max(0f, MathF.Min(start.X, target.X) - margin);
        _localWinMinZ = MathF.Max(0f, MathF.Min(start.Y, target.Y) - margin);
        float maxX = MathF.Min(_grid.MapWidth, MathF.Max(start.X, target.X) + margin);
        float maxZ = MathF.Min(_grid.MapDepth, MathF.Max(start.Y, target.Y) + margin);

        float maxDim = _tuning.MaxLocalWindowSize;
        if (maxX - _localWinMinX > maxDim) maxX = _localWinMinX + maxDim;
        if (maxZ - _localWinMinZ > maxDim) maxZ = _localWinMinZ + maxDim;

        _localWinCols = Math.Max(1, (int)MathF.Ceiling((maxX - _localWinMinX) / _grid.CellSize));
        _localWinRows = Math.Max(1, (int)MathF.Ceiling((maxZ - _localWinMinZ) / _grid.CellSize));
        _localWinTotalCells = _localWinCols * _localWinRows;
        _areaPrepCurrentRow = 0;
        _areaPrepCurrentCol = 0;
    }

    private bool AdvanceAreaPreparation(WorldState world, int slot, UnitState unit)
    {
        if (_grid.IsFlatGrid && _grid.TerrainObstacles.Count == 0 && _grid.Bridges.Count == 0)
        {
            InitLocalSearch();
            _activeStage = NavigationSearchStage.LocalPlanning;
            return false;
        }

        while (_areaPrepCurrentRow < _localWinRows && AreaPrepCellsThisTick < _tuning.MaxAreaPrepCellsPerTick)
        {
            float cz = _localWinMinZ + (_areaPrepCurrentRow + 0.5f) * _grid.CellSize;
            int baseIdx = _areaPrepCurrentRow * _localWinCols;

            while (_areaPrepCurrentCol < _localWinCols && AreaPrepCellsThisTick < _tuning.MaxAreaPrepCellsPerTick)
            {
                float cx = _localWinMinX + (_areaPrepCurrentCol + 0.5f) * _grid.CellSize;
                _localClearance[baseIdx + _areaPrepCurrentCol] = _grid.ComputePointClearance(new Vector2(cx, cz), _activeRequest.Capabilities);
                _areaPrepCurrentCol++;
                AreaPrepCellsThisTick++;
                TotalAreaPrepCells++;
            }

            if (_areaPrepCurrentCol >= _localWinCols)
            {
                _areaPrepCurrentCol = 0;
                _areaPrepCurrentRow++;
            }
        }

        if (_areaPrepCurrentRow >= _localWinRows)
        {
            InitLocalSearch();
            _activeStage = NavigationSearchStage.LocalPlanning;
            return false;
        }

        return true;
    }

    private void InitLocalSearch()
    {
        _searchGeneration++;
        _heapCount = 0;

        Vector2 start = _activeRequest.Start;
        Vector2 target = _activeRequest.Target;
        float radius = _activeRadius;

        if (!_grid.IsFlatGrid && !_activeRequest.IsChunkContinuation && _slotGlobalPortalCounts[TryGetSlot(_activeRequest.Unit, out int s) ? s : 0] > 0)
        {
            int pIdx = _slotGlobalPortalIndices[s];
            if (pIdx < _slotGlobalPortalCounts[s])
            {
                target = _slotGlobalPortals[s * MaxGlobalPlanPortals + pIdx];
            }
        }

        _searchGen[_virtualGoalCell] = _searchGeneration;
        _gScore[_virtualGoalCell] = float.PositiveInfinity;
        _cameFrom[_virtualGoalCell] = -1;
        _heapPosition[_virtualGoalCell] = -1;

        uint targetSurface = 0;
        BridgeStructure targetBridge = default!;
        bool hasTargetBridge = false;
        if ((_activeRequest.Capabilities & TraversabilityCapabilities.Bridge) != 0 && _grid.Bridges.Count > 0)
        {
            for (int b = 0; b < _grid.Bridges.Count; b++)
            {
                BridgeStructure br = _grid.Bridges[b];
                if (br.Contains(target))
                {
                    targetSurface = (uint)br.Id.Value;
                    targetBridge = br;
                    hasTargetBridge = true;
                    break;
                }
            }
        }
        _cellSurface[_virtualGoalCell] = targetSurface;

        // 1. Goal anchor cells in local window
        (int goalCol, int goalRow) = LocalWorldToCell(target);
        int goalWindow = Math.Clamp((int)MathF.Ceiling((radius + 1.5f * _grid.CellSize) / _grid.CellSize), 2, 5);
        int goalAnchorCount = 0;

        for (int dr = -goalWindow; dr <= goalWindow; dr++)
        {
            int gr = goalRow + dr;
            if (gr < 0 || gr >= _localWinRows) continue;

            for (int dc = -goalWindow; dc <= goalWindow; dc++)
            {
                int gc = goalCol + dc;
                if (gc < 0 || gc >= _localWinCols) continue;

                if (!IsLocalCellPassable(gc, gr, radius)) continue;

                Vector2 center = LocalCellCenter(gc, gr);
                uint anchorSurface = targetSurface;
                if (targetSurface > 0)
                {
                    if (!hasTargetBridge || !targetBridge.Contains(center))
                    {
                        continue;
                    }
                }

                if (!_grid.IsSegmentClear(center, target, radius, _activeRequest.Capabilities, anchorSurface)) continue;

                int cell = gr * _localWinCols + gc;
                _goalAnchorGen[cell] = _searchGeneration;
                _cellSurface[cell] = anchorSurface;
                goalAnchorCount++;
            }
        }

        // 2. Start anchor cells in local window
        (int startCol, int startRow) = LocalWorldToCell(start);
        int startCell = startRow * _localWinCols + startCol;
        int startAnchorCount = 0;

        uint startSurface = _activeSurface;
        BridgeStructure startBridge = default!;
        bool hasStartBridge = false;
        if (startSurface > 0 && _grid.Bridges.Count > 0)
        {
            hasStartBridge = _grid.TryGetBridge(new StructureId((int)startSurface), out startBridge);
        }

        bool startCellValid = IsInLocalBounds(startCol, startRow) &&
            IsLocalCellPassable(startCol, startRow, radius) &&
            (startSurface == 0 || (hasStartBridge && startBridge.Contains(LocalCellCenter(startCol, startRow)))) &&
            _grid.IsSegmentClear(start, LocalCellCenter(startCol, startRow), radius, _activeRequest.Capabilities, startSurface);

        if (startCellValid)
        {
            _searchGen[startCell] = _searchGeneration;
            _gScore[startCell] = Vector2.Distance(start, LocalCellCenter(startCol, startRow));
            _cameFrom[startCell] = -1;
            _cellSurface[startCell] = startSurface;
            HeapPush(startCell);
            startAnchorCount++;

            if (_goalAnchorGen[startCell] == _searchGeneration && startSurface == targetSurface)
            {
                float distToTarget = Vector2.Distance(LocalCellCenter(startCol, startRow), target);
                _gScore[_virtualGoalCell] = _gScore[startCell] + distToTarget;
                _cameFrom[_virtualGoalCell] = startCell;
                HeapPush(_virtualGoalCell);
            }
        }
        else
        {
            int startWindow = Math.Clamp((int)MathF.Ceiling((radius + 1.5f * _grid.CellSize) / _grid.CellSize), 2, 5);
            for (int dr = -startWindow; dr <= startWindow; dr++)
            {
                int sr = startRow + dr;
                if (sr < 0 || sr >= _localWinRows) continue;

                for (int dc = -startWindow; dc <= startWindow; dc++)
                {
                    int sc = startCol + dc;
                    if (sc < 0 || sc >= _localWinCols) continue;

                    if (!IsLocalCellPassable(sc, sr, radius)) continue;

                    Vector2 center = LocalCellCenter(sc, sr);
                    if (startSurface > 0 && (!hasStartBridge || !startBridge.Contains(center)))
                    {
                        continue;
                    }

                    uint centerStartSurface = startSurface;
                    if (!_grid.IsSegmentClear(start, center, radius, _activeRequest.Capabilities, centerStartSurface)) continue;

                    int cell = sr * _localWinCols + sc;
                    _searchGen[cell] = _searchGeneration;
                    _gScore[cell] = Vector2.Distance(start, center);
                    _cameFrom[cell] = -1;
                    _cellSurface[cell] = centerStartSurface;
                    HeapPush(cell);
                    startAnchorCount++;

                    if (_goalAnchorGen[cell] == _searchGeneration && centerStartSurface == targetSurface)
                    {
                        float distToTarget = Vector2.Distance(center, target);
                        float cost = _gScore[cell] + distToTarget;
                        if (cost < _gScore[_virtualGoalCell])
                        {
                            _gScore[_virtualGoalCell] = cost;
                            _cameFrom[_virtualGoalCell] = cell;
                        }
                    }
                }
            }

            if (startAnchorCount > 0 && _gScore[_virtualGoalCell] < float.PositiveInfinity)
            {
                HeapPush(_virtualGoalCell);
            }
        }
    }

    private bool AdvanceLocalPlanning(WorldState world, int slot, UnitState unit)
    {
        float radius = _activeRadius;
        Vector2 target = _activeRequest.Target;
        if (!_grid.IsFlatGrid && !_activeRequest.IsChunkContinuation && _slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
        {
            target = _slotGlobalPortals[slot * MaxGlobalPlanPortals + _slotGlobalPortalIndices[slot]];
        }

        int maxExpBudget = Math.Min(_tuning.MaxNodeExpansionsPerTick, _tuning.MaxLocalExpansionsPerTick);

        while (_heapCount > 0 && LocalExpansionsThisTick < maxExpBudget)
        {
            if (_activeTotalExpansions >= _tuning.MaxTotalExpansionsPerRequest)
            {
                _unitStatuses[slot] = PathRequestStatus.CapacityExceeded;
                CapacityExceeded++;
                _unitHasActiveRoutes[slot] = false;
                _routeCounts[slot] = 0;
                _routeIndices[slot] = 0;
                world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
                CancelActiveSearch();
                return false;
            }

            int current = HeapPop();
            if (_closedGen[current] == _searchGeneration) continue;
            _closedGen[current] = _searchGeneration;

            LocalExpansionsThisTick++;
            TotalLocalExpansions++;
            _activeTotalExpansions++;

            if (current == _virtualGoalCell)
            {
                InitPathSmoothing(slot, current, target, unit.Position);
                _activeStage = NavigationSearchStage.PathSmoothing;
                return false;
            }

            int col = current % _localWinCols;
            int row = current / _localWinCols;
            Vector2 currentCenter = LocalCellCenter(col, row);
            float currentClearance = GetLocalCellClearance(col, row);
            float currentG = _gScore[current];

            if (_goalAnchorGen[current] == _searchGeneration && _cellSurface[current] == _cellSurface[_virtualGoalCell])
            {
                float distToTarget = Vector2.Distance(currentCenter, target);
                float tentativeGoalG = currentG + distToTarget;

                if (_searchGen[_virtualGoalCell] != _searchGeneration)
                {
                    _searchGen[_virtualGoalCell] = _searchGeneration;
                    _gScore[_virtualGoalCell] = float.PositiveInfinity;
                }

                if (tentativeGoalG < _gScore[_virtualGoalCell] - 1e-4f)
                {
                    _gScore[_virtualGoalCell] = tentativeGoalG;
                    _cameFrom[_virtualGoalCell] = current;
                    if (!IsInHeap(_virtualGoalCell))
                    {
                        HeapPush(_virtualGoalCell);
                    }
                    else
                    {
                        HeapDecreaseKey(_virtualGoalCell);
                    }
                }
            }

            for (int i = 0; i < s_neighbors.Length; i++)
            {
                var n = s_neighbors[i];
                int nextCol = col + n.dCol;
                int nextRow = row + n.dRow;

                if (!IsInLocalBounds(nextCol, nextRow)) continue;

                if (n.isDiagonal)
                {
                    int c1Col = col + n.c1Col;
                    int c1Row = row + n.c1Row;
                    int c2Col = col + n.c2Col;
                    int c2Row = row + n.c2Row;
                    if (!IsLocalCellPassable(c1Col, c1Row, radius) || !IsLocalCellPassable(c2Col, c2Row, radius))
                        continue;
                }

                if (!IsLocalCellPassable(nextCol, nextRow, radius)) continue;

                int nextCell = nextRow * _localWinCols + nextCol;
                if (_closedGen[nextCell] == _searchGeneration) continue;

                float stepCost = n.isDiagonal ? 1.41421356f * _grid.CellSize : _grid.CellSize;
                float nextClearance = GetLocalCellClearance(nextCol, nextRow);
                float safeThreshold = radius + stepCost * 0.5f;
                Vector2 nextCenter = LocalCellCenter(nextCol, nextRow);
                uint currentSurface = _cellSurface[current];
                uint nextSurface = currentSurface;
                bool surfaceTransitionRejected = false;

                if (_grid.Bridges.Count > 0 && (_activeRequest.Capabilities & TraversabilityCapabilities.Bridge) != 0)
                {
                    for (int b = 0; b < _grid.Bridges.Count; b++)
                    {
                        BridgeStructure br = _grid.Bridges[b];
                        bool currentOnBridge = br.Contains(currentCenter);
                        bool nextOnBridge = br.Contains(nextCenter);

                        if (currentSurface == 0)
                        {
                            if (!currentOnBridge && nextOnBridge)
                            {
                                float threshold = MathF.Max(6.0f, br.Width * 0.5f + 1.0f);
                                if (br.IsNearEntry(currentCenter, threshold) && br.IsNearEntry(nextCenter, threshold))
                                {
                                    nextSurface = (uint)br.Id.Value;
                                    break;
                                }
                            }
                        }
                        else if (currentSurface == (uint)br.Id.Value)
                        {
                            if (!nextOnBridge)
                            {
                                float threshold = MathF.Max(6.0f, br.Width * 0.5f + 1.0f);
                                if (!br.IsNearEntry(currentCenter, threshold) || !br.IsNearEntry(nextCenter, threshold))
                                {
                                    surfaceTransitionRejected = true;
                                    break;
                                }
                                nextSurface = 0;
                                break;
                            }
                        }
                    }
                }

                if (surfaceTransitionRejected) continue;

                if (_grid.Bridges.Count > 0 || currentClearance < safeThreshold || nextClearance < safeThreshold)
                {
                    uint stepSurface = currentSurface > 0 ? currentSurface : nextSurface;
                    if (!_grid.IsSegmentClear(currentCenter, nextCenter, radius, _activeRequest.Capabilities, stepSurface))
                        continue;
                }

                float tentativeG = currentG + stepCost;

                if (_searchGen[nextCell] != _searchGeneration)
                {
                    _searchGen[nextCell] = _searchGeneration;
                    _gScore[nextCell] = float.PositiveInfinity;
                }

                if (tentativeG < _gScore[nextCell] - 1e-4f)
                {
                    _gScore[nextCell] = tentativeG;
                    _cameFrom[nextCell] = current;
                    _cellSurface[nextCell] = nextSurface;
                    if (!IsInHeap(nextCell))
                    {
                        HeapPush(nextCell);
                    }
                    else
                    {
                        HeapDecreaseKey(nextCell);
                    }
                }
            }
        }

        if (_heapCount == 0 && _hasActiveSearch)
        {
            _unitStatuses[slot] = PathRequestStatus.NoPath;
            FailedSearches++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
            CancelActiveSearch();
            return false;
        }

        return true;
    }

    private void InitPathSmoothing(int slot, int endNode, Vector2 target, Vector2 currentPos)
    {
        _rawPathCount = 0;
        int trace = (endNode == _virtualGoalCell) ? _cameFrom[_virtualGoalCell] : endNode;
        while (trace != -1 && _rawPathCount < _rawPathBuffer.Length - 4)
        {
            int col = trace % _localWinCols;
            int row = trace / _localWinCols;
            _rawPathSurfaceBuffer[_rawPathCount] = _cellSurface[trace];
            _rawPathBuffer[_rawPathCount++] = LocalCellCenter(col, row);
            trace = _cameFrom[trace];
        }

        if (_rawPathCount == 0)
        {
            _smoothingAnchorIdx = -1;
            return;
        }

        // Reverse raw path
        for (int i = 0; i < _rawPathCount / 2; i++)
        {
            (_rawPathBuffer[i], _rawPathBuffer[_rawPathCount - 1 - i]) = (_rawPathBuffer[_rawPathCount - 1 - i], _rawPathBuffer[i]);
            (_rawPathSurfaceBuffer[i], _rawPathSurfaceBuffer[_rawPathCount - 1 - i]) = (_rawPathSurfaceBuffer[_rawPathCount - 1 - i], _rawPathSurfaceBuffer[i]);
        }

        if (Vector2.DistanceSquared(_rawPathBuffer[_rawPathCount - 1], target) > 1e-4f)
        {
            _rawPathSurfaceBuffer[_rawPathCount] = _cellSurface[_virtualGoalCell];
            _rawPathBuffer[_rawPathCount++] = target;
        }
        else
        {
            _rawPathSurfaceBuffer[_rawPathCount - 1] = _cellSurface[_virtualGoalCell];
            _rawPathBuffer[_rawPathCount - 1] = target;
        }

        _smoothedCount = 0;

        // The first splice uses the same resumable ray budget as later shortcut searches.
        _smoothingCurrentAnchor = _activeRequest.IsChunkContinuation ? _activeRequest.Start : currentPos;
        _smoothingCurrentAnchorSurface = _activeSurface;
        _smoothingAnchorIdx = -1;

        _smoothingCandidateIdx = _rawPathCount - 1;
    }

    private bool AdvancePathSmoothing(WorldState world, int slot, UnitState unit)
    {
        while (_smoothingAnchorIdx < _rawPathCount - 1 &&
               _smoothedCount < _tuning.MaxWaypointsPerRoute &&
               SmoothingRaycastsThisTick < _tuning.MaxSmoothingRaycastsPerTick)
        {
            int farthest = -1;
            while (_smoothingCandidateIdx > _smoothingAnchorIdx)
            {
                if (SmoothingRaycastsThisTick >= _tuning.MaxSmoothingRaycastsPerTick) return true;
                int nextIdx = _smoothingCandidateIdx--;
                SmoothingRaycastsThisTick++;
                TotalSmoothingRaycasts++;

                uint candidateSurface = _rawPathSurfaceBuffer[nextIdx];
                if (_smoothingCurrentAnchorSurface != candidateSurface)
                {
                    // Transition between ground (0) and deck (> 0):
                    // A shortcut across surfaces is only allowed if near the bridge ramp entry/exit.
                    if (_grid.Bridges.Count > 0)
                    {
                        bool nearRamp = false;
                        for (int b = 0; b < _grid.Bridges.Count; b++)
                        {
                            BridgeStructure br = _grid.Bridges[b];
                            float threshold = MathF.Max(6.0f, br.Width * 0.5f + 1.0f);
                            if (br.IsNearEntry(_smoothingCurrentAnchor, threshold) && br.IsNearEntry(_rawPathBuffer[nextIdx], threshold))
                            {
                                nearRamp = true;
                                break;
                            }
                        }
                        if (!nearRamp) continue;
                    }
                }

                uint smoothSurface = _smoothingCurrentAnchorSurface > 0 ? _smoothingCurrentAnchorSurface : candidateSurface;
                if (_grid.IsSegmentClear(_smoothingCurrentAnchor, _rawPathBuffer[nextIdx], _activeRadius, _activeRequest.Capabilities, smoothSurface))
                {
                    farthest = nextIdx;
                    break;
                }
            }

            if (farthest == -1)
            {
                farthest = _smoothingAnchorIdx + 1;
            }

            _smoothingCurrentAnchor = _rawPathBuffer[farthest];
            _smoothingCurrentAnchorSurface = _rawPathSurfaceBuffer[farthest];
            _smoothedPathBuffer[_smoothedCount++] = _smoothingCurrentAnchor;
            _smoothingAnchorIdx = farthest;
            _smoothingCandidateIdx = _rawPathCount - 1;
        }

        if (SmoothingRaycastsThisTick >= _tuning.MaxSmoothingRaycastsPerTick && _smoothingAnchorIdx < _rawPathCount - 1)
        {
            return true;
        }

        bool reachedChunkEnd = _smoothingAnchorIdx >= _rawPathCount - 1;

        if (_grid.IsFlatGrid && !reachedChunkEnd)
        {
            // On flat prototype grid, overflowing MaxWaypointsPerRoute reports CapacityExceeded
            _unitStatuses[slot] = PathRequestStatus.CapacityExceeded;
            CapacityExceeded++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
            CancelActiveSearch();
            return false;
        }

        if (_smoothedCount == 0 && _rawPathCount > 0)
        {
            int countToCopy = Math.Min(_rawPathCount, _tuning.MaxWaypointsPerRoute);
            for (int i = 0; i < countToCopy; i++)
            {
                _smoothedPathBuffer[i] = _rawPathBuffer[i];
            }
            _smoothedCount = countToCopy;
        }

        if (_smoothedCount == 0)
        {
            _unitStatuses[slot] = PathRequestStatus.CapacityExceeded;
            CapacityExceeded++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, unit.Position, false, supportSurface: unit.SupportSurface);
            CancelActiveSearch();
            return false;
        }

        if (_activeRequest.IsChunkContinuation)
        {
            ChunkRequestsCompleted++;
            _unitStatuses[slot] = PathRequestStatus.PathFound;
            _unitChunkPending[slot] = false;

            if (_slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
            {
                _slotGlobalPortalIndices[slot]++;
            }

            int offset = slot * _tuning.MaxWaypointsPerRoute;
            if (_unitHasActiveRoutes[slot] && _routeIndices[slot] >= _routeCounts[slot])
            {
                for (int i = 0; i < _smoothedCount; i++)
                {
                    _routeWaypoints[offset + i] = _smoothedPathBuffer[i];
                }
                _routeCounts[slot] = _smoothedCount;
                _routeIndices[slot] = 0;
                _unitChunkWaitTicks[slot] = 0;
                world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, _unitFinalTargets[slot], true, supportSurface: unit.SupportSurface);
            }
            else if (_unitHasActiveRoutes[slot])
            {
                for (int i = 0; i < _smoothedCount; i++)
                {
                    _slotNextChunkWaypoints[offset + i] = _smoothedPathBuffer[i];
                }
                _slotNextChunkCounts[slot] = _smoothedCount;
            }
            else
            {
                for (int i = 0; i < _smoothedCount; i++)
                {
                    _routeWaypoints[offset + i] = _smoothedPathBuffer[i];
                }
                _routeCounts[slot] = _smoothedCount;
                _routeIndices[slot] = 0;
                _unitHasActiveRoutes[slot] = true;
                _unitChunkWaitTicks[slot] = 0;
                world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, _unitFinalTargets[slot], true, supportSurface: unit.SupportSurface);
            }
        }
        else
        {
            int offset = slot * _tuning.MaxWaypointsPerRoute;
            for (int i = 0; i < _smoothedCount; i++)
            {
                _routeWaypoints[offset + i] = _smoothedPathBuffer[i];
            }
            _routeCounts[slot] = _smoothedCount;
            _routeIndices[slot] = 0;
            _unitHasActiveRoutes[slot] = true;
            _unitChunkPending[slot] = false;
            _unitChunkWaitTicks[slot] = 0;

            if (_slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
            {
                _slotGlobalPortalIndices[slot]++;
            }

            CompletedSearches++;
            _unitFinalTargets[slot] = _activeRequest.Target;
            _unitStatuses[slot] = PathRequestStatus.PathFound;
            world.UpdateUnitNavigation(_activeRequest.Unit, unit.Position, _unitFinalTargets[slot], true, supportSurface: unit.SupportSurface);
        }

        CancelActiveSearch();
        return false;
    }

    private void SetUnitSingleWaypointRoute(int slot, Vector2 target)
    {
        int offset = slot * _tuning.MaxWaypointsPerRoute;
        _routeWaypoints[offset] = target;
        _routeCounts[slot] = 1;
        _routeIndices[slot] = 0;
        _unitHasActiveRoutes[slot] = true;
        _unitChunkPending[slot] = false;
    }

    private bool TrySnapDestination(Vector2 start, Vector2 target, float radius, out Vector2 snapped) =>
        TrySnapDestination(start, target, radius, TraversabilityCapabilities.DefaultVehicle, 0, out snapped);

    private bool TrySnapDestination(Vector2 start, Vector2 target, float radius, TraversabilityCapabilities capabilities, out Vector2 snapped) =>
        TrySnapDestination(start, target, radius, capabilities, 0, out snapped);

    private bool TrySnapDestination(Vector2 start, Vector2 target, float radius, TraversabilityCapabilities capabilities, uint targetSurface, out Vector2 snapped) =>
        _grid.TrySnapDestination(start, target, radius, capabilities, targetSurface, _tuning.MaxSnapDistance, out snapped);

    private void ProcessMovement(WorldState world)
    {
        int unitCount = world.Count;
        for (int i = 0; i < unitCount; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (!TryGetSlot(unit.Id, out int slot))
            {
                continue;
            }

            uint supportSurface = unit.SupportSurface;

            if (!_unitHasActiveRoutes[slot])
            {
                if (unit.Moving)
                {
                    world.UpdateUnitNavigation(unit.Id, unit.Position, unit.Position, false, _currentTick, supportSurface);
                }
                continue;
            }

            float speed = 6.0f;
            float radius = 1.0f;
            float turnRate = 45.0f;
            TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle;
            if (_catalog.TryGetProfile(unit.Definition, out UnitMovementProfile profile))
            {
                speed = profile.Speed;
                radius = profile.Radius;
                capabilities = profile.Capabilities;
                turnRate = profile.TurnRate;
            }

            float maxTurnPerTick = (turnRate * (MathF.PI / 180f)) * Simulation.TickSeconds;
            float currentHeading = 0f;
            if (world.TryGetCombatState(unit.Id, out WorldState.CombatState cs))
            {
                currentHeading = cs.HullHeading;
            }
            float newHeading = currentHeading;
            float remainingTurnBudget = maxTurnPerTick;

            Vector2 currentPos = unit.Position;
            int offset = slot * _tuning.MaxWaypointsPerRoute;
            int count = _routeCounts[slot];
            int idx = _routeIndices[slot];
            uint currentSurface = unit.SupportSurface;
            Vector2 finalTarget = _unitFinalTargets[slot];
            bool hasFinalFacing = _unitHasFinalFacing[slot];
            float finalFacing = _unitFinalFacings[slot];

            bool reachedFinalDestination = Vector2.Distance(currentPos, finalTarget) <= 0.05f;

            if (reachedFinalDestination)
            {
                if (!hasFinalFacing)
                {
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    _unitStatuses[slot] = PathRequestStatus.PathFound;
                    world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, false, _currentTick, supportSurface, newHeading);
                    continue;
                }

                float facingError = CombatSystem.ShorterArcDelta(currentHeading, finalFacing);
                float absFacingError = MathF.Abs(facingError);

                if (absFacingError <= _tuning.FinalFacingTolerance && absFacingError <= remainingTurnBudget)
                {
                    newHeading = finalFacing;
                    _unitHasFinalFacing[slot] = false;
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    _unitStatuses[slot] = PathRequestStatus.PathFound;
                    world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, false, _currentTick, supportSurface, newHeading);
                    continue;
                }
                else
                {
                    float turnStep = MathF.Min(absFacingError, remainingTurnBudget);
                    newHeading = CombatSystem.NormalizeYaw(currentHeading + MathF.Sign(facingError) * turnStep);
                    world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, true, _currentTick, supportSurface, newHeading);
                    continue;
                }
            }

            while (idx < count && Vector2.DistanceSquared(currentPos, _routeWaypoints[offset + idx]) <= 1e-4f)
            {
                idx++;
            }

            if (idx < count)
            {
                Vector2 targetWaypoint = _routeWaypoints[offset + idx];
                Vector2 toWaypoint = targetWaypoint - currentPos;
                float dist = toWaypoint.Length();

                if (dist > 1e-4f)
                {
                    float remainingDistance = speed * Simulation.TickSeconds;

                    while (remainingDistance > 1e-4f && idx < count)
                    {
                        targetWaypoint = _routeWaypoints[offset + idx];
                        toWaypoint = targetWaypoint - currentPos;
                        dist = toWaypoint.Length();

                        if (dist <= 1e-4f)
                        {
                            idx++;
                            continue;
                        }

                        float desiredHeading = MathF.Atan2(-toWaypoint.X, -toWaypoint.Y);
                        float headingError = CombatSystem.ShorterArcDelta(newHeading, desiredHeading);
                        float absHeadingError = MathF.Abs(headingError);

                        if (absHeadingError > 1e-5f && remainingTurnBudget > 1e-5f)
                        {
                            float turnStep = MathF.Min(absHeadingError, remainingTurnBudget);
                            newHeading = CombatSystem.NormalizeYaw(newHeading + MathF.Sign(headingError) * turnStep);
                            remainingTurnBudget -= turnStep;
                            headingError = CombatSystem.ShorterArcDelta(newHeading, desiredHeading);
                            absHeadingError = MathF.Abs(headingError);
                        }

                        if (absHeadingError > _tuning.PivotSteerAngleThreshold)
                        {
                            // Heading discrepancy exceeds alignment threshold: halt linear displacement to pivot in place
                            remainingDistance = 0f;
                            break;
                        }

                        Vector2 stepStart = currentPos;
                        Vector2 stepEnd = currentPos;
                        bool clear = false;

                        if (remainingDistance < dist)
                        {
                            Vector2 step = toWaypoint * (remainingDistance / dist);
                            Vector2 nextPos = currentPos + step;
                            if (_grid.IsSegmentClear(currentPos, nextPos, radius, capabilities, currentSurface))
                            {
                                stepEnd = nextPos;
                                remainingDistance = 0f;
                                clear = true;
                            }
                            else
                            {
                                idx = count;
                                remainingDistance = 0f;
                                _unitStatuses[slot] = PathRequestStatus.NoPath;
                            }
                        }
                        else
                        {
                            if (_grid.IsSegmentClear(currentPos, targetWaypoint, radius, capabilities, currentSurface))
                            {
                                stepEnd = targetWaypoint;
                                remainingDistance -= dist;
                                idx++;
                                clear = true;
                            }
                            else
                            {
                                idx = count;
                                remainingDistance = 0f;
                                _unitStatuses[slot] = PathRequestStatus.NoPath;
                            }
                        }

                        if (clear)
                        {
                            if (currentSurface == 0)
                            {
                                if ((capabilities & TraversabilityCapabilities.Bridge) != 0 && _grid.Bridges.Count > 0)
                                {
                                    for (int b = 0; b < _grid.Bridges.Count; b++)
                                    {
                                        BridgeStructure bridge = _grid.Bridges[b];
                                        if (bridge.Contains(stepEnd))
                                        {
                                            if (!bridge.Contains(stepStart) && (bridge.IsNearEntry(stepStart) || bridge.IsNearEntry(stepEnd)))
                                            {
                                                currentSurface = (uint)bridge.Id.Value;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                if (_grid.TryGetBridge(new StructureId((int)currentSurface), out BridgeStructure bridge))
                                {
                                    if (!bridge.Contains(stepEnd))
                                    {
                                        currentSurface = 0;
                                    }
                                }
                                else
                                {
                                    currentSurface = 0;
                                }
                            }

                            currentPos = stepEnd;
                        }
                    }
                }
            }

            supportSurface = currentSurface;
            _routeIndices[slot] = idx;
            bool reachedEndOfChunk = idx >= count;

            if (reachedEndOfChunk && _slotNextChunkCounts[slot] > 0)
            {
                // Seamlessly swap in staged next chunk
                int nextCount = _slotNextChunkCounts[slot];
                for (int w = 0; w < nextCount; w++)
                {
                    _routeWaypoints[offset + w] = _slotNextChunkWaypoints[offset + w];
                }
                _routeCounts[slot] = nextCount;
                _routeIndices[slot] = 0;
                _slotNextChunkCounts[slot] = 0;
                _unitChunkPending[slot] = false;
                reachedEndOfChunk = false;
                idx = 0;
                count = nextCount;
            }

            reachedFinalDestination = Vector2.Distance(currentPos, finalTarget) <= 0.05f;

            if (reachedFinalDestination)
            {
                if (!hasFinalFacing)
                {
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    _unitStatuses[slot] = PathRequestStatus.PathFound;
                    world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, false, _currentTick, supportSurface, newHeading);
                    continue;
                }

                float facingError = CombatSystem.ShorterArcDelta(newHeading, finalFacing);
                float absFacingError = MathF.Abs(facingError);

                if (absFacingError <= _tuning.FinalFacingTolerance && absFacingError <= remainingTurnBudget)
                {
                    newHeading = finalFacing;
                    _unitHasFinalFacing[slot] = false;
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    _unitStatuses[slot] = PathRequestStatus.PathFound;
                    world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, false, _currentTick, supportSurface, newHeading);
                    continue;
                }
                else if (remainingTurnBudget > 1e-5f)
                {
                    float turnStep = MathF.Min(absFacingError, remainingTurnBudget);
                    newHeading = CombatSystem.NormalizeYaw(newHeading + MathF.Sign(facingError) * turnStep);
                    remainingTurnBudget -= turnStep;
                    world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, true, _currentTick, supportSurface, newHeading);
                    continue;
                }
                else
                {
                    // Full turn budget consumed by steering this tick; continue facing maneuver on next tick
                    world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, true, _currentTick, supportSurface, newHeading);
                    continue;
                }
            }

            // Only enqueue next chunk if current chunk does not already reach the final destination
            Vector2 chunkEnd = _routeWaypoints[offset + Math.Max(0, count - 1)];
            bool chunkReachesFinalTarget = Vector2.Distance(chunkEnd, finalTarget) <= 0.05f;

            if (!chunkReachesFinalTarget && !reachedFinalDestination && !_unitChunkPending[slot] && _slotNextChunkCounts[slot] == 0 && idx >= Math.Max(0, count - _tuning.ChunkLookaheadWaypoints))
            {
                EnqueueNextChunkRequest(slot, unit, radius, capabilities);
            }

            if (reachedEndOfChunk)
            {
                _unitChunkWaitTicks[slot]++;
                ChunkWaitTicks++;
                world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, false, _currentTick, supportSurface, newHeading);
            }
            else
            {
                world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, true, _currentTick, supportSurface, newHeading);
            }
        }
    }

    private void EnqueueNextChunkRequest(int slot, in UnitState unit, float radius, TraversabilityCapabilities capabilities)
    {
        if (_queueCount >= _requestQueue.Length) return;

        Vector2 chunkStart = _routeWaypoints[slot * _tuning.MaxWaypointsPerRoute + Math.Max(0, _routeCounts[slot] - 1)];
        Vector2 nextTarget = _unitFinalTargets[slot];

        if (_slotGlobalPortalCounts[slot] > 0 && _slotGlobalPortalIndices[slot] < _slotGlobalPortalCounts[slot])
        {
            nextTarget = _slotGlobalPortals[slot * MaxGlobalPlanPortals + _slotGlobalPortalIndices[slot]];
        }

        uint orderGen = _unitOrderGenerations[slot];
        _unitChunkPending[slot] = true;

        _requestQueue[_queueTail] = new PathRequest(
            unit.Owner, unit.Id, chunkStart, nextTarget, radius, orderGen, IsChunkContinuation: true, Capabilities: capabilities);
        _queueTail = (_queueTail + 1) % _requestQueue.Length;
        _queueCount++;
    }

    // Local window coordinate calculations
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInLocalBounds(int col, int row) =>
        (uint)col < (uint)_localWinCols && (uint)row < (uint)_localWinRows;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector2 LocalCellCenter(int col, int row) =>
        new(_localWinMinX + (col + 0.5f) * _grid.CellSize, _localWinMinZ + (row + 0.5f) * _grid.CellSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (int Col, int Row) LocalWorldToCell(Vector2 pos)
    {
        int col = Math.Clamp((int)((pos.X - _localWinMinX) / _grid.CellSize), 0, _localWinCols - 1);
        int row = Math.Clamp((int)((pos.Y - _localWinMinZ) / _grid.CellSize), 0, _localWinRows - 1);
        return (col, row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetLocalCellClearance(int col, int row)
    {
        if (!IsInLocalBounds(col, row)) return 0f;
        if (_grid.IsFlatGrid)
        {
            if (_grid.TerrainObstacles.Count == 0 && _grid.Bridges.Count == 0)
            {
                (int gCol, int gRow) = _grid.WorldToCell(LocalCellCenter(col, row));
                return _grid.GetCellClearance(gCol, gRow);
            }
            return _grid.ComputePointClearance(LocalCellCenter(col, row), _activeRequest.Capabilities);
        }
        return _localClearance[row * _localWinCols + col];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsLocalCellPassable(int col, int row, float radius) =>
        GetLocalCellClearance(col, row) >= radius;

    // Indexed min-heap operations
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInHeap(int cell) =>
        _heapGen[cell] == _searchGeneration && _heapPosition[cell] >= 0;

    private void HeapPush(int cell)
    {
        int i = _heapCount++;
        _heap[i] = cell;
        _heapPosition[cell] = i;
        _heapGen[cell] = _searchGeneration;
        BubbleUp(i);
    }

    private void HeapDecreaseKey(int cell)
    {
        if (IsInHeap(cell))
        {
            BubbleUp(_heapPosition[cell]);
        }
    }

    private int HeapPop()
    {
        int result = _heap[0];
        _heapPosition[result] = -1;
        _heapCount--;
        if (_heapCount > 0)
        {
            _heap[0] = _heap[_heapCount];
            _heapPosition[_heap[0]] = 0;
            BubbleDown(0);
        }
        return result;
    }

    private void BubbleUp(int i)
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (ComesBefore(_heap[i], _heap[parent]))
            {
                SwapHeap(i, parent);
                i = parent;
            }
            else break;
        }
    }

    private void BubbleDown(int i)
    {
        while (true)
        {
            int left = (i << 1) + 1;
            int right = left + 1;
            if (left >= _heapCount) break;
            int best = left;
            if (right < _heapCount && ComesBefore(_heap[right], _heap[left]))
                best = right;
            if (ComesBefore(_heap[best], _heap[i]))
            {
                SwapHeap(i, best);
                i = best;
            }
            else break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwapHeap(int i, int j)
    {
        int cellI = _heap[i];
        int cellJ = _heap[j];
        _heap[i] = cellJ;
        _heap[j] = cellI;
        _heapPosition[cellI] = j;
        _heapPosition[cellJ] = i;
    }

    private bool ComesBefore(int cellA, int cellB)
    {
        float fA = _gScore[cellA] + Heuristic(cellA);
        float fB = _gScore[cellB] + Heuristic(cellB);
        if (MathF.Abs(fA - fB) > 1e-4f) return fA < fB;

        float gA = _gScore[cellA];
        float gB = _gScore[cellB];
        if (MathF.Abs(gA - gB) > 1e-4f) return gA > gB;

        return cellA < cellB;
    }

    private float Heuristic(int cell)
    {
        if (cell == _virtualGoalCell) return 0f;
        int col = cell % _localWinCols;
        int row = cell / _localWinCols;
        return Vector2.Distance(LocalCellCenter(col, row), _activeRequest.Target);
    }
}
