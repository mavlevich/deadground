using System.Collections.ObjectModel;
using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Diagnostic counters for a line-of-sight query execution.
/// </summary>
public readonly struct LineOfSightDiagnostics
{
    public int VisitedTerrainCells { get; }
    public int VisitedStructureCells { get; }
    public int TestedStructures { get; }

    public LineOfSightDiagnostics(int visitedTerrainCells, int visitedStructureCells, int testedStructures)
    {
        VisitedTerrainCells = visitedTerrainCells;
        VisitedStructureCells = visitedStructureCells;
        TestedStructures = testedStructures;
    }
}

/// <summary>
/// Immutable static map geometry service combining continuous bilinear terrain,
/// opaque axis-aligned structures, and an acceleration uniform spatial grid.
///
/// Designed to execute 3D line-of-sight queries completely independent of Godot,
/// physics engines, or dynamic simulation entities, with zero allocations per query.
/// </summary>
public sealed class StaticGeometry
{
    internal const float GroundTolerance = 0.01f;
    internal const float EndContactTolerance = 0.05f;
    internal const float VolumeEpsilon = 1e-6f;
    internal const float WorldPenetrationEpsilon = 1e-4f;

    [ThreadStatic]
    private static LineOfSightContext? t_defaultContext;

    private readonly OpaqueStructure[] _structures;
    private readonly ReadOnlyCollection<OpaqueStructure> _readOnlyStructures;
    private readonly int[] _cellOffsets;
    private readonly int[] _structureIndices;
    private readonly int _gridColumns;
    private readonly int _gridRows;

    public HeightMap HeightMap { get; }
    public IReadOnlyList<OpaqueStructure> Structures => _readOnlyStructures;
    public float StructureCellSize { get; }

