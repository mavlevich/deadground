using System.Numerics;
using System.Runtime.CompilerServices;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative Region-Portal Graph representing high-level spatial connectivity across large maps.
/// Partitions static geometry into sectors with border portals and intra-sector connected components.
/// Provides time-sliced, allocation-free global A* search with bounded work per tick.
/// </summary>
public sealed class HierarchicalNavGraph
{
    private const int MaxPortals = 8192;

    private static readonly (int dr, int dc)[] s_cardinalNeighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    private readonly NavigationGrid _grid;
    private readonly float _sectorSize;
    private readonly float _cellSize;
    private readonly int _sectorCols;
    private readonly int _sectorRows;
    private readonly int _totalSectors;

    public MovementClass MovementClass { get; }
    public float ClassRadius { get; }

    // Portal data
    private readonly Vector2[] _portalPositions;
    private readonly float[] _portalClearances;
    private readonly int[] _portalSectorA;
    private readonly int[] _portalSectorB;
    private readonly int[] _portalCompA;
    private readonly int[] _portalCompB;
    private int _portalCount;

    // Per-sector portal indices (CSR format, no fixed cap)
    private int[] _sectorPortalOffsets;
    private int[] _sectorPortalCounts;
    private int[] _sectorPortalList;

    // Intra-sector edges connecting portals sharing the same connected component
    public readonly record struct IntraEdge(int TargetPortal, float Cost, float BottleneckClearance);
    private IntraEdge[] _intraEdges;
    private readonly int[] _portalEdgeOffsets;
    private readonly int[] _portalEdgeCounts;

    // Per-sector local component maps for fast sector component query
    // Stored as flat component array per sector using ushort (up to 65534 components per sector)
    private readonly int[] _sectorCellsOffset;
    private readonly int[] _sectorGridCols;
    private readonly int[] _sectorGridRows;
    private readonly ushort[] _sectorComponentGrid;

    // Preallocated Global A* scratch buffers
    private readonly int _virtualStartNode;
    private readonly int _virtualGoalNode;
    private readonly int _totalGraphNodes;

    private readonly int[] _heap;
    private int _heapCount;
    private readonly int[] _heapPosition;
    private readonly uint[] _heapGen;
    private readonly float[] _gScore;
    private readonly int[] _cameFrom;
    private readonly uint[] _searchGen;
    private readonly uint[] _closedGen;
    private readonly int[] _portalTraceBuffer;
    private uint _globalSearchGeneration;

    // In-flight resumable global search state
    private bool _hasActiveGlobalSearch;
    private Vector2 _activeStart;
    private Vector2 _activeTarget;
    private float _activeRadius;
    private int _activeTotalExpansions;

    public int SectorCols => _sectorCols;
    public int SectorRows => _sectorRows;
    public int TotalSectors => _totalSectors;
    public float SectorSize => _sectorSize;
    public int PortalCount => _portalCount;
    public bool HasActiveGlobalSearch => _hasActiveGlobalSearch;

    public HierarchicalNavGraph(
        NavigationGrid grid,
        float sectorSize = NavigationTuning.DefaultSectorSize,
        float cellSize = NavigationTuning.DefaultCellSize)
        : this(grid, MovementClass.Tracked, sectorSize, cellSize)
    {
    }

