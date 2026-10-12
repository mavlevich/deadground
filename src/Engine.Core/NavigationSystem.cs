using System.Numerics;
using System.Runtime.CompilerServices;

namespace DeadGround.Engine;

/// <summary>
/// Server-side authoritative navigation and deterministic path planning system.
/// Executes time-sliced A* search over NavigationGrid with bounded per-tick node expansion budget.
/// Strictly allocation-free during simulation ticks.
/// </summary>
public sealed class NavigationSystem : ISimulationSystem
{
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

    // Active in-flight search state
    private bool _hasActiveSearch;
    private PathRequest _activeRequest;
    private readonly int _virtualGoalCell;
    private float _activeRadius;

    // A* preallocated search buffers (sized to TotalCells + 1 for virtual goal)
    private readonly int[] _heap;
    private int _heapCount;
    private readonly int[] _heapPosition;
    private readonly uint[] _heapGen;
    private readonly float[] _gScore;
    private readonly int[] _cameFrom;
    private readonly uint[] _searchGen;
    private readonly uint[] _closedGen;
    private readonly uint[] _goalAnchorGen;
    private uint _searchGeneration;

    // Path reconstruction and smoothing buffers
    private readonly Vector2[] _rawPathBuffer;
    private readonly Vector2[] _smoothedPathBuffer;

    // Bounded live unit storage slots and lookup table
    private readonly int _maxUnits;
    private readonly int[] _freeSlots;
    private int _freeSlotCount;
    private readonly EntityId[] _slotUnitIds;
    private readonly uint[] _idTableKeys;
    private readonly int[] _idTableSlots;
    private readonly int _idTableMask;
    private uint _lastWorldRemovalCount;

    // Preallocated per-slot route storage
    private readonly Vector2[] _routeWaypoints;
    private readonly int[] _routeCounts;
    private readonly int[] _routeIndices;
    private readonly Vector2[] _unitFinalTargets;
    private readonly PathRequestStatus[] _unitStatuses;
    private readonly uint[] _unitOrderGenerations;
    private readonly bool[] _unitHasActiveRoutes;
    private uint _nextOrderGeneration;

    public NavigationGrid Grid => _grid;
    public NavigationTuning Tuning => _tuning;
    public int PendingRequests => _queueCount + (_hasActiveSearch ? 1 : 0);
    public int NodesExpandedThisTick { get; private set; }
    public int TotalNodesExpanded { get; private set; }
    public int CompletedSearches { get; private set; }
    public int FailedSearches { get; private set; }
    public int CapacityExceeded { get; private set; }

    public readonly record struct PathRequest(
        int Player,
        EntityId Unit,
        Vector2 Start,
        Vector2 Target,
        float Radius,
        uint OrderGeneration);

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

        _requestQueue = new PathRequest[_tuning.MaxPendingRequests];
        _tempQueue = new PathRequest[_tuning.MaxPendingRequests];

        int totalCells = _grid.TotalCells;
        int nodeCount = totalCells + 1;
        _virtualGoalCell = totalCells;

        _heap = new int[nodeCount];
        _heapPosition = new int[nodeCount];
        _heapGen = new uint[nodeCount];
        _gScore = new float[nodeCount];
        _cameFrom = new int[nodeCount];
        _searchGen = new uint[nodeCount];
        _closedGen = new uint[nodeCount];
        _goalAnchorGen = new uint[nodeCount];

        _rawPathBuffer = new Vector2[Math.Max(1024, totalCells + 4)];
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
        _unitStatuses = new PathRequestStatus[_maxUnits];
        _unitOrderGenerations = new uint[_maxUnits];
        _unitHasActiveRoutes = new bool[_maxUnits];
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
                _unitFinalTargets[slot] = Vector2.Zero;
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