    public StaticGeometry(HeightMap heightMap, ReadOnlySpan<OpaqueStructure> structures, float structureCellSize = 16.0f)
    {
        ArgumentNullException.ThrowIfNull(heightMap);
        if (!float.IsFinite(structureCellSize) || structureCellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(structureCellSize), "Structure cell size must be positive and finite.");

        HeightMap = heightMap;
        StructureCellSize = structureCellSize;

        // Defensive copy to ensure immutability
        _structures = new OpaqueStructure[structures.Length];
        HashSet<StructureId> seenIds = new();

        for (int i = 0; i < structures.Length; i++)
        {
            OpaqueStructure s = structures[i] ?? throw new ArgumentException($"Structure at index {i} is null.");

            if (!seenIds.Add(s.Id))
                throw new ArgumentException($"Duplicate structure ID detected: {s.Id}.");

            if (s.MinX < 0 || s.MaxX > heightMap.Width ||
                s.MinZ < 0 || s.MaxZ > heightMap.Depth)
            {
                throw new ArgumentOutOfRangeException(nameof(structures),
                    $"Structure {s.Id} ({s.Min} .. {s.Max}) exceeds map horizontal bounds [0..{heightMap.Width}, 0..{heightMap.Depth}].");
            }

            if (s.IsGrounded)
            {
                float minTerrainY = float.PositiveInfinity;
                minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(s.MinX, s.MinZ));
                minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(s.MaxX, s.MinZ));
                minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(s.MinX, s.MaxZ));
                minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(s.MaxX, s.MaxZ));

                int minCol = Math.Clamp((int)MathF.Ceiling(s.MinX / heightMap.CellSize), 0, heightMap.Columns - 1);
                int maxCol = Math.Clamp((int)MathF.Floor(s.MaxX / heightMap.CellSize), 0, heightMap.Columns - 1);
                int minRow = Math.Clamp((int)MathF.Ceiling(s.MinZ / heightMap.CellSize), 0, heightMap.Rows - 1);
                int maxRow = Math.Clamp((int)MathF.Floor(s.MaxZ / heightMap.CellSize), 0, heightMap.Rows - 1);

                // Footprint edge intersections with grid columns
                for (int c = minCol; c <= maxCol; c++)
                {
                    float x = c * heightMap.CellSize;
                    if (x > s.MinX && x < s.MaxX)
                    {
                        minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(x, s.MinZ));
                        minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(x, s.MaxZ));
                    }
                }

                // Footprint edge intersections with grid rows
                for (int r = minRow; r <= maxRow; r++)
                {
                    float z = r * heightMap.CellSize;
                    if (z > s.MinZ && z < s.MaxZ)
                    {
                        minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(s.MinX, z));
                        minTerrainY = Math.Min(minTerrainY, heightMap.GetHeight(s.MaxX, z));
                    }
                }

                // Interior grid vertices
                for (int r = minRow; r <= maxRow; r++)
                {
                    for (int c = minCol; c <= maxCol; c++)
                    {
                        minTerrainY = Math.Min(minTerrainY, heightMap.SampleHeight(c, r));
                    }
                }

                if (s.MinY > minTerrainY + GroundTolerance)
                {
                    throw new ArgumentException(
                        $"Grounded structure {s.Id} MinY ({s.MinY:F3}) must not be higher than lowest terrain point under footprint ({minTerrainY:F3}).");
                }
            }

            _structures[i] = s;
        }

        // Build 2D uniform grid spatial index
        _gridColumns = Math.Max(1, (int)MathF.Ceiling(heightMap.Width / structureCellSize));
        _gridRows = Math.Max(1, (int)MathF.Ceiling(heightMap.Depth / structureCellSize));
        int totalCells = checked(_gridColumns * _gridRows);

        int[] cellCounts = new int[totalCells];
        for (int i = 0; i < _structures.Length; i++)
        {
            OpaqueStructure s = _structures[i];
            int minCx = Math.Clamp((int)MathF.Floor(s.MinX / structureCellSize), 0, _gridColumns - 1);
            int maxCx = Math.Clamp((int)MathF.Floor(s.MaxX / structureCellSize), 0, _gridColumns - 1);
            int minCz = Math.Clamp((int)MathF.Floor(s.MinZ / structureCellSize), 0, _gridRows - 1);
            int maxCz = Math.Clamp((int)MathF.Floor(s.MaxZ / structureCellSize), 0, _gridRows - 1);

            for (int cz = minCz; cz <= maxCz; cz++)
            {
                int row = cz * _gridColumns;
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    cellCounts[row + cx]++;
                }
            }
        }

        _cellOffsets = new int[totalCells + 1];
        int totalRefs = 0;
        for (int i = 0; i < totalCells; i++)
        {
            _cellOffsets[i] = totalRefs;
            totalRefs = checked(totalRefs + cellCounts[i]);
        }
        _cellOffsets[totalCells] = totalRefs;

        _structureIndices = new int[totalRefs];
        int[] insertCursors = (int[])_cellOffsets.Clone();

        for (int i = 0; i < _structures.Length; i++)
        {
            OpaqueStructure s = _structures[i];
            int minCx = Math.Clamp((int)MathF.Floor(s.MinX / structureCellSize), 0, _gridColumns - 1);
            int maxCx = Math.Clamp((int)MathF.Floor(s.MaxX / structureCellSize), 0, _gridColumns - 1);
            int minCz = Math.Clamp((int)MathF.Floor(s.MinZ / structureCellSize), 0, _gridRows - 1);
            int maxCz = Math.Clamp((int)MathF.Floor(s.MaxZ / structureCellSize), 0, _gridRows - 1);

            for (int cz = minCz; cz <= maxCz; cz++)
            {
                int row = cz * _gridColumns;
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    int cellIndex = row + cx;
                    _structureIndices[insertCursors[cellIndex]++] = i;
                }
            }
        }

        _readOnlyStructures = Array.AsReadOnly(_structures);
    }

    public StaticGeometry(HeightMap heightMap, IEnumerable<OpaqueStructure> structures, float structureCellSize = 16.0f)
        : this(heightMap, (structures ?? throw new ArgumentNullException(nameof(structures))).ToArray().AsSpan(), structureCellSize)
    {
    }

    private static LineOfSightContext GetThreadContext(int structureCount)
    {
        t_defaultContext ??= new LineOfSightContext(structureCount);
        t_defaultContext.EnsureCapacity(structureCount);
        return t_defaultContext;
    }

    /// <summary>
    /// Checks line of sight between two 3D world coordinates.
    /// Allocation-free value-type query.
    /// </summary>
    public LineOfSightResult CheckLineOfSight(Vector3 start, Vector3 end) =>
        CheckLineOfSight(start, end, null, out _);

    /// <summary>
    /// Checks line of sight between two 3D world coordinates using a caller-provided context.
    /// </summary>
    public LineOfSightResult CheckLineOfSight(Vector3 start, Vector3 end, LineOfSightContext? context) =>
        CheckLineOfSight(start, end, context, out _);

    /// <summary>
    /// Checks line of sight and outputs traversal diagnostic metrics.
    /// </summary>
    public LineOfSightResult CheckLineOfSight(Vector3 start, Vector3 end, out LineOfSightDiagnostics diagnostics) =>
        CheckLineOfSight(start, end, null, out diagnostics);

    /// <summary>
    /// Checks line of sight with diagnostic metrics using an optional caller-provided context.
    /// </summary>
    public LineOfSightResult CheckLineOfSight(
        Vector3 start,
        Vector3 end,
        LineOfSightContext? context,
        out LineOfSightDiagnostics diagnostics)
    {
        int visitedTerrainCells = 0;
        int visitedStructureCells = 0;
        int testedStructures = 0;

        if (TryQueryPrologue(start, end, out Vector3 delta, out float totalDistance,
            out LineOfSightResult immediateResult, out float closestT, out ObstructionKind closestKind, ref visitedTerrainCells))
        {
            diagnostics = new LineOfSightDiagnostics(0, 0, 0);
            return immediateResult;
        }

        StructureId closestStructureId = default;

        if (TryTraceStructures(start, delta, totalDistance, closestT, context, out float structHitT, out StructureId hitStructId,
            ref visitedStructureCells, ref testedStructures))
        {
            if (structHitT <= closestT + VolumeEpsilon)
            {
                closestT = structHitT;
                closestKind = ObstructionKind.Structure;
                closestStructureId = hitStructId;
            }
        }

        diagnostics = new LineOfSightDiagnostics(visitedTerrainCells, visitedStructureCells, testedStructures);

        if (closestKind == ObstructionKind.None)
        {
            return LineOfSightResult.Clear(end, totalDistance);
        }

        Vector3 hitPoint = start + delta * closestT;
        float hitDistance = closestT * totalDistance;

        return closestKind == ObstructionKind.Terrain
            ? LineOfSightResult.BlockedByTerrain(hitPoint, hitDistance, closestT)
            : LineOfSightResult.BlockedByStructure(closestStructureId, hitPoint, hitDistance, closestT);
    }

    /// <summary>
    /// Exhaustive brute-force search over all structures. Used as an internal test oracle.
    /// </summary>
    internal LineOfSightResult CheckLineOfSightBruteForce(Vector3 start, Vector3 end)
    {
        int dummy = 0;
        if (TryQueryPrologue(start, end, out Vector3 delta, out float totalDistance,
            out LineOfSightResult immediateResult, out float closestT, out ObstructionKind closestKind, ref dummy))
        {
            return immediateResult;
        }

        bool foundStructHit = false;
        StructureId bestStructId = default;
        float bestStructT = closestT;

        for (int i = 0; i < _structures.Length; i++)
        {
            OpaqueStructure s = _structures[i];
            if (TryIntersectBox(s, start, delta, totalDistance, out float tNear))
            {
                if (IsBetterHit(tNear, s.Id, bestStructT, bestStructId, foundStructHit))
                {
                    bestStructT = tNear;
                    bestStructId = s.Id;
                    foundStructHit = true;
                }
            }
        }

        StructureId closestStructureId = default;
        if (foundStructHit && (closestKind == ObstructionKind.None || bestStructT <= closestT + VolumeEpsilon))
        {
            closestT = bestStructT;
            closestKind = ObstructionKind.Structure;
            closestStructureId = bestStructId;
        }

        if (closestKind == ObstructionKind.None)
            return LineOfSightResult.Clear(end, totalDistance);

        Vector3 hitPoint = start + delta * closestT;
        float hitDistance = closestT * totalDistance;

        return closestKind == ObstructionKind.Terrain
            ? LineOfSightResult.BlockedByTerrain(hitPoint, hitDistance, closestT)
            : LineOfSightResult.BlockedByStructure(closestStructureId, hitPoint, hitDistance, closestT);
    }

    private bool TryQueryPrologue(
        Vector3 start,
        Vector3 end,
        out Vector3 delta,
        out float totalDistance,
        out LineOfSightResult immediateResult,
        out float closestT,
        out ObstructionKind closestKind,
        ref int visitedTerrainCells)
    {
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(start.Z) ||
            !float.IsFinite(end.X) || !float.IsFinite(end.Y) || !float.IsFinite(end.Z))
        {
            throw new ArgumentException("Start and end coordinates must be finite values.");
        }

        delta = end - start;
        totalDistance = delta.Length();
        closestT = 1.0f;
        closestKind = ObstructionKind.None;

        if (totalDistance < 1e-7f)
        {
            immediateResult = EvaluateCoincidentPoint(start);
            return true;
        }

        if (IsInsideTerrain(start))
        {
            immediateResult = LineOfSightResult.BlockedByTerrain(start, 0, 0);
            return true;
        }

        if (TryGetContainingStructure(start, out StructureId insideStructId))
        {
            immediateResult = LineOfSightResult.BlockedByStructure(insideStructId, start, 0, 0);
            return true;
        }

        if (TryTraceTerrain(start, delta, end, totalDistance, out float terrainHitT, ref visitedTerrainCells))
        {
            closestT = terrainHitT;
            closestKind = ObstructionKind.Terrain;
        }

        immediateResult = default;
        return false;
    }

    private LineOfSightResult EvaluateCoincidentPoint(Vector3 point)
    {
        if (IsInsideTerrain(point))
            return LineOfSightResult.BlockedByTerrain(point, 0, 0);

        if (TryGetContainingStructure(point, out StructureId structId))
            return LineOfSightResult.BlockedByStructure(structId, point, 0, 0);

        return LineOfSightResult.Clear(point, 0);
    }

    private bool IsInsideTerrain(Vector3 point) =>
        HeightMap.Contains(new Vector2(point.X, point.Z)) &&
        point.Y < HeightMap.GetHeight(point.X, point.Z) - GroundTolerance;

    private bool TryGetContainingStructure(Vector3 point, out StructureId structureId)
    {
        structureId = default;
        if (point.X < 0 || point.X > HeightMap.Width || point.Z < 0 || point.Z > HeightMap.Depth)
            return false;

        int cx = Math.Clamp((int)MathF.Floor(point.X / StructureCellSize), 0, _gridColumns - 1);
        int cz = Math.Clamp((int)MathF.Floor(point.Z / StructureCellSize), 0, _gridRows - 1);
        int cellIndex = cz * _gridColumns + cx;

        int startIdx = _cellOffsets[cellIndex];
        int endIdx = _cellOffsets[cellIndex + 1];

        bool found = false;
        StructureId bestId = default;

        for (int i = startIdx; i < endIdx; i++)
        {
            OpaqueStructure s = _structures[_structureIndices[i]];
            if (s.ContainsInterior(point, VolumeEpsilon))
            {
                if (!found || s.Id.Value < bestId.Value)
                {
                    bestId = s.Id;
                    found = true;
                }
            }
        }

        if (found)
        {
            structureId = bestId;
            return true;
        }

        return false;
    }

    private static bool IsBetterHit(float newT, StructureId newId, float currentT, StructureId currentId, bool hasCurrentHit)
    {
        if (!hasCurrentHit) return true;
        if (newT < currentT - VolumeEpsilon) return true;
        if (Math.Abs(newT - currentT) <= VolumeEpsilon && newId.Value < currentId.Value) return true;
        return false;
    }

    private bool TryTraceTerrain(Vector3 start, Vector3 delta, Vector3 end, float totalDistance, out float hitT, ref int visitedCells)
    {
        hitT = 1.0f;
        float dx = delta.X;
        float dz = delta.Z;
        float l2d = MathF.Sqrt(dx * dx + dz * dz);

        // Vertical query (pure vertical ray)
        if (l2d < 1e-7f)
        {
            visitedCells++;
            if (!HeightMap.Contains(new Vector2(start.X, start.Z)))
                return false;

            float groundY = HeightMap.GetHeight(start.X, start.Z);
            if (start.Y >= groundY - GroundTolerance && start.Y + delta.Y < groundY - GroundTolerance)
            {
                hitT = Math.Clamp((start.Y - groundY) / -delta.Y, 0, 1);
                return true;
            }
            return false;
        }

        // Clip 2D ray to heightmap boundaries [0..Width, 0..Depth]
        double tMin = 0.0, tMax = 1.0;
        if (!ClipInterval(start.X, dx, 0, HeightMap.Width, ref tMin, ref tMax) ||
            !ClipInterval(start.Z, dz, 0, HeightMap.Depth, ref tMin, ref tMax))
        {
            return false;
        }

        float cellSize = HeightMap.CellSize;
        int maxCellX = HeightMap.Columns - 2;
        int maxCellZ = HeightMap.Rows - 2;

        double tCurr = tMin;
        double tLimit = tMax;

        double startRayX = start.X + tCurr * dx;
        double startRayZ = start.Z + tCurr * dz;

        int ix = Math.Clamp((int)Math.Floor(startRayX / cellSize), 0, maxCellX);
        int iz = Math.Clamp((int)Math.Floor(startRayZ / cellSize), 0, maxCellZ);

        int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
        int stepZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);

        while (tCurr < tLimit)
        {
            visitedCells++;

            double nextBoundaryX = stepX != 0
                ? ((ix + (stepX > 0 ? 1 : 0)) * cellSize - start.X) / (double)dx
                : double.PositiveInfinity;
            double nextBoundaryZ = stepZ != 0
                ? ((iz + (stepZ > 0 ? 1 : 0)) * cellSize - start.Z) / (double)dz
                : double.PositiveInfinity;

            double tNext = Math.Min(tLimit, Math.Min(nextBoundaryX, nextBoundaryZ));

            if (TryIntersectTerrainCell(ix, iz, start, delta, end, (float)tCurr, (float)tNext, out float cellHitT))
            {
                float remainingDistance = (1.0f - cellHitT) * totalDistance;
                if (remainingDistance <= EndContactTolerance &&
                    end.Y >= HeightMap.GetHeight(end.X, end.Z) - GroundTolerance)
                {
                    // Touching surface at or near a valid surface target does not obstruct line of sight.
                }
                else
                {
                    hitT = cellHitT;
                    return true;
                }
            }

            tCurr = tNext;
            if (tCurr >= tLimit) break;

            if (nextBoundaryX < nextBoundaryZ - 1e-9)
            {
                ix += stepX;
            }
            else if (nextBoundaryZ < nextBoundaryX - 1e-9)
            {
                iz += stepZ;
            }
            else
            {
                ix += stepX;
                iz += stepZ;
            }

            if (ix < 0 || ix > maxCellX || iz < 0 || iz > maxCellZ) break;
        }

        return false;
    }

    private bool TryIntersectTerrainCell(int ix, int iz, Vector3 start, Vector3 delta, Vector3 end, float tIn, float tOut, out float hitT)
    {
        hitT = 1.0f;
        if (tOut < tIn) return false;

        float cellSize = HeightMap.CellSize;
        float cellOriginX = ix * cellSize;
        float cellOriginZ = iz * cellSize;

        Vector3 pIn = tIn <= 1e-6f ? start : start + delta * tIn;
        Vector3 pOut = tOut >= 1.0f - 1e-6f ? end : start + delta * tOut;

        float uIn = Math.Clamp((pIn.X - cellOriginX) / cellSize, 0, 1);
        float vIn = Math.Clamp((pIn.Z - cellOriginZ) / cellSize, 0, 1);
        float uOut = Math.Clamp((pOut.X - cellOriginX) / cellSize, 0, 1);
        float vOut = Math.Clamp((pOut.Z - cellOriginZ) / cellSize, 0, 1);

        float h00 = HeightMap.SampleHeight(ix, iz);
        float h10 = HeightMap.SampleHeight(ix + 1, iz);
        float h01 = HeightMap.SampleHeight(ix, iz + 1);
        float h11 = HeightMap.SampleHeight(ix + 1, iz + 1);

        float cuv = h11 - h10 - h01 + h00;

        float hIn = BilinearHeight(h00, h10, h01, cuv, uIn, vIn);
        float hOut = BilinearHeight(h00, h10, h01, cuv, uOut, vOut);

        float dIn = pIn.Y - hIn;
        float dOut = pOut.Y - hOut;

        // Shift surface down by GroundTolerance: touching surface is clear, only real penetration blocks.
        float dInEff = dIn + GroundTolerance;
        float dOutEff = dOut + GroundTolerance;

        // If starting underground
        if (dInEff < 0)
        {
            hitT = tIn;
            return true;
        }

        float du = uOut - uIn;
        float dv = vOut - vIn;

        // Quadratic equation in tau in [0..1] along interval [tIn, tOut]:
        // D_eff(tau) = A * tau^2 + B * tau + C
        float a = -cuv * du * dv;
        float c = dInEff;
        float b = dOutEff - dInEff - a;

        // Subcase 1: Essentially linear
        if (Math.Abs(a) < 1e-9f)
        {
            if (dOutEff < 0)
            {
                float denom = dInEff - dOutEff;
                float tau = denom > 1e-9f ? dInEff / denom : 0f;
                tau = Math.Clamp(tau, 0, 1);
                hitT = tIn + tau * (tOut - tIn);
                return true;
            }
            return false;
        }

        // Subcase 2: Concave (A < 0). Midpoint is higher than chord, can only cross below 0 if dOutEff < 0.
        if (a < 0)
        {
            if (dOutEff < 0)
            {
                float disc = b * b - 4 * a * c;
                if (disc >= 0)
                {
                    float sqrtDisc = MathF.Sqrt(disc);
                    float tau = (-b - sqrtDisc) / (2 * a);
                    if (tau >= 0 && tau <= 1)
                    {
                        hitT = tIn + tau * (tOut - tIn);
                        return true;
                    }
                }
            }
            return false;
        }

        // Subcase 3: Convex (A > 0). Curve dips downward in the middle.
        // Can cross below 0 if dOutEff < 0 or if the vertex lies within (0, 1) with b < 0.
        if (dOutEff < 0 || (b < 0 && -b < 2 * a))
        {
            float discr = b * b - 4 * a * c;
            if (discr >= 0)
            {
                float sqrtDisc = MathF.Sqrt(discr);
                float q = 0.5f * (-b + sqrtDisc);
                float tau = q > 1e-9f ? c / q : 0f;
                if (tau >= 0 && tau <= 1)
                {
                    hitT = tIn + tau * (tOut - tIn);
                    return true;
                }
            }
        }

        return false;
    }

    private static float BilinearHeight(float h00, float h10, float h01, float cuv, float u, float v) =>
        h00 + (h10 - h00) * u + (h01 - h00) * v + cuv * u * v;

    private bool TryTraceStructures(
        Vector3 start,
        Vector3 delta,
        float totalDistance,
        float maxT,
        LineOfSightContext? context,
        out float hitT,
        out StructureId hitStructureId,
        ref int visitedCells,
        ref int testedStructures)
    {
        hitT = maxT;
        hitStructureId = default;
        bool foundHit = false;

        if (_structures.Length == 0 || maxT <= 0)
            return false;

        LineOfSightContext ctx = context ?? GetThreadContext(_structures.Length);
        ctx.EnsureCapacity(_structures.Length);
        int queryId = ctx.NextQueryId();
        int[] mailbox = ctx.Mailbox;

        float dx = delta.X;
        float dz = delta.Z;
        float l2d = MathF.Sqrt(dx * dx + dz * dz);

        // Vertical query over structure grid
        if (l2d < 1e-7f)
        {
            visitedCells++;
            int cx = Math.Clamp((int)MathF.Floor(start.X / StructureCellSize), 0, _gridColumns - 1);
            int cz = Math.Clamp((int)MathF.Floor(start.Z / StructureCellSize), 0, _gridRows - 1);
            int cellIndex = cz * _gridColumns + cx;

            int startIdx = _cellOffsets[cellIndex];
            int endIdx = _cellOffsets[cellIndex + 1];

            for (int i = startIdx; i < endIdx; i++)
            {
                int sIdx = _structureIndices[i];
                if (mailbox[sIdx] == queryId) continue;
                mailbox[sIdx] = queryId;

                testedStructures++;
                OpaqueStructure s = _structures[sIdx];
                if (TryIntersectBox(s, start, delta, totalDistance, out float tNear))
                {
                    if (IsBetterHit(tNear, s.Id, hitT, hitStructureId, foundHit))
                    {
                        hitT = tNear;
                        hitStructureId = s.Id;
                        foundHit = true;
                    }
                }
            }
            return foundHit;
        }

        // Clip 2D ray to structure grid boundary
        double tMin = 0.0, tMax = maxT;
        float gridWidth = _gridColumns * StructureCellSize;
        float gridDepth = _gridRows * StructureCellSize;

        if (!ClipInterval(start.X, dx, 0, gridWidth, ref tMin, ref tMax) ||
            !ClipInterval(start.Z, dz, 0, gridDepth, ref tMin, ref tMax))
        {
            return false;
        }

        double tCurr = tMin;
        double tLimit = Math.Min((double)maxT, tMax);

        double startRayX = start.X + tCurr * dx;
        double startRayZ = start.Z + tCurr * dz;

        int cxCurr = Math.Clamp((int)Math.Floor(startRayX / StructureCellSize), 0, _gridColumns - 1);
        int czCurr = Math.Clamp((int)Math.Floor(startRayZ / StructureCellSize), 0, _gridRows - 1);

        int stepX = dx > 0 ? 1 : (dx < 0 ? -1 : 0);
        int stepZ = dz > 0 ? 1 : (dz < 0 ? -1 : 0);

        while (tCurr < tLimit)
        {
            visitedCells++;

            double nextBoundaryX = stepX != 0
                ? ((cxCurr + (stepX > 0 ? 1 : 0)) * StructureCellSize - start.X) / (double)dx
                : double.PositiveInfinity;
            double nextBoundaryZ = stepZ != 0
                ? ((czCurr + (stepZ > 0 ? 1 : 0)) * StructureCellSize - start.Z) / (double)dz
                : double.PositiveInfinity;

            double tNext = Math.Min(tLimit, Math.Min(nextBoundaryX, nextBoundaryZ));

            // If we already found an intersection earlier than this cell entrance, no later cell can beat it.
            if (foundHit && hitT <= tCurr + VolumeEpsilon)
                break;

            int cellIndex = czCurr * _gridColumns + cxCurr;
            int startIdx = _cellOffsets[cellIndex];
            int endIdx = _cellOffsets[cellIndex + 1];

            for (int i = startIdx; i < endIdx; i++)
            {
                int sIdx = _structureIndices[i];
                if (mailbox[sIdx] == queryId) continue;
                mailbox[sIdx] = queryId;

                testedStructures++;
                OpaqueStructure s = _structures[sIdx];
                if (TryIntersectBox(s, start, delta, totalDistance, out float tNear))
                {
                    if (IsBetterHit(tNear, s.Id, hitT, hitStructureId, foundHit))
                    {
                        hitT = tNear;
                        hitStructureId = s.Id;
                        foundHit = true;
                    }
                }
            }

            // If a hit was confirmed inside or before the exit of this cell, no later cell can contain a closer hit.
            if (foundHit && hitT <= tNext + VolumeEpsilon)
                break;

            tCurr = tNext;
            if (tCurr >= tLimit) break;

            if (nextBoundaryX < nextBoundaryZ - 1e-9)
            {
                cxCurr += stepX;
            }
            else if (nextBoundaryZ < nextBoundaryX - 1e-9)
            {
                czCurr += stepZ;
            }
            else
            {
                cxCurr += stepX;
                czCurr += stepZ;
            }

            if (cxCurr < 0 || cxCurr >= _gridColumns || czCurr < 0 || czCurr >= _gridRows) break;
        }

        return foundHit;
    }

    private static bool TryIntersectBox(OpaqueStructure structure, Vector3 start, Vector3 delta, float totalDistance, out float hitT)
    {
        hitT = 1.0f;
        float tNear = 0.0f;
        float tFar = 1.0f;

        // X slab
        if (Math.Abs(delta.X) > 1e-9f)
        {
            float invDx = 1.0f / delta.X;
            float t1 = (structure.MinX - start.X) * invDx;
            float t2 = (structure.MaxX - start.X) * invDx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar) return false;
        }
        else
        {
            if (start.X <= structure.MinX || start.X >= structure.MaxX) return false;
        }

        // Y slab
        if (Math.Abs(delta.Y) > 1e-9f)
        {
            float invDy = 1.0f / delta.Y;
            float t1 = (structure.MinY - start.Y) * invDy;
            float t2 = (structure.MaxY - start.Y) * invDy;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar) return false;
        }
        else
        {
            if (start.Y <= structure.MinY || start.Y >= structure.MaxY) return false;
        }

        // Z slab
        if (Math.Abs(delta.Z) > 1e-9f)
        {
            float invDz = 1.0f / delta.Z;
            float t1 = (structure.MinZ - start.Z) * invDz;
            float t2 = (structure.MaxZ - start.Z) * invDz;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar) return false;
        }
        else
        {
            if (start.Z <= structure.MinZ || start.Z >= structure.MaxZ) return false;
        }

        // Grazing check in coordinate-space distance along ray:
        // Physical penetration must exceed WorldPenetrationEpsilon to be considered an obstruction.
        float penetrationDistance = (tFar - tNear) * totalDistance;
        if (penetrationDistance < WorldPenetrationEpsilon)
            return false;

        if (structure.ContainsInterior(start, VolumeEpsilon))
            hitT = 0.0f;
        else
            hitT = Math.Max(0.0f, tNear);

        return true;
    }

    private static bool ClipInterval(float origin, float delta, float min, float max, ref double t0, ref double t1)
    {
        if (Math.Abs(delta) < 1e-12f)
        {
            return origin >= min && origin <= max;
        }

        double inv = 1.0 / delta;
        double r0 = (min - origin) * inv;
        double r1 = (max - origin) * inv;
        if (r0 > r1) (r0, r1) = (r1, r0);

        t0 = Math.Max(t0, r0);
        t1 = Math.Min(t1, r1);
        return t0 <= t1;
    }
}