    public HierarchicalNavGraph(
        NavigationGrid grid,
        MovementClass movementClass,
        float sectorSize = NavigationTuning.DefaultSectorSize,
        float cellSize = NavigationTuning.DefaultCellSize)
    {
        ArgumentNullException.ThrowIfNull(grid);
        _grid = grid;
        MovementClass = movementClass;
        ClassRadius = movementClass.GetClearanceRadius();
        _sectorSize = MathF.Max(10f, sectorSize);
        _cellSize = MathF.Max(0.1f, cellSize);

        _sectorCols = Math.Max(1, (int)MathF.Ceiling(grid.MapWidth / _sectorSize));
        _sectorRows = Math.Max(1, (int)MathF.Ceiling(grid.MapDepth / _sectorSize));
        _totalSectors = _sectorCols * _sectorRows;

        _portalPositions = new Vector2[MaxPortals];
        _portalClearances = new float[MaxPortals];
        _portalSectorA = new int[MaxPortals];
        _portalSectorB = new int[MaxPortals];
        _portalCompA = new int[MaxPortals];
        _portalCompB = new int[MaxPortals];

        _sectorPortalOffsets = new int[_totalSectors];
        _sectorPortalCounts = new int[_totalSectors];
        _sectorPortalList = [];

        _intraEdges = [];
        _portalEdgeOffsets = new int[MaxPortals];
        _portalEdgeCounts = new int[MaxPortals];

        _sectorCellsOffset = new int[_totalSectors];
        _sectorGridCols = new int[_totalSectors];
        _sectorGridRows = new int[_totalSectors];

        // Sizing sector component grids
        int totalSectorCells = 0;
        for (int s = 0; s < _totalSectors; s++)
        {
            var bounds = GetSectorBounds(s);
            int cols = Math.Max(1, (int)MathF.Ceiling((bounds.MaxX - bounds.MinX) / _cellSize));
            int rows = Math.Max(1, (int)MathF.Ceiling((bounds.MaxZ - bounds.MinZ) / _cellSize));
            _sectorCellsOffset[s] = totalSectorCells;
            _sectorGridCols[s] = cols;
            _sectorGridRows[s] = rows;
            totalSectorCells += cols * rows;
        }
        _sectorComponentGrid = new ushort[totalSectorCells];

        // Global A* scratch buffers
        _virtualStartNode = MaxPortals;
        _virtualGoalNode = MaxPortals + 1;
        _totalGraphNodes = MaxPortals + 2;

        _heap = new int[_totalGraphNodes];
        _heapPosition = new int[_totalGraphNodes];
        _heapGen = new uint[_totalGraphNodes];
        _gScore = new float[_totalGraphNodes];
        _cameFrom = new int[_totalGraphNodes];
        _searchGen = new uint[_totalGraphNodes];
        _closedGen = new uint[_totalGraphNodes];
        _portalTraceBuffer = new int[MaxPortals];

        // Build sectors, portals, components and intra-edges
        BuildGraph();
    }

    public (float MinX, float MinZ, float MaxX, float MaxZ) GetSectorBounds(int sectorIndex)
    {
        int sx = sectorIndex % _sectorCols;
        int sy = sectorIndex / _sectorCols;
        float minX = sx * _sectorSize;
        float minZ = sy * _sectorSize;
        float maxX = MathF.Min(_grid.MapWidth, (sx + 1) * _sectorSize);
        float maxZ = MathF.Min(_grid.MapDepth, (sy + 1) * _sectorSize);
        return (minX, minZ, maxX, maxZ);
    }

    public int GetSectorIndex(Vector2 position)
    {
        int sx = Math.Clamp((int)(position.X / _sectorSize), 0, _sectorCols - 1);
        int sy = Math.Clamp((int)(position.Y / _sectorSize), 0, _sectorRows - 1);
        return sy * _sectorCols + sx;
    }

    private void BuildGraph()
    {
        // 1. Build local sector component grids
        BuildSectorComponents();

        // 2. Discover portals along vertical and horizontal sector boundaries
        DiscoverPortals();

        // 3. Assign portal component memberships in adjacent sectors
        AssignPortalComponents();

        // 4. Construct intra-sector edges between portals sharing the same component
        BuildIntraSectorEdges();
    }