        // Backward-shift deletion in open-addressing table
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
        _unitFinalTargets[slot] = Vector2.Zero;
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
                    _hasActiveSearch = false;
                }
                ReleaseSlot(s);
            }
        }
    }

    public PathRequestStatus GetUnitStatus(EntityId unit)
    {
        if (TryGetSlot(unit, out int slot))
            return _unitStatuses[slot];
        return PathRequestStatus.None;
    }

    public bool EnqueueCommand(int player, MoveCommand command)
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

        // Cancel previous active search if it belonged to this unit
        if (_hasActiveSearch && _activeRequest.Unit == command.Unit)
        {
            _hasActiveSearch = false;
        }

        // Keep previous active route running while the new path is being searched.
        // Only bump order generation and mark status as Pending.
        uint orderGen = checked(++_nextOrderGeneration);
        _unitOrderGenerations[slot] = orderGen;
        if (!_unitHasActiveRoutes[slot])
        {
            _unitFinalTargets[slot] = command.Target;
        }
        _unitStatuses[slot] = PathRequestStatus.Pending;

        float radius = 1.0f;
        Vector2 start = command.Target; // Resolved upon dequeue
        _requestQueue[_queueTail] = new PathRequest(player, command.Unit, start, command.Target, radius, orderGen);
        _queueTail = (_queueTail + 1) % _requestQueue.Length;
        _queueCount++;
        return true;
    }

    public void ResetPlayer(int player)
    {
        // 1. Cancel active in-flight search if it belongs to this player
        if (_hasActiveSearch && _activeRequest.Player == player)
        {
            EntityId unitId = _activeRequest.Unit;
            _hasActiveSearch = false;
            if (TryGetSlot(unitId, out int s))
            {
                _unitStatuses[s] = PathRequestStatus.None;
            }
        }

        // 2. Remove all pending deferred requests in queue for this player
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

        SweepRemovedUnits(world);

        NodesExpandedThisTick = 0;

        // 1. Process pathfinding requests up to per-tick expansion budget
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

                if (!StartActiveSearch(world, req))
                {
                    continue;
                }
            }

            ExpandActiveSearch(world);
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

            Vector2 start = unit.Position;
            Vector2 target = req.Target;

            if (!_grid.IsPointClear(target, radius))
            {
                if (!TrySnapDestination(start, target, radius, out Vector2 snapped))
                {
                    _unitStatuses[slot] = PathRequestStatus.InvalidDestination;
                    FailedSearches++;
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    world.UpdateUnitNavigation(req.Unit, unit.Position, unit.Position, false);
                    continue;
                }
                target = snapped;
                req = req with { Target = target };
            }

            if (Vector2.DistanceSquared(start, target) <= 1e-4f)
            {
                _unitStatuses[slot] = PathRequestStatus.PathFound;
                _unitHasActiveRoutes[slot] = false;
                _routeCounts[slot] = 0;
                _routeIndices[slot] = 0;
                _unitFinalTargets[slot] = target;
                CompletedSearches++;
                world.UpdateUnitNavigation(req.Unit, unit.Position, target, false);
                continue;
            }

            if (_grid.IsSegmentClear(start, target, radius))
            {
                SetUnitSingleWaypointRoute(slot, target);
                _unitFinalTargets[slot] = target;
                _unitStatuses[slot] = PathRequestStatus.PathFound;
                CompletedSearches++;
                world.UpdateUnitNavigation(req.Unit, unit.Position, target, true);
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

    private bool StartActiveSearch(WorldState world, PathRequest req)
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

        Vector2 start = unit.Position;
        Vector2 target = req.Target;

        if (!_grid.IsPointClear(target, radius))
        {
            if (!TrySnapDestination(start, target, radius, out Vector2 snapped))
            {
                _unitStatuses[slot] = PathRequestStatus.InvalidDestination;
                FailedSearches++;
                _unitHasActiveRoutes[slot] = false;
                _routeCounts[slot] = 0;
                _routeIndices[slot] = 0;
                world.UpdateUnitNavigation(req.Unit, unit.Position, unit.Position, false);
                return false;
            }
            target = snapped;
            req = req with { Target = target };
        }

        if (Vector2.DistanceSquared(start, target) <= 1e-4f)
        {
            _unitStatuses[slot] = PathRequestStatus.PathFound;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            _unitFinalTargets[slot] = target;
            CompletedSearches++;
            world.UpdateUnitNavigation(req.Unit, unit.Position, target, false);
            return false;
        }

        if (_grid.IsSegmentClear(start, target, radius))
        {
            SetUnitSingleWaypointRoute(slot, target);
            _unitFinalTargets[slot] = target;
            _unitStatuses[slot] = PathRequestStatus.PathFound;
            CompletedSearches++;
            world.UpdateUnitNavigation(req.Unit, unit.Position, target, true);
            return false;
        }

        _searchGeneration++;
        _heapCount = 0;
        _activeRequest = req with { Start = start, Radius = radius };
        _activeRadius = radius;

        // 1. Identify goal anchor cells connecting to target
        (int goalCol, int goalRow) = _grid.WorldToCell(target);
        int goalWindow = Math.Clamp((int)MathF.Ceiling((radius + 1.5f * _grid.CellSize) / _grid.CellSize), 2, 5);
        int goalAnchorCount = 0;

        for (int dr = -goalWindow; dr <= goalWindow; dr++)
        {
            int gr = goalRow + dr;
            if (gr < 0 || gr >= _grid.Rows) continue;

            for (int dc = -goalWindow; dc <= goalWindow; dc++)
            {
                int gc = goalCol + dc;
                if (gc < 0 || gc >= _grid.Columns) continue;

                if (!_grid.IsCellPassable(gc, gr, radius)) continue;

                Vector2 center = _grid.CellCenter(gc, gr);
                if (!_grid.IsSegmentClear(center, target, radius)) continue;

                int cell = _grid.CellIndex(gc, gr);
                _goalAnchorGen[cell] = _searchGeneration;
                goalAnchorCount++;
            }
        }

        if (goalAnchorCount == 0)
        {
            _unitStatuses[slot] = PathRequestStatus.NoPath;
            FailedSearches++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(req.Unit, unit.Position, unit.Position, false);
            return false;
        }

        // 2. Initialize virtual goal node
        _searchGen[_virtualGoalCell] = _searchGeneration;
        _gScore[_virtualGoalCell] = float.PositiveInfinity;
        _cameFrom[_virtualGoalCell] = -1;
        _heapPosition[_virtualGoalCell] = -1;

        // 3. Identify start anchor cells connecting to start
        (int startCol, int startRow) = _grid.WorldToCell(start);
        int startCell = _grid.CellIndex(startCol, startRow);
        int startAnchorCount = 0;

        if (_grid.IsInBounds(startCol, startRow) &&
            _grid.IsCellPassable(startCol, startRow, radius) &&
            _grid.IsSegmentClear(start, _grid.CellCenter(startCell), radius))
        {
            _searchGen[startCell] = _searchGeneration;
            _gScore[startCell] = Vector2.Distance(start, _grid.CellCenter(startCell));
            _cameFrom[startCell] = -1;
            HeapPush(startCell);
            startAnchorCount++;

            if (_goalAnchorGen[startCell] == _searchGeneration)
            {
                float distToTarget = Vector2.Distance(_grid.CellCenter(startCell), target);
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
                if (sr < 0 || sr >= _grid.Rows) continue;

                for (int dc = -startWindow; dc <= startWindow; dc++)
                {
                    int sc = startCol + dc;
                    if (sc < 0 || sc >= _grid.Columns) continue;

                    if (!_grid.IsCellPassable(sc, sr, radius)) continue;

                    Vector2 center = _grid.CellCenter(sc, sr);
                    if (!_grid.IsSegmentClear(start, center, radius)) continue;

                    int cell = _grid.CellIndex(sc, sr);
                    _searchGen[cell] = _searchGeneration;
                    _gScore[cell] = Vector2.Distance(start, center);
                    _cameFrom[cell] = -1;
                    HeapPush(cell);
                    startAnchorCount++;

                    if (_goalAnchorGen[cell] == _searchGeneration)
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

        if (startAnchorCount == 0)
        {
            _unitStatuses[slot] = PathRequestStatus.NoPath;
            FailedSearches++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(req.Unit, unit.Position, unit.Position, false);
            return false;
        }

        _unitStatuses[slot] = PathRequestStatus.Searching;
        _hasActiveSearch = true;
        return true;
    }

    private void ExpandActiveSearch(WorldState world)
    {
        EntityId unitId = _activeRequest.Unit;
        if (!world.TryGetUnit(unitId, out UnitState unit))
        {
            _hasActiveSearch = false;
            if (TryGetSlot(unitId, out int s)) ReleaseSlot(s);
            return;
        }

        if (!TryGetSlot(unitId, out int slot))
        {
            _hasActiveSearch = false;
            return;
        }

        float radius = _activeRadius;
        Vector2 target = _activeRequest.Target;

        while (_heapCount > 0 && NodesExpandedThisTick < _tuning.MaxNodeExpansionsPerTick)
        {
            int current = HeapPop();
            if (_closedGen[current] == _searchGeneration) continue;
            _closedGen[current] = _searchGeneration;

            NodesExpandedThisTick++;
            TotalNodesExpanded++;

            if (current == _virtualGoalCell)
            {
                bool reached = ReconstructAndApplyRoute(slot, current, _activeRequest.Start, target, radius, unit.Position);

                if (reached)
                {
                    _unitFinalTargets[slot] = target;
                    _unitStatuses[slot] = PathRequestStatus.PathFound;
                    CompletedSearches++;
                    world.UpdateUnitNavigation(unitId, unit.Position, target, true);
                }
                else
                {
                    _unitStatuses[slot] = PathRequestStatus.CapacityExceeded;
                    CapacityExceeded++;
                    _unitHasActiveRoutes[slot] = false;
                    _routeCounts[slot] = 0;
                    _routeIndices[slot] = 0;
                    world.UpdateUnitNavigation(unitId, unit.Position, unit.Position, false);
                }
                _hasActiveSearch = false;
                return;
            }

            (int col, int row) = _grid.CellCoordinates(current);
            Vector2 currentCenter = _grid.CellCenter(col, row);
            float currentClearance = _grid.GetCellClearance(col, row);
            float currentG = _gScore[current];

            if (_goalAnchorGen[current] == _searchGeneration)
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

                if (!_grid.IsInBounds(nextCol, nextRow)) continue;

                // Diagonal corner clearance: both adjacent cardinal neighbors must be passable
                if (n.isDiagonal)
                {
                    int c1Col = col + n.c1Col;
                    int c1Row = row + n.c1Row;
                    int c2Col = col + n.c2Col;
                    int c2Row = row + n.c2Row;
                    if (!_grid.IsCellPassable(c1Col, c1Row, radius) || !_grid.IsCellPassable(c2Col, c2Row, radius))
                        continue;
                }

                if (!_grid.IsCellPassable(nextCol, nextRow, radius)) continue;

                int nextCell = _grid.CellIndex(nextCol, nextRow);
                if (_closedGen[nextCell] == _searchGeneration) continue;

                float stepCost = n.isDiagonal ? 1.41421356f * _grid.CellSize : _grid.CellSize;

                // Swept-capsule edge clearance check
                float nextClearance = _grid.GetCellClearance(nextCol, nextRow);
                float safeThreshold = radius + stepCost * 0.5f;
                if (currentClearance < safeThreshold || nextClearance < safeThreshold)
                {
                    Vector2 nextCenter = _grid.CellCenter(nextCol, nextRow);
                    if (!_grid.IsSegmentClear(currentCenter, nextCenter, radius))
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
            // Open set exhausted without finding a route
            _unitStatuses[slot] = PathRequestStatus.NoPath;
            FailedSearches++;
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            world.UpdateUnitNavigation(unitId, unit.Position, unit.Position, false);
            _hasActiveSearch = false;
        }
    }

    private bool ReconstructAndApplyRoute(int slot, int endNode, Vector2 start, Vector2 target, float radius, Vector2 currentPos)
    {
        int count = 0;
        int trace = (endNode == _virtualGoalCell) ? _cameFrom[_virtualGoalCell] : endNode;
        while (trace != -1 && count < _rawPathBuffer.Length - 4)
        {
            _rawPathBuffer[count++] = _grid.CellCenter(trace);
            trace = _cameFrom[trace];
        }

        if (count == 0)
        {
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            return false;
        }

        // Reverse raw path so it starts at start cell and finishes at goal cell
        for (int i = 0; i < count / 2; i++)
        {
            (_rawPathBuffer[i], _rawPathBuffer[count - 1 - i]) = (_rawPathBuffer[count - 1 - i], _rawPathBuffer[i]);
        }

        // Append target if target is not at goal cell center
        if (Vector2.DistanceSquared(_rawPathBuffer[count - 1], target) > 1e-4f)
        {
            _rawPathBuffer[count++] = target;
        }
        else
        {
            _rawPathBuffer[count - 1] = target;
        }

        // Raycast string-pulling smoothing: skip intermediate collinear/visible waypoints
        int smoothedCount = 0;

        // Splice directly from currentPos if it can see any waypoint along raw path from goal downwards
        int visibleFromCurrent = -1;
        for (int k = count - 1; k >= 0; k--)
        {
            if (_grid.IsSegmentClear(currentPos, _rawPathBuffer[k], radius))
            {
                visibleFromCurrent = k;
                break;
            }
        }

        Vector2 currentAnchor;
        int anchorIdx;

        if (visibleFromCurrent >= 0)
        {
            currentAnchor = _rawPathBuffer[visibleFromCurrent];
            _smoothedPathBuffer[smoothedCount++] = currentAnchor;
            anchorIdx = visibleFromCurrent;
        }
        else
        {
            currentAnchor = start;
            anchorIdx = -1;
        }

        while (anchorIdx < count - 1 && smoothedCount < _tuning.MaxWaypointsPerRoute)
        {
            int farthest = -1;
            for (int nextIdx = count - 1; nextIdx > anchorIdx; nextIdx--)
            {
                if (_grid.IsSegmentClear(currentAnchor, _rawPathBuffer[nextIdx], radius))
                {
                    farthest = nextIdx;
                    break;
                }
            }

            if (farthest == -1)
            {
                break;
            }

            currentAnchor = _rawPathBuffer[farthest];
            _smoothedPathBuffer[smoothedCount++] = currentAnchor;
            anchorIdx = farthest;
        }

        bool reachedTarget = anchorIdx == count - 1 && smoothedCount > 0;
        if (!reachedTarget)
        {
            _unitHasActiveRoutes[slot] = false;
            _routeCounts[slot] = 0;
            _routeIndices[slot] = 0;
            return false;
        }

        // Copy smoothed waypoints into unit's route storage
        int offset = slot * _tuning.MaxWaypointsPerRoute;
        for (int i = 0; i < smoothedCount; i++)
        {
            _routeWaypoints[offset + i] = _smoothedPathBuffer[i];
        }
        _routeCounts[slot] = smoothedCount;
        _routeIndices[slot] = 0;
        _unitHasActiveRoutes[slot] = true;
        return true;
    }

    private void SetUnitSingleWaypointRoute(int slot, Vector2 target)
    {
        int offset = slot * _tuning.MaxWaypointsPerRoute;
        _routeWaypoints[offset] = target;
        _routeCounts[slot] = 1;
        _routeIndices[slot] = 0;
        _unitHasActiveRoutes[slot] = true;
    }

    private bool TrySnapDestination(Vector2 start, Vector2 target, float radius, out Vector2 snapped)
    {
        float maxSnapDistance = _tuning.MaxSnapDistance;
        float maxSnapDistanceSq = maxSnapDistance * maxSnapDistance;

        (int centerCol, int centerRow) = _grid.WorldToCell(target);
        int cellWindow = (int)MathF.Ceiling(maxSnapDistance / _grid.CellSize);

        float bestScore = float.MaxValue;
        Vector2 bestPoint = target;
        bool found = false;

        for (int dr = -cellWindow; dr <= cellWindow; dr++)
        {
            int r = centerRow + dr;
            if (r < 0 || r >= _grid.Rows) continue;

            for (int dc = -cellWindow; dc <= cellWindow; dc++)
            {
                int c = centerCol + dc;
                if (c < 0 || c >= _grid.Columns) continue;

                if (!_grid.IsCellPassable(c, r, radius)) continue;

                Vector2 candidate = _grid.CellCenter(c, r);
                float distSq = Vector2.DistanceSquared(target, candidate);
                if (distSq > maxSnapDistanceSq) continue;

                if (!_grid.IsPointClear(candidate, radius)) continue;

                // Candidates directly visible from unit or closer to unit receive a preference
                // so clicking an obstacle snaps to the side of the wall facing the unit.
                float score = distSq;
                if (!_grid.IsSegmentClear(start, candidate, radius)) score += 4.0f;
                score += 0.001f * Vector2.DistanceSquared(start, candidate);

                if (score < bestScore - 1e-4f)
                {
                    bestScore = score;
                    bestPoint = candidate;
                    found = true;
                }
            }
        }

        snapped = bestPoint;
        return found;
    }

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

            if (!_unitHasActiveRoutes[slot])
            {
                if (unit.Moving)
                {
                    world.UpdateUnitNavigation(unit.Id, unit.Position, unit.Position, false);
                }
                continue;
            }

            float speed = 6.0f;
            float radius = 1.0f;
            if (_catalog.TryGetProfile(unit.Definition, out UnitMovementProfile profile))
            {
                speed = profile.Speed;
                radius = profile.Radius;
            }

            float remainingDistance = speed * Simulation.TickSeconds;
            Vector2 currentPos = unit.Position;
            int offset = slot * _tuning.MaxWaypointsPerRoute;
            int count = _routeCounts[slot];
            int idx = _routeIndices[slot];

            while (remainingDistance > 1e-5f && idx < count)
            {
                Vector2 targetWaypoint = _routeWaypoints[offset + idx];
                Vector2 toWaypoint = targetWaypoint - currentPos;
                float dist = toWaypoint.Length();

                if (dist <= 1e-4f)
                {
                    idx++;
                    continue;
                }

                if (remainingDistance < dist)
                {
                    Vector2 step = toWaypoint * (remainingDistance / dist);
                    Vector2 nextPos = currentPos + step;
                    if (_grid.IsSegmentClear(currentPos, nextPos, radius))
                    {
                        currentPos = nextPos;
                        remainingDistance = 0f;
                    }
                    else
                    {
                        // Collision encountered along path: halt unit
                        idx = count;
                        remainingDistance = 0f;
                        _unitStatuses[slot] = PathRequestStatus.NoPath;
                    }
                }
                else
                {
                    // Waypoint reached: consume traveled distance and advance
                    if (_grid.IsSegmentClear(currentPos, targetWaypoint, radius))
                    {
                        currentPos = targetWaypoint;
                        remainingDistance -= dist;
                        idx++;
                    }
                    else
                    {
                        idx = count;
                        remainingDistance = 0f;
                        _unitStatuses[slot] = PathRequestStatus.NoPath;
                    }
                }
            }

            _routeIndices[slot] = idx;
            bool stillMoving = idx < count;
            _unitHasActiveRoutes[slot] = stillMoving;

            Vector2 finalTarget = _unitFinalTargets[slot];
            world.UpdateUnitNavigation(unit.Id, currentPos, finalTarget, stillMoving);
        }
    }

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

        // Tie-break 1: higher gScore first
        float gA = _gScore[cellA];
        float gB = _gScore[cellB];
        if (MathF.Abs(gA - gB) > 1e-4f) return gA > gB;

        // Tie-break 2: lower cellIndex
        return cellA < cellB;
    }

    private float Heuristic(int cell)
    {
        if (cell == _virtualGoalCell) return 0f;
        return Vector2.Distance(_grid.CellCenter(cell), _activeRequest.Target);
    }
}