    private void BuildSectorComponents()
    {
        // Temporary BFS queue for flood filling components within a sector
        int maxSectorCells = 0;
        for (int s = 0; s < _totalSectors; s++)
        {
            int cellCount = _sectorGridCols[s] * _sectorGridRows[s];
            if (cellCount > maxSectorCells) maxSectorCells = cellCount;
        }

        int[] bfsQueue = new int[maxSectorCells];
        int[] sectorObstacles = new int[Math.Min(1024, _grid.GroundedObstacleCount)];

        for (int s = 0; s < _totalSectors; s++)
        {
            var bounds = GetSectorBounds(s);
            int cols = _sectorGridCols[s];
            int rows = _sectorGridRows[s];
            int baseOffset = _sectorCellsOffset[s];
            int cellCount = cols * rows;

            // Fast path: if interior sector has no obstacles, the entire sector is one connected component (1)
            bool isMapBoundarySector = bounds.MinX < ClassRadius || bounds.MaxX > _grid.MapWidth - ClassRadius ||
                                       bounds.MinZ < ClassRadius || bounds.MaxZ > _grid.MapDepth - ClassRadius;

            if (!isMapBoundarySector && !_grid.HasObstaclesInBox(bounds.MinX - ClassRadius, bounds.MinZ - ClassRadius, bounds.MaxX + ClassRadius, bounds.MaxZ + ClassRadius))
            {
                Array.Fill(_sectorComponentGrid, (ushort)1, baseOffset, cellCount);
                continue;
            }

            int localObsCount = _grid.QueryObstacles(
                bounds.MinX - ClassRadius, bounds.MinZ - ClassRadius,
                bounds.MaxX + ClassRadius, bounds.MaxZ + ClassRadius,
                sectorObstacles);

            // Mark passable (0) vs impassable (ushort.MaxValue)
            // Account for grid-cell discretization offset (up to half a cell) so corridors of intended clearance connect
            float effectiveRadius = MathF.Max(0.2f, ClassRadius - _cellSize * 0.5f);
            float radiusSq = (effectiveRadius - 1e-4f) * (effectiveRadius - 1e-4f);

            Array.Clear(_sectorComponentGrid, baseOffset, cellCount);

            if (isMapBoundarySector)
            {
                for (int r = 0; r < rows; r++)
                {
                    float cz = bounds.MinZ + (r + 0.5f) * _cellSize;
                    int rowIdx = baseOffset + r * cols;
                    for (int c = 0; c < cols; c++)
                    {
                        float cx = bounds.MinX + (c + 0.5f) * _cellSize;
                        if (cx < ClassRadius || cx > _grid.MapWidth - ClassRadius ||
                            cz < ClassRadius || cz > _grid.MapDepth - ClassRadius)
                        {
                            _sectorComponentGrid[rowIdx + c] = ushort.MaxValue;
                        }
                    }
                }
            }

            for (int o = 0; o < localObsCount; o++)
            {
                ref readonly NavigationGrid.GroundedObstacle obs = ref _grid.GroundedObstacles[sectorObstacles[o]];
                int minC = Math.Clamp((int)((obs.MinX - effectiveRadius - bounds.MinX) / _cellSize), 0, cols - 1);
                int maxC = Math.Clamp((int)((obs.MaxX + effectiveRadius - bounds.MinX) / _cellSize), 0, cols - 1);
                int minR = Math.Clamp((int)((obs.MinZ - effectiveRadius - bounds.MinZ) / _cellSize), 0, rows - 1);
                int maxR = Math.Clamp((int)((obs.MaxZ + effectiveRadius - bounds.MinZ) / _cellSize), 0, rows - 1);

                for (int r = minR; r <= maxR; r++)
                {
                    float cz = bounds.MinZ + (r + 0.5f) * _cellSize;
                    int rowIdx = baseOffset + r * cols;
                    for (int c = minC; c <= maxC; c++)
                    {
                        if (_sectorComponentGrid[rowIdx + c] == ushort.MaxValue) continue;
                        float cx = bounds.MinX + (c + 0.5f) * _cellSize;

                        if (cx >= obs.MinX && cx <= obs.MaxX && cz >= obs.MinZ && cz <= obs.MaxZ)
                        {
                            _sectorComponentGrid[rowIdx + c] = ushort.MaxValue;
                            continue;
                        }

                        float dx = MathF.Max(0f, MathF.Max(obs.MinX - cx, cx - obs.MaxX));
                        float dz = MathF.Max(0f, MathF.Max(obs.MinZ - cz, cz - obs.MaxZ));
                        if (dx * dx + dz * dz < radiusSq)
                        {
                            _sectorComponentGrid[rowIdx + c] = ushort.MaxValue;
                        }
                    }
                }
            }

            // BFS flood fill to label connected components (1, 2, 3...)
            ushort currentComponent = 1;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = baseOffset + r * cols + c;
                    if (_sectorComponentGrid[idx] != 0) continue; // Already labeled or impassable

                    int head = 0;
                    int tail = 0;
                    bfsQueue[tail++] = (r << 16) | c;
                    _sectorComponentGrid[idx] = currentComponent;

                    while (head < tail)
                    {
                        int packed = bfsQueue[head++];
                        int cr = packed >> 16;
                        int cc = packed & 0xFFFF;

                        for (int i = 0; i < s_cardinalNeighbors.Length; i++)
                        {
                            int nr = cr + s_cardinalNeighbors[i].dr;
                            int nc = cc + s_cardinalNeighbors[i].dc;
                            if (nr < 0 || nr >= rows || nc < 0 || nc >= cols) continue;

                            int nIdx = baseOffset + nr * cols + nc;
                            if (_sectorComponentGrid[nIdx] == 0)
                            {
                                _sectorComponentGrid[nIdx] = currentComponent;
                                bfsQueue[tail++] = (nr << 16) | nc;
                            }
                        }
                    }

                    if (currentComponent == ushort.MaxValue - 1)
                    {
                        throw new InvalidOperationException($"Sector {s} exceeded maximum supported connected components ({ushort.MaxValue - 1}).");
                    }
                    currentComponent++;
                }
            }
        }
    }

    private void DiscoverPortals()
    {
        _portalCount = 0;
        List<int>[] sectorPortals = new List<int>[_totalSectors];
        for (int s = 0; s < _totalSectors; s++) sectorPortals[s] = new List<int>();

        // Sample interval along boundary
        float sampleStep = MathF.Max(0.5f, _cellSize);

        // 1. Vertical boundaries: between (sx, sy) and (sx + 1, sy)
        for (int sy = 0; sy < _sectorRows; sy++)
        {
            for (int sx = 0; sx < _sectorCols - 1; sx++)
            {
                int sA = sy * _sectorCols + sx;
                int sB = sy * _sectorCols + (sx + 1);

                var boundsA = GetSectorBounds(sA);
                var boundsB = GetSectorBounds(sB);
                float xBorder = boundsA.MaxX;
                float zStart = MathF.Max(boundsA.MinZ, boundsB.MinZ);
                float zEnd = MathF.Min(boundsA.MaxZ, boundsB.MaxZ);

                ExtractPortalsAlongSegment(
                    new Vector2(xBorder, zStart),
                    new Vector2(xBorder, zEnd),
                    sA, sB, sampleStep, sectorPortals);
            }
        }

        // 2. Horizontal boundaries: between (sx, sy) and (sx, sy + 1)
        for (int sy = 0; sy < _sectorRows - 1; sy++)
        {
            for (int sx = 0; sx < _sectorCols; sx++)
            {
                int sA = sy * _sectorCols + sx;
                int sB = (sy + 1) * _sectorCols + sx;

                var boundsA = GetSectorBounds(sA);
                var boundsB = GetSectorBounds(sB);
                float zBorder = boundsA.MaxZ;
                float xStart = MathF.Max(boundsA.MinX, boundsB.MinX);
                float xEnd = MathF.Min(boundsA.MaxX, boundsB.MaxX);

                ExtractPortalsAlongSegment(
                    new Vector2(xStart, zBorder),
                    new Vector2(xEnd, zBorder),
                    sA, sB, sampleStep, sectorPortals);
            }
        }

        int totalSectorPortals = 0;
        for (int s = 0; s < _totalSectors; s++)
        {
            _sectorPortalOffsets[s] = totalSectorPortals;
            _sectorPortalCounts[s] = sectorPortals[s].Count;
            totalSectorPortals += sectorPortals[s].Count;
        }

        _sectorPortalList = new int[totalSectorPortals];
        for (int s = 0; s < _totalSectors; s++)
        {
            var list = sectorPortals[s];
            int offset = _sectorPortalOffsets[s];
            for (int i = 0; i < list.Count; i++)
            {
                _sectorPortalList[offset + i] = list[i];
            }
        }
    }

    private void ExtractPortalsAlongSegment(
        Vector2 start,
        Vector2 end,
        int sectorA,
        int sectorB,
        float step,
        List<int>[] sectorPortals)
    {
        Vector2 dir = end - start;
        float len = dir.Length();
        if (len < step) return;
        Vector2 normalDir = dir / len;

        var boundsA = GetSectorBounds(sectorA);
        var boundsB = GetSectorBounds(sectorB);
        bool hasObsA = _grid.HasObstaclesInBox(boundsA.MinX - ClassRadius, boundsA.MinZ - ClassRadius, boundsA.MaxX + ClassRadius, boundsA.MaxZ + ClassRadius);
        bool hasObsB = _grid.HasObstaclesInBox(boundsB.MinX - ClassRadius, boundsB.MinZ - ClassRadius, boundsB.MaxX + ClassRadius, boundsB.MaxZ + ClassRadius);

        if (!hasObsA && !hasObsB)
        {
            if (_portalCount >= MaxPortals)
            {
                throw new InvalidOperationException($"HierarchicalNavGraph exceeded maximum supported portals ({MaxPortals}).");
            }

            int pIdx = _portalCount++;
            Vector2 portalPos = (start + end) * 0.5f;
            _portalPositions[pIdx] = portalPos;
            _portalClearances[pIdx] = len * 0.5f;
            _portalSectorA[pIdx] = sectorA;
            _portalSectorB[pIdx] = sectorB;
            _portalCompA[pIdx] = -1;
            _portalCompB[pIdx] = -1;

            sectorPortals[sectorA].Add(pIdx);
            sectorPortals[sectorB].Add(pIdx);
            return;
        }

        int samples = (int)MathF.Floor(len / step);
        bool inOpening = false;
        float openingStartDist = 0f;
        float bestClearance = 0f;
        float bestDist = 0f;

        float effectiveThreshold = MathF.Max(0.2f, ClassRadius - step * 0.5f);

        for (int i = 0; i <= samples; i++)
        {
            float d = MathF.Min(len, i * step);
            Vector2 samplePos = start + normalDir * d;

            float clearance = ComputePointClearance(samplePos);
            bool inClearTerrain = clearance > 0.05f;

            if (inClearTerrain)
            {
                if (!inOpening)
                {
                    inOpening = true;
                    openingStartDist = d;
                    bestClearance = clearance;
                    bestDist = d;
                }
                else if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    bestDist = d;
                }
            }

            if (!inClearTerrain || i == samples)
            {
                if (inOpening)
                {
                    float width = d - openingStartDist;
                    if (bestClearance >= effectiveThreshold)
                    {
                        if (_portalCount >= MaxPortals)
                        {
                            throw new InvalidOperationException($"HierarchicalNavGraph exceeded maximum supported portals ({MaxPortals}).");
                        }

                        int pIdx = _portalCount++;
                        Vector2 portalPos = start + normalDir * bestDist;
                        _portalPositions[pIdx] = portalPos;
                        _portalClearances[pIdx] = MathF.Max(bestClearance, width * 0.5f);
                        _portalSectorA[pIdx] = sectorA;
                        _portalSectorB[pIdx] = sectorB;
                        _portalCompA[pIdx] = -1;
                        _portalCompB[pIdx] = -1;

                        sectorPortals[sectorA].Add(pIdx);
                        sectorPortals[sectorB].Add(pIdx);
                    }
                    inOpening = false;
                }
            }
        }
    }

    private void AssignPortalComponents()
    {
        for (int p = 0; p < _portalCount; p++)
        {
            Vector2 pos = _portalPositions[p];
            int sA = _portalSectorA[p];
            int sB = _portalSectorB[p];

            _portalCompA[p] = FindClosestComponentInSector(sA, pos);
            _portalCompB[p] = FindClosestComponentInSector(sB, pos);
        }
    }

    private int FindClosestComponentInSector(int sector, Vector2 point)
    {
        var bounds = GetSectorBounds(sector);
        int cols = _sectorGridCols[sector];
        int rows = _sectorGridRows[sector];
        int baseOffset = _sectorCellsOffset[sector];

        int c = Math.Clamp((int)((point.X - bounds.MinX) / _cellSize), 0, cols - 1);
        int r = Math.Clamp((int)((point.Y - bounds.MinZ) / _cellSize), 0, rows - 1);

        ushort directComp = _sectorComponentGrid[baseOffset + r * cols + c];
        if (directComp > 0 && directComp != ushort.MaxValue) return directComp;

        // Search small window for closest clear component cell
        int bestComp = -1;
        float bestDistSq = float.MaxValue;

        for (int dr = -4; dr <= 4; dr++)
        {
            int nr = r + dr;
            if (nr < 0 || nr >= rows) continue;
            for (int dc = -4; dc <= 4; dc++)
            {
                int nc = c + dc;
                if (nc < 0 || nc >= cols) continue;

                ushort comp = _sectorComponentGrid[baseOffset + nr * cols + nc];
                if (comp > 0 && comp != ushort.MaxValue)
                {
                    float cx = bounds.MinX + (nc + 0.5f) * _cellSize;
                    float cz = bounds.MinZ + (nr + 0.5f) * _cellSize;
                    float dSq = Vector2.DistanceSquared(point, new Vector2(cx, cz));
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestComp = comp;
                    }
                }
            }
        }

        return bestComp;
    }

    private void BuildIntraSectorEdges()
    {
        List<IntraEdge> edges = new();

        for (int p = 0; p < _portalCount; p++)
        {
            _portalEdgeOffsets[p] = edges.Count;
            int startCount = edges.Count;

            int sA = _portalSectorA[p];
            int compA = _portalCompA[p];
            int sB = _portalSectorB[p];
            int compB = _portalCompB[p];

            // 1. Connect across sector boundary to the other sector's portals
            // In sector A
            ConnectPortalsInSector(p, sA, compA, edges);

            // In sector B
            ConnectPortalsInSector(p, sB, compB, edges);

            _portalEdgeCounts[p] = edges.Count - startCount;
        }

        _intraEdges = edges.ToArray();
    }

    private void ConnectPortalsInSector(int sourcePortal, int sector, int component, List<IntraEdge> edges)
    {
        if (component <= 0) return;

        int offset = _sectorPortalOffsets[sector];
        int count = _sectorPortalCounts[sector];

        for (int i = 0; i < count; i++)
        {
            int targetPortal = _sectorPortalList[offset + i];
            if (targetPortal == sourcePortal) continue;

            int targetComp = (_portalSectorA[targetPortal] == sector)
                ? _portalCompA[targetPortal]
                : _portalCompB[targetPortal];

            if (targetComp == component)
            {
                // Portals belong to the same connected component within this sector!
                float dist = Vector2.Distance(_portalPositions[sourcePortal], _portalPositions[targetPortal]);
                float bottleneck = MathF.Min(_portalClearances[sourcePortal], _portalClearances[targetPortal]);

                edges.Add(new IntraEdge(targetPortal, dist, bottleneck));
            }
        }
    }

    public int GetComponentId(Vector2 point, float radius)
    {
        int sector = GetSectorIndex(point);
        return FindClosestComponentInSector(sector, point);
    }

    private float ComputePointClearance(Vector2 point) =>
        _grid.ComputePointClearance(point);

    public void StartGlobalSearch(Vector2 start, Vector2 target, float radius)
    {
        _globalSearchGeneration++;
        _heapCount = 0;
        _activeStart = start;
        _activeTarget = target;
        _activeRadius = radius;
        _activeTotalExpansions = 0;

        int startSector = GetSectorIndex(start);
        int targetSector = GetSectorIndex(target);
        int startComp = FindClosestComponentInSector(startSector, start);
        int targetComp = FindClosestComponentInSector(targetSector, target);

        if (startComp <= 0 || targetComp <= 0)
        {
            _hasActiveGlobalSearch = false;
            return;
        }

        // Initialize virtual goal node
        _searchGen[_virtualGoalNode] = _globalSearchGeneration;
        _gScore[_virtualGoalNode] = float.PositiveInfinity;
        _cameFrom[_virtualGoalNode] = -1;
        _heapPosition[_virtualGoalNode] = -1;

        // Connect virtual start to portals in start sector with matching component and clearance
        int startOffset = _sectorPortalOffsets[startSector];
        int startCount = _sectorPortalCounts[startSector];
        int connectedPortals = 0;

        for (int i = 0; i < startCount; i++)
        {
            int p = _sectorPortalList[startOffset + i];
            int pComp = (_portalSectorA[p] == startSector) ? _portalCompA[p] : _portalCompB[p];

            if (pComp == startComp && _portalClearances[p] >= radius)
            {
                float cost = Vector2.Distance(start, _portalPositions[p]);
                _searchGen[p] = _globalSearchGeneration;
                _gScore[p] = cost;
                _cameFrom[p] = _virtualStartNode;
                HeapPush(p);
                connectedPortals++;
            }
        }

        _hasActiveGlobalSearch = connectedPortals > 0;
    }

    public PathRequestStatus ResumeGlobalSearch(
        int maxExpansions,
        int maxTotalExpansions,
        out int expansionsUsed,
        Span<Vector2> outPortals,
        out int portalCount)
    {
        expansionsUsed = 0;
        portalCount = 0;

        if (!_hasActiveGlobalSearch)
        {
            return PathRequestStatus.NoPath;
        }

        int targetSector = GetSectorIndex(_activeTarget);
        int targetComp = FindClosestComponentInSector(targetSector, _activeTarget);

        while (_heapCount > 0 && expansionsUsed < maxExpansions)
        {
            if (_activeTotalExpansions >= maxTotalExpansions)
            {
                _hasActiveGlobalSearch = false;
                return PathRequestStatus.CapacityExceeded;
            }

            int current = HeapPop();
            if (_closedGen[current] == _globalSearchGeneration) continue;
            _closedGen[current] = _globalSearchGeneration;

            expansionsUsed++;
            _activeTotalExpansions++;

            if (current == _virtualGoalNode)
            {
                portalCount = ReconstructPortalPlan(outPortals);
                _hasActiveGlobalSearch = false;
                return PathRequestStatus.PathFound;
            }

            Vector2 currentPos = _portalPositions[current];
            float currentG = _gScore[current];

            // If current portal is in target sector with matching component, evaluate connection to virtual goal
            int curSectorA = _portalSectorA[current];
            int curSectorB = _portalSectorB[current];

            bool reachesTargetComp =
                (curSectorA == targetSector && _portalCompA[current] == targetComp) ||
                (curSectorB == targetSector && _portalCompB[current] == targetComp);

            if (reachesTargetComp)
            {
                float distToTarget = Vector2.Distance(currentPos, _activeTarget);
                float tentativeGoalG = currentG + distToTarget;

                if (_searchGen[_virtualGoalNode] != _globalSearchGeneration)
                {
                    _searchGen[_virtualGoalNode] = _globalSearchGeneration;
                    _gScore[_virtualGoalNode] = float.PositiveInfinity;
                }

                if (tentativeGoalG < _gScore[_virtualGoalNode] - 1e-4f)
                {
                    _gScore[_virtualGoalNode] = tentativeGoalG;
                    _cameFrom[_virtualGoalNode] = current;
                    if (!IsInHeap(_virtualGoalNode))
                    {
                        HeapPush(_virtualGoalNode);
                    }
                    else
                    {
                        HeapDecreaseKey(_virtualGoalNode);
                    }
                }
            }

            // Expand intra-sector edges
            int edgeOffset = _portalEdgeOffsets[current];
            int edgeCount = _portalEdgeCounts[current];

            for (int i = 0; i < edgeCount; i++)
            {
                IntraEdge edge = _intraEdges[edgeOffset + i];
                if (edge.BottleneckClearance < _activeRadius) continue;

                int nextPortal = edge.TargetPortal;
                if (_closedGen[nextPortal] == _globalSearchGeneration) continue;

                float tentativeG = currentG + edge.Cost;

                if (_searchGen[nextPortal] != _globalSearchGeneration)
                {
                    _searchGen[nextPortal] = _globalSearchGeneration;
                    _gScore[nextPortal] = float.PositiveInfinity;
                }

                if (tentativeG < _gScore[nextPortal] - 1e-4f)
                {
                    _gScore[nextPortal] = tentativeG;
                    _cameFrom[nextPortal] = current;
                    if (!IsInHeap(nextPortal))
                    {
                        HeapPush(nextPortal);
                    }
                    else
                    {
                        HeapDecreaseKey(nextPortal);
                    }
                }
            }
        }

        if (_heapCount == 0 && _hasActiveGlobalSearch)
        {
            _hasActiveGlobalSearch = false;
            return PathRequestStatus.NoPath;
        }

        return PathRequestStatus.Searching;
    }

    private int ReconstructPortalPlan(Span<Vector2> outPortals)
    {
        int trace = _cameFrom[_virtualGoalNode];
        int count = 0;

        while (trace != _virtualStartNode && trace >= 0 && trace < _portalCount && count < _portalTraceBuffer.Length)
        {
            _portalTraceBuffer[count++] = trace;
            trace = _cameFrom[trace];
        }

        int written = 0;
        Vector2 lastPos = _activeStart;
        for (int i = count - 1; i >= 0 && written < outPortals.Length; i--)
        {
            Vector2 pos = _portalPositions[_portalTraceBuffer[i]];
            if (Vector2.DistanceSquared(pos, lastPos) > 1.0f && Vector2.DistanceSquared(pos, _activeTarget) > 1.0f)
            {
                outPortals[written++] = pos;
                lastPos = pos;
            }
        }

        return written;
    }

    public void CancelGlobalSearch()
    {
        _hasActiveGlobalSearch = false;
    }

    // Min-heap indexed operations
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInHeap(int node) =>
        _heapGen[node] == _globalSearchGeneration && _heapPosition[node] >= 0;

    private void HeapPush(int node)
    {
        int i = _heapCount++;
        _heap[i] = node;
        _heapPosition[node] = i;
        _heapGen[node] = _globalSearchGeneration;
        BubbleUp(i);
    }

    private void HeapDecreaseKey(int node)
    {
        if (IsInHeap(node))
        {
            BubbleUp(_heapPosition[node]);
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
        int nodeI = _heap[i];
        int nodeJ = _heap[j];
        _heap[i] = nodeJ;
        _heap[j] = nodeI;
        _heapPosition[nodeI] = j;
        _heapPosition[nodeJ] = i;
    }

    private bool ComesBefore(int nodeA, int nodeB)
    {
        float fA = _gScore[nodeA] + Heuristic(nodeA);
        float fB = _gScore[nodeB] + Heuristic(nodeB);
        if (MathF.Abs(fA - fB) > 1e-4f) return fA < fB;

        float gA = _gScore[nodeA];
        float gB = _gScore[nodeB];
        if (MathF.Abs(gA - gB) > 1e-4f) return gA > gB;

        return nodeA < nodeB;
    }

    private float Heuristic(int node)
    {
        if (node == _virtualGoalNode) return 0f;
        return Vector2.Distance(_portalPositions[node], _activeTarget);
    }
}
