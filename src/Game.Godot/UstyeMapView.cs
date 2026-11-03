using System.IO;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;
using ScaleVehicle = DeadGround.Client.UstyeMapPreview.ScaleVehicle;

namespace DeadGround.Client;

/// <summary>
/// Reusable 3D map presentation for the 5000 x 5000 m Ustye map.
/// Shared between standalone map preview and playable game modes.
/// Generates terrain chunks, water surfaces, roads, bridges, structures, landmarks, and vegetation.
/// </summary>
public partial class UstyeMapView : Node3D
{
    private readonly UstyePlanData _plan;
    private readonly bool _includeScaleVehicles;
    private FogOfWarView? _fogOverlay;

    private Node3D _terrainRoot = null!;
    private Node3D _waterRoot = null!;
    private Node3D _roadsRoot = null!;
    private Node3D _structuresRoot = null!;
    private Node3D _landmarksRoot = null!;
    private Node3D _vegetationRoot = null!;
    private Node3D? _vehiclesRoot;
    private Node3D _contextRoot = null!;

    private Mesh _coniferTreeMesh = null!;
    private Mesh _deciduousTreeMesh = null!;
    private readonly List<MultiMeshInstance3D> _coniferMultiMeshes = [];
    private readonly List<MultiMeshInstance3D> _deciduousMultiMeshes = [];
    private readonly List<ScaleVehicle> _scaleVehicles = [];

    public UstyePlanData Plan => _plan;
    public Node3D TerrainRoot => _terrainRoot;
    public Node3D WaterRoot => _waterRoot;
    public Node3D RoadsRoot => _roadsRoot;
    public Node3D StructuresRoot => _structuresRoot;
    public Node3D ContextRoot => _contextRoot;
    public Node3D LandmarksRoot => _landmarksRoot;
    public Node3D VegetationRoot => _vegetationRoot;
    public Node3D? VehiclesRoot => _vehiclesRoot;

    public Mesh ConiferTreeMesh => _coniferTreeMesh;
    public Mesh DeciduousTreeMesh => _deciduousTreeMesh;
    public IReadOnlyList<MultiMeshInstance3D> ConiferMultiMeshes => _coniferMultiMeshes;
    public IReadOnlyList<MultiMeshInstance3D> DeciduousMultiMeshes => _deciduousMultiMeshes;
    public int TotalConiferCount => _coniferMultiMeshes.Sum(m => m.Multimesh?.InstanceCount ?? 0);
    public int TotalDeciduousCount => _deciduousMultiMeshes.Sum(m => m.Multimesh?.InstanceCount ?? 0);
    public IReadOnlyList<ScaleVehicle> ScaleVehicles => _scaleVehicles;

    public UstyeMapView(UstyePlanData? plan = null, bool includeScaleVehicles = false)
        : this(plan, includeScaleVehicles, null)
    {
    }

    internal UstyeMapView(UstyePlanData? plan, bool includeScaleVehicles, FogOfWarView? fogOverlay)
    {
        _plan = plan ?? UstyePlanData.Load();
        _includeScaleVehicles = includeScaleVehicles;
        _fogOverlay = fogOverlay;
    }

    internal void AttachFogOfWar(FogOfWarView fog)
    {
        _fogOverlay = fog;
        if (_terrainRoot is not null)
        {
            foreach (Node child in _terrainRoot.GetChildren())
            {
                if (child is MeshInstance3D mesh)
                {
                    mesh.MaterialOverride = fog.Material;
                }
            }
        }
    }

    public override void _Ready()
    {
        if (_terrainRoot is null)
        {
            SetupTerrain();
        }
        SetupWater();
        SetupRoads();
        SetupCrossings();
        SetupStructures();
        SetupLocalContext();
        SetupLandmarkSites();
        SetupVegetation();
        if (_includeScaleVehicles)
        {
            SetupScaleVehicles();
        }
    }

    internal bool IsRiparianChunk(int cx, int cz)
    {
        float minX = cx * 500f;
        float maxX = (cx + 1) * 500f;
        float minZ = cz * 500f;
        float maxZ = (cz + 1) * 500f;

        const float riverPad = 90f;
        foreach (NumericsVector2 p in _plan.RiverSpline)
        {
            if (p.X >= minX - riverPad && p.X <= maxX + riverPad &&
                p.Y >= minZ - riverPad && p.Y <= maxZ + riverPad)
            {
                return true;
            }
        }

        const float bayPad = 90f;
        foreach (NumericsVector2 p in _plan.BayCoastSpline)
        {
            if (p.X >= minX - bayPad && p.X <= maxX + bayPad &&
                p.Y >= minZ - bayPad && p.Y <= maxZ + bayPad)
            {
                return true;
            }
        }

        return false;
    }

    private static float SmoothNoise2D(float x, float z, float frequency)
    {
        float nx = x * frequency;
        float nz = z * frequency;
        float s1 = MathF.Sin(nx * 1.0f + nz * 0.7f);
        float s2 = MathF.Sin(nx * -0.6f + nz * 1.3f + 1.2f);
        float s3 = MathF.Sin(nx * 1.8f - nz * 1.1f + 2.7f);
        return 0.5f + 0.5f * (0.50f * s1 + 0.35f * s2 + 0.15f * s3);
    }

    private static Color EvaluateTerrainColor(float wx, float wy, float wz, Vector3 norm, Vector3 lightDir)
    {
        float macro = SmoothNoise2D(wx, wz, 1f / 350f);

        Color coolMeadow = new("3a4a28");
        Color dryRye = new("465031");
        Color richPasture = new("314124");

        Color meadowBase = macro < 0.50f
            ? coolMeadow.Lerp(dryRye, macro / 0.50f)
            : dryRye.Lerp(richPasture, (macro - 0.50f) / 0.50f);

        if (wy > 14f)
        {
            float ridgeFactor = Math.Clamp((wy - 14f) / 26f, 0f, 1f);
            meadowBase = meadowBase.Lerp(new Color("5a5c43"), ridgeFactor);
        }

        float shade = Mathf.Clamp(0.55f + 0.45f * norm.Dot(lightDir), 0.35f, 1.0f);
        return new Color(meadowBase.R * shade, meadowBase.G * shade, meadowBase.B * shade, 1f);
    }

    private void SetupTerrain()
    {
        _terrainRoot = new Node3D { Name = "TerrainRoot" };
        AddChild(_terrainRoot);

        const float chunkSize = 500f;
        int chunkCols = (int)(_plan.ExtentX / chunkSize);
        int chunkRows = (int)(_plan.ExtentZ / chunkSize);

        Material terrainMaterial = _fogOverlay != null
            ? _fogOverlay.Material
            : new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel,
                VertexColorUseAsAlbedo = true,
                Roughness = 0.95f,
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled
            };

        Vector3 lightDir = new Vector3(-0.55f, 0.75f, -0.35f).Normalized();

        for (int cz = 0; cz < chunkRows; cz++)
        {
            for (int cx = 0; cx < chunkCols; cx++)
            {
                bool isFine = IsRiparianChunk(cx, cz);
                float step = isFine ? 6.25f : 25.0f;
                int cellsPerChunk = (int)(chunkSize / step);

                float originX = cx * chunkSize;
                float originZ = cz * chunkSize;

                int vCols = cellsPerChunk + 1;
                int vRows = cellsPerChunk + 1;
                Vector3[] vertices = new Vector3[vCols * vRows];
                Vector3[] normals = new Vector3[vertices.Length];
                Color[] colors = new Color[vertices.Length];
                int[] indices = new int[cellsPerChunk * cellsPerChunk * 6];

                bool coarseWest = isFine && cx > 0 && !IsRiparianChunk(cx - 1, cz);
                bool coarseEast = isFine && cx < chunkCols - 1 && !IsRiparianChunk(cx + 1, cz);
                bool coarseSouth = isFine && cz > 0 && !IsRiparianChunk(cx, cz - 1);
                bool coarseNorth = isFine && cz < chunkRows - 1 && !IsRiparianChunk(cx, cz + 1);

                float[,] heights = new float[vCols, vRows];
                for (int lz = 0; lz <= cellsPerChunk; lz++)
                {
                    float wz = originZ + lz * step;
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        float wx = originX + lx * step;
                        heights[lx, lz] = _plan.SampleTerrainElevation(wx, wz);
                    }
                }

                if (coarseWest)
                {
                    for (int lz = 0; lz <= cellsPerChunk; lz++)
                    {
                        int k = lz / 4;
                        int rem = lz % 4;
                        float y0 = _plan.SampleTerrainElevation(originX, originZ + k * 25f);
                        float y1 = _plan.SampleTerrainElevation(originX, originZ + (k + 1) * 25f);
                        heights[0, lz] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }
                if (coarseEast)
                {
                    for (int lz = 0; lz <= cellsPerChunk; lz++)
                    {
                        int k = lz / 4;
                        int rem = lz % 4;
                        float y0 = _plan.SampleTerrainElevation(originX + chunkSize, originZ + k * 25f);
                        float y1 = _plan.SampleTerrainElevation(originX + chunkSize, originZ + (k + 1) * 25f);
                        heights[cellsPerChunk, lz] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }
                if (coarseSouth)
                {
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        int k = lx / 4;
                        int rem = lx % 4;
                        float y0 = _plan.SampleTerrainElevation(originX + k * 25f, originZ);
                        float y1 = _plan.SampleTerrainElevation(originX + (k + 1) * 25f, originZ);
                        heights[lx, 0] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }
                if (coarseNorth)
                {
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        int k = lx / 4;
                        int rem = lx % 4;
                        float y0 = _plan.SampleTerrainElevation(originX + k * 25f, originZ + chunkSize);
                        float y1 = _plan.SampleTerrainElevation(originX + (k + 1) * 25f, originZ + chunkSize);
                        heights[lx, cellsPerChunk] = Mathf.Lerp(y0, y1, rem / 4f);
                    }
                }

                for (int lz = 0; lz <= cellsPerChunk; lz++)
                {
                    float wz = originZ + lz * step;
                    for (int lx = 0; lx <= cellsPerChunk; lx++)
                    {
                        float wx = originX + lx * step;
                        float wy = heights[lx, lz];
                        int vIdx = lz * vCols + lx;
                        vertices[vIdx] = new Vector3(wx, wy, wz);

                        float hL = lx > 0 ? heights[lx - 1, lz] : _plan.SampleTerrainElevation(wx - step, wz);
                        float hR = lx < cellsPerChunk ? heights[lx + 1, lz] : _plan.SampleTerrainElevation(wx + step, wz);
                        float hD = lz > 0 ? heights[lx, lz - 1] : _plan.SampleTerrainElevation(wx, wz - step);
                        float hU = lz < cellsPerChunk ? heights[lx, lz + 1] : _plan.SampleTerrainElevation(wx, wz + step);
                        Vector3 norm = new Vector3(hL - hR, 2f * step, hD - hU).Normalized();
                        normals[vIdx] = norm;

                        colors[vIdx] = EvaluateTerrainColor(wx, wy, wz, norm, lightDir);
                    }
                }

                int iOffset = 0;
                for (int lz = 0; lz < cellsPerChunk; lz++)
                {
                    for (int lx = 0; lx < cellsPerChunk; lx++)
                    {
                        int a = lz * vCols + lx;
                        int b = a + 1;
                        int c = (lz + 1) * vCols + lx;
                        int d = c + 1;

                        indices[iOffset++] = a;
                        indices[iOffset++] = b;
                        indices[iOffset++] = c;

                        indices[iOffset++] = b;
                        indices[iOffset++] = d;
                        indices[iOffset++] = c;
                    }
                }

                using Godot.Collections.Array arrays = new();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                arrays[(int)Mesh.ArrayType.Color] = colors;
                arrays[(int)Mesh.ArrayType.Index] = indices;

                ArrayMesh chunkMesh = new();
                chunkMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

                MeshInstance3D chunkInstance = new()
                {
                    Name = $"TerrainChunk_{cx}_{cz}",
                    Mesh = chunkMesh,
                    MaterialOverride = terrainMaterial
                };
                _terrainRoot.AddChild(chunkInstance);
            }
        }
    }

    private void SetupWater()
    {
        _waterRoot = new Node3D { Name = "WaterRoot" };
        AddChild(_waterRoot);

        Shader waterShader = GD.Load<Shader>("res://UstyeWater.gdshader");
        ShaderMaterial waterMaterial = new()
        {
            Shader = waterShader
        };

        NumericsVector2[] spline = _plan.RiverSpline;
        float halfWidth = _plan.RiverWidth * 0.52f;

        Vector3[] riverVerts = new Vector3[spline.Length * 2];
        Vector3[] riverNorms = new Vector3[riverVerts.Length];
        Vector2[] riverUvs = new Vector2[riverVerts.Length];
        int[] riverIndices = new int[(spline.Length - 1) * 6];

        float cumulativeLength = 0f;

        for (int i = 0; i < spline.Length; i++)
        {
            NumericsVector2 curr = spline[i];
            if (i > 0) cumulativeLength += NumericsVector2.Distance(curr, spline[i - 1]);

            NumericsVector2 tangent = i < spline.Length - 1
                ? NumericsVector2.Normalize(spline[i + 1] - curr)
                : NumericsVector2.Normalize(curr - spline[i - 1]);
            NumericsVector2 normal = new(-tangent.Y, tangent.X);

            NumericsVector2 left = curr - normal * halfWidth;
            NumericsVector2 right = curr + normal * halfWidth;

            riverVerts[i * 2] = new Vector3(left.X, 2.0f, left.Y);
            riverVerts[i * 2 + 1] = new Vector3(right.X, 2.0f, right.Y);
            riverNorms[i * 2] = Vector3.Up;
            riverNorms[i * 2 + 1] = Vector3.Up;

            float v = cumulativeLength * 0.015f;
            riverUvs[i * 2] = new Vector2(0f, v);
            riverUvs[i * 2 + 1] = new Vector2(1f, v);
        }

        int rOffset = 0;
        for (int i = 0; i < spline.Length - 1; i++)
        {
            int a = i * 2;
            int b = i * 2 + 1;
            int c = (i + 1) * 2;
            int d = (i + 1) * 2 + 1;

            riverIndices[rOffset++] = a;
            riverIndices[rOffset++] = b;
            riverIndices[rOffset++] = c;

            riverIndices[rOffset++] = b;
            riverIndices[rOffset++] = d;
            riverIndices[rOffset++] = c;
        }

        using Godot.Collections.Array riverArrays = new();
        riverArrays.Resize((int)Mesh.ArrayType.Max);
        riverArrays[(int)Mesh.ArrayType.Vertex] = riverVerts;
        riverArrays[(int)Mesh.ArrayType.Normal] = riverNorms;
        riverArrays[(int)Mesh.ArrayType.TexUV] = riverUvs;
        riverArrays[(int)Mesh.ArrayType.Index] = riverIndices;

        ArrayMesh riverMesh = new();
        riverMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, riverArrays);

        MeshInstance3D riverInstance = new()
        {
            Name = "RiverWater",
            Mesh = riverMesh,
            MaterialOverride = waterMaterial
        };
        _waterRoot.AddChild(riverInstance);

        NumericsVector2[] bayCoast = _plan.BayCoastSpline;
        List<Vector3> bayVerts = [];
        List<Vector3> bayNorms = [];
        List<Vector2> bayUvs = [];
        List<int> bayIndices = [];

        Vector2 bayCenter = new(4500f, 4500f);
        bayVerts.Add(new Vector3(bayCenter.X, 2.0f, bayCenter.Y));
        bayNorms.Add(Vector3.Up);
        bayUvs.Add(new Vector2(0.5f, 0.5f));

        for (int i = 0; i < bayCoast.Length; i++)
        {
            NumericsVector2 p = bayCoast[i];
            bayVerts.Add(new Vector3(p.X, 2.0f, p.Y));
            bayNorms.Add(Vector3.Up);
            bayUvs.Add(new Vector2(p.X * 0.001f, p.Y * 0.001f));
        }

        bayVerts.Add(new Vector3(5000f, 2.0f, 5000f));
        bayNorms.Add(Vector3.Up);
        bayUvs.Add(new Vector2(5.0f, 5.0f));

        int totalBay = bayVerts.Count;
        for (int i = 1; i < totalBay - 1; i++)
        {
            bayIndices.Add(0);
            bayIndices.Add(i);
            bayIndices.Add(i + 1);
        }
        bayIndices.Add(0);
        bayIndices.Add(totalBay - 1);
        bayIndices.Add(1);

        using Godot.Collections.Array bayArrays = new();
        bayArrays.Resize((int)Mesh.ArrayType.Max);
        bayArrays[(int)Mesh.ArrayType.Vertex] = bayVerts.ToArray();
        bayArrays[(int)Mesh.ArrayType.Normal] = bayNorms.ToArray();
        bayArrays[(int)Mesh.ArrayType.TexUV] = bayUvs.ToArray();
        bayArrays[(int)Mesh.ArrayType.Index] = bayIndices.ToArray();

        ArrayMesh bayMesh = new();
        bayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, bayArrays);

        MeshInstance3D bayInstance = new()
        {
            Name = "BayWater",
            Mesh = bayMesh,
            MaterialOverride = waterMaterial
        };
        _waterRoot.AddChild(bayInstance);
    }

    private void SetupRoads()
    {
        _roadsRoot = new Node3D { Name = "RoadsRoot" };
        AddChild(_roadsRoot);

        StandardMaterial3D asphaltMaterial = new()
        {
            AlbedoColor = new Color("353638"),
            Roughness = 0.88f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        StandardMaterial3D gravelMaterial = new()
        {
            AlbedoColor = new Color("585246"),
            Roughness = 0.94f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        StandardMaterial3D dirtPathMaterial = new()
        {
            AlbedoColor = new Color("655b4b"),
            Roughness = 0.98f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled
        };

        foreach (UstyePlanData.Road road in _plan.Roads)
        {
            Material mat = road.Id switch
            {
                "central" => asphaltMaterial,
                "foot" => dirtPathMaterial,
                _ => gravelMaterial
            };

            UstyePlanData.Crossing? crossing = _plan.Crossings.FirstOrDefault(c => c.RoadId == road.Id);
            BuildRoadRibbon(road.Points, road.Width, mat, $"Road_{road.Id}", crossing);
        }

        foreach (UstyePlanData.Road access in _plan.AccessRoads)
        {
            BuildRoadRibbon(access.Points, access.Width, gravelMaterial, $"Road_{access.Id}", null);
        }
    }

    private static List<Vector2> SubdividePoints(Vector2[] points, float maxStep = 6.0f)
    {
        List<Vector2> result = [];
        if (points.Length == 0) return result;
        result.Add(points[0]);
        for (int i = 0; i < points.Length - 1; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[i + 1];
            float dist = a.DistanceTo(b);
            int steps = Math.Max(1, (int)MathF.Ceiling(dist / maxStep));
            for (int s = 1; s <= steps; s++)
            {
                result.Add(a.Lerp(b, (float)s / steps));
            }
        }
        return result;
    }

    private static List<List<Vector2>> SplitRoadAtCrossing(Vector2[] points, UstyePlanData.Crossing? crossing)
    {
        if (crossing is null || points.Length < 2)
        {
            return [points.ToList()];
        }

        Vector2 pA = new(crossing.EndA.X, crossing.EndA.Y);
        Vector2 pB = new(crossing.EndB.X, crossing.EndB.Y);

        Vector2 pStart = points[0].DistanceTo(pB) < points[0].DistanceTo(pA) ? pB : pA;
        Vector2 pEnd = pStart == pA ? pB : pA;

        Vector2 span = pEnd - pStart;
        float spanLen = span.Length();
        Vector2 spanDir = span / spanLen;

        List<Vector2> appA = [];
        List<Vector2> appB = [];

        foreach (Vector2 p in points)
        {
            float proj = (p - pStart).Dot(spanDir);
            if (proj <= 0.01f)
            {
                if (p.DistanceTo(pStart) > 2.0f)
                {
                    appA.Add(p);
                }
            }
            else if (proj >= spanLen - 0.01f)
            {
                if (p.DistanceTo(pEnd) > 2.0f)
                {
                    appB.Add(p);
                }
            }
        }

        appA.Add(pStart);
        appB.Insert(0, pEnd);

        return [appA, appB];
    }

    private void BuildRoadRibbon(NumericsVector2[] numericsPoints, float width, Material material, string name, UstyePlanData.Crossing? crossing)
    {
        if (numericsPoints.Length < 2) return;

        Vector2[] points = new Vector2[numericsPoints.Length];
        for (int i = 0; i < numericsPoints.Length; i++)
            points[i] = new Vector2(numericsPoints[i].X, numericsPoints[i].Y);

        List<List<Vector2>> strips = SplitRoadAtCrossing(points, crossing);
        float halfWidth = width * 0.5f;

        List<Vector3> verts = [];
        List<Vector3> norms = [];
        List<int> indices = [];

        foreach (List<Vector2> strip in strips)
        {
            if (strip.Count < 2) continue;
            List<Vector2> densePoints = SubdividePoints(strip.ToArray(), 6.0f);
            int stripBaseVertex = verts.Count;

            for (int i = 0; i < densePoints.Count; i++)
            {
                Vector2 curr = densePoints[i];

                Vector2 tangent = i < densePoints.Count - 1
                    ? (densePoints[i + 1] - curr).Normalized()
                    : (curr - densePoints[i - 1]).Normalized();
                Vector2 normal = new(-tangent.Y, tangent.X);

                Vector2 left = curr - normal * halfWidth;
                Vector2 right = curr + normal * halfWidth;

                float yLeft = _plan.SampleTerrainElevation(left.X, left.Y) + 0.12f;
                float yRight = _plan.SampleTerrainElevation(right.X, right.Y) + 0.12f;

                int baseIdx = verts.Count;
                verts.Add(new Vector3(left.X, yLeft, left.Y));
                verts.Add(new Vector3(right.X, yRight, right.Y));
                norms.Add(Vector3.Up);
                norms.Add(Vector3.Up);

                if (baseIdx >= stripBaseVertex + 2)
                {
                    int a = baseIdx - 2;
                    int b = baseIdx - 1;
                    int c = baseIdx;
                    int d = baseIdx + 1;

                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);

                    indices.Add(b);
                    indices.Add(d);
                    indices.Add(c);
                }
            }
        }

        if (indices.Count == 0) return;

        using Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = norms.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        MeshInstance3D inst = new()
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = material
        };
        _roadsRoot.AddChild(inst);
    }

    private void SetupCrossings()
    {
        StandardMaterial3D concreteBridgeMat = new()
        {
            AlbedoColor = new Color("727578"),
            Roughness = 0.85f
        };

        StandardMaterial3D steelDeckMat = new()
        {
            AlbedoColor = new Color("484d52"),
            Roughness = 0.70f,
            Metallic = 0.35f
        };

        StandardMaterial3D railingMat = new()
        {
            AlbedoColor = new Color("35383c"),
            Roughness = 0.60f
        };

        foreach (UstyePlanData.Crossing crossing in _plan.Crossings)
        {
            Node3D bridgeRoot = new() { Name = $"Bridge_{crossing.Id}" };
            _roadsRoot.AddChild(bridgeRoot);

            NumericsVector2 a = crossing.EndA;
            NumericsVector2 b = crossing.EndB;

            float hA = _plan.SampleTerrainElevation(a.X, a.Y) + 0.12f;
            float hB = _plan.SampleTerrainElevation(b.X, b.Y) + 0.12f;

            Vector3 pA = new(a.X, hA, a.Y);
            Vector3 pB = new(b.X, hB, b.Y);
            Vector3 spanVec = pB - pA;
            float length = spanVec.Length();
            float deckWidth = crossing.DeckWidth;
            float deckThickness = crossing.Id == "CROSS-C" ? 1.6f : 1.2f;

            Vector3 axisX = spanVec.Normalized();
            Vector2 dir2D = new Vector2(b.X - a.X, b.Y - a.Y).Normalized();
            Vector3 axisZ = new Vector3(-dir2D.Y, 0f, dir2D.X).Normalized();
            Vector3 axisY = axisZ.Cross(axisX).Normalized();
            Basis deckBasis = new(axisX, axisY, axisZ);

            Vector3 topCenter = (pA + pB) * 0.5f;
            Vector3 boxCenter = topCenter - axisY * (deckThickness * 0.5f);

            Material deckMat = crossing.Id == "CROSS-C" ? concreteBridgeMat : steelDeckMat;
            MeshInstance3D deckMesh = new()
            {
                Name = "Deck",
                Mesh = new BoxMesh { Size = new Vector3(length, deckThickness, deckWidth) },
                MaterialOverride = deckMat,
                Transform = new Transform3D(deckBasis, boxCenter)
            };
            bridgeRoot.AddChild(deckMesh);

            float railHeight = 1.1f;
            float railThickness = 0.25f;
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 railCenter = boxCenter +
                    axisY * (deckThickness * 0.5f + railHeight * 0.5f) +
                    axisZ * (side * (deckWidth * 0.5f - railThickness * 0.5f));

                MeshInstance3D rail = new()
                {
                    Name = $"Railing_{side}",
                    Mesh = new BoxMesh { Size = new Vector3(length, railHeight, railThickness) },
                    MaterialOverride = railingMat,
                    Transform = new Transform3D(deckBasis, railCenter)
                };
                bridgeRoot.AddChild(rail);
            }

            // Abutment blocks anchored into the river banks at EndA and EndB
            float yaw2D = -MathF.Atan2(dir2D.Y, dir2D.X);
            MeshInstance3D abutmentA = new()
            {
                Name = "Abutment_A",
                Mesh = new BoxMesh { Size = new Vector3(8f, 5f, deckWidth * 1.15f) },
                MaterialOverride = concreteBridgeMat,
                Position = new Vector3(a.X, hA - 2.5f, a.Y),
                Rotation = new Vector3(0f, yaw2D, 0f)
            };
            bridgeRoot.AddChild(abutmentA);

            MeshInstance3D abutmentB = new()
            {
                Name = "Abutment_B",
                Mesh = new BoxMesh { Size = new Vector3(8f, 5f, deckWidth * 1.15f) },
                MaterialOverride = concreteBridgeMat,
                Position = new Vector3(b.X, hB - 2.5f, b.Y),
                Rotation = new Vector3(0f, yaw2D, 0f)
            };
            bridgeRoot.AddChild(abutmentB);

            int numPiers = length > 200f ? 4 : 2;
            for (int p = 1; p <= numPiers; p++)
            {
                float t = (float)p / (numPiers + 1);
                NumericsVector2 pierPos = NumericsVector2.Lerp(a, b, t);
                float deckBottomY = Mathf.Lerp(hA, hB, t) - deckThickness;
                float bedY = _plan.SampleTerrainElevation(pierPos.X, pierPos.Y);
                float pierHeight = MathF.Max(1.0f, deckBottomY - bedY);
                float pierCenterY = bedY + pierHeight * 0.5f;

                MeshInstance3D pier = new()
                {
                    Name = $"Pier_{p}",
                    Mesh = new BoxMesh { Size = new Vector3(4f, pierHeight, deckWidth * 0.85f) },
                    MaterialOverride = concreteBridgeMat,
                    Position = new Vector3(pierPos.X, pierCenterY, pierPos.Y),
                    Rotation = new Vector3(0f, yaw2D, 0f)
                };
                bridgeRoot.AddChild(pier);
            }
        }
    }

    private void SetupStructures()
    {
        _structuresRoot = new Node3D { Name = "StructuresRoot" };
        AddChild(_structuresRoot);

        StandardMaterial3D panelBlockMat = new()
        {
            AlbedoColor = new Color("ababa7"),
            Roughness = 0.90f
        };

        StandardMaterial3D windowBandMat = new()
        {
            AlbedoColor = new Color("3e4248"),
            Roughness = 0.55f,
            Metallic = 0.15f
        };

        StandardMaterial3D warehouseWallMat = new()
        {
            AlbedoColor = new Color("656e6d"),
            Roughness = 0.88f
        };

        StandardMaterial3D warehouseRoofMat = new()
        {
            AlbedoColor = new Color("4a5252"),
            Roughness = 0.80f
        };

        StandardMaterial3D logMat = new()
        {
            AlbedoColor = new Color("785838"),
            Roughness = 0.95f
        };

        StandardMaterial3D waterTowerMat = new()
        {
            AlbedoColor = new Color("888e92"),
            Roughness = 0.85f
        };

        StandardMaterial3D waterTankMat = new()
        {
            AlbedoColor = new Color("4e5a62"),
            Roughness = 0.65f,
            Metallic = 0.30f
        };

        foreach (UstyePlanData.Structure structure in _plan.Structures)
        {
            Vector2 min = new(structure.Min.X, structure.Min.Y);
            Vector2 size = new(structure.Size.X, structure.Size.Y);
            Vector2 center = min + size * 0.5f;
            float baseElevation = _plan.SampleTerrainElevation(center.X, center.Y);

            Node3D node = new() { Name = $"Structure_{structure.Id}" };
            _structuresRoot.AddChild(node);

            if (structure.Kind == "panel_block")
            {
                MeshInstance3D blockMesh = new()
                {
                    Name = "BlockBody",
                    Mesh = new BoxMesh { Size = new Vector3(size.X, structure.Height, size.Y) },
                    MaterialOverride = panelBlockMat,
                    Position = new Vector3(center.X, baseElevation + structure.Height * 0.5f, center.Y)
                };
                node.AddChild(blockMesh);

                for (int floor = 1; floor <= 4; floor++)
                {
                    float yBand = baseElevation + floor * (structure.Height / 5f);
                    MeshInstance3D bandNorth = new()
                    {
                        Name = $"WindowBand_N_{floor}",
                        Mesh = new BoxMesh { Size = new Vector3(size.X * 0.92f, 1.1f, 0.15f) },
                        MaterialOverride = windowBandMat,
                        Position = new Vector3(center.X, yBand, center.Y - size.Y * 0.5f - 0.08f)
                    };
                    node.AddChild(bandNorth);

                    MeshInstance3D bandSouth = new()
                    {
                        Name = $"WindowBand_S_{floor}",
                        Mesh = new BoxMesh { Size = new Vector3(size.X * 0.92f, 1.1f, 0.15f) },
                        MaterialOverride = windowBandMat,
                        Position = new Vector3(center.X, yBand, center.Y + size.Y * 0.5f + 0.08f)
                    };
                    node.AddChild(bandSouth);
                }

                MeshInstance3D roofBorder = new()
                {
                    Name = "RoofParapet",
                    Mesh = new BoxMesh { Size = new Vector3(size.X + 0.4f, 0.6f, size.Y + 0.4f) },
                    MaterialOverride = panelBlockMat,
                    Position = new Vector3(center.X, baseElevation + structure.Height + 0.3f, center.Y)
                };
                node.AddChild(roofBorder);
            }
            else if (structure.Kind == "warehouse")
            {
                float wallHeight = structure.Height * 0.70f;
                float roofHeight = structure.Height * 0.30f;

                MeshInstance3D wallMesh = new()
                {
                    Name = "WarehouseWalls",
                    Mesh = new BoxMesh { Size = new Vector3(size.X, wallHeight, size.Y) },
                    MaterialOverride = warehouseWallMat,
                    Position = new Vector3(center.X, baseElevation + wallHeight * 0.5f, center.Y)
                };
                node.AddChild(wallMesh);

                MeshInstance3D roofMesh = new()
                {
                    Name = "WarehouseRoof",
                    Mesh = new PrismMesh { Size = new Vector3(size.X + 0.8f, roofHeight, size.Y + 0.8f) },
                    MaterialOverride = warehouseRoofMat,
                    Position = new Vector3(center.X, baseElevation + wallHeight + roofHeight * 0.5f, center.Y)
                };
                node.AddChild(roofMesh);
            }
            else if (structure.Kind == "log_stack")
            {
                MeshInstance3D logStack = new()
                {
                    Name = "LogPile",
                    Mesh = new BoxMesh { Size = new Vector3(size.X, structure.Height, size.Y) },
                    MaterialOverride = logMat,
                    Position = new Vector3(center.X, baseElevation + structure.Height * 0.5f, center.Y)
                };
                node.AddChild(logStack);
            }
            else if (structure.Kind == "water_tower")
            {
                float towerHeight = structure.Height * 0.75f;
                float tankHeight = structure.Height * 0.25f;

                MeshInstance3D towerLeg = new()
                {
                    Name = "TowerBody",
                    Mesh = new CylinderMesh { TopRadius = 2.4f, BottomRadius = 3.6f, Height = towerHeight },
                    MaterialOverride = waterTowerMat,
                    Position = new Vector3(center.X, baseElevation + towerHeight * 0.5f, center.Y)
                };
                node.AddChild(towerLeg);

                MeshInstance3D tank = new()
                {
                    Name = "WaterTank",
                    Mesh = new CylinderMesh { TopRadius = 4.8f, BottomRadius = 4.8f, Height = tankHeight },
                    MaterialOverride = waterTankMat,
                    Position = new Vector3(center.X, baseElevation + towerHeight + tankHeight * 0.5f, center.Y)
                };
                node.AddChild(tank);
            }
        }
    }

    private void SetupLocalContext()
    {
        _contextRoot = new Node3D { Name = "LocalContextRoot" };
        AddChild(_contextRoot);

        StandardMaterial3D concretePadMat = new()
        {
            AlbedoColor = new Color("555759"),
            Roughness = 0.90f
        };

        StandardMaterial3D timberPalletMat = new()
        {
            AlbedoColor = new Color("654c30"),
            Roughness = 0.92f
        };

        StandardMaterial3D earthenBermMat = new()
        {
            AlbedoColor = new Color("4a473c"),
            Roughness = 0.96f
        };

        foreach (UstyePlanData.Structure st in _plan.Structures)
        {
            if (st.Kind == "panel_block")
            {
                Vector2 center = new Vector2(st.Min.X, st.Min.Y) + new Vector2(st.Size.X, st.Size.Y) * 0.5f;
                float y = _plan.SampleTerrainElevation(center.X, center.Y);

                MeshInstance3D pad = new()
                {
                    Name = $"Courtyard_{st.Id}",
                    Mesh = new BoxMesh { Size = new Vector3(st.Size.X + 8f, 0.15f, st.Size.Y + 8f) },
                    MaterialOverride = concretePadMat,
                    Position = new Vector3(center.X, y + 0.05f, center.Y)
                };
                _contextRoot.AddChild(pad);
            }
        }

        Vector3[] lumberPiles =
        [
            new(2470f, 0f, 3620f),
            new(2490f, 0f, 3635f),
            new(2510f, 0f, 3650f)
        ];
        for (int i = 0; i < lumberPiles.Length; i++)
        {
            Vector3 pos = lumberPiles[i];
            float y = _plan.SampleTerrainElevation(pos.X, pos.Z);
            MeshInstance3D pile = new()
            {
                Name = $"LumberPile_{i}",
                Mesh = new BoxMesh { Size = new Vector3(18f, 2.2f, 6.5f) },
                MaterialOverride = timberPalletMat,
                Position = new Vector3(pos.X, y + 1.1f, pos.Z),
                Rotation = new Vector3(0f, 0.2f * i, 0f)
            };
            _contextRoot.AddChild(pile);
        }

        Vector3[] depotBerms =
        [
            new(3440f, 0f, 2660f),
            new(3570f, 0f, 2660f),
            new(3440f, 0f, 2790f),
            new(3570f, 0f, 2790f)
        ];
        for (int i = 0; i < depotBerms.Length; i++)
        {
            Vector3 pos = depotBerms[i];
            float y = _plan.SampleTerrainElevation(pos.X, pos.Z);
            MeshInstance3D berm = new()
            {
                Name = $"DepotBerm_{i}",
                Mesh = new BoxMesh { Size = new Vector3(85f, 2.8f, 5.0f) },
                MaterialOverride = earthenBermMat,
                Position = new Vector3(pos.X, y + 1.4f, pos.Z)
            };
            _contextRoot.AddChild(berm);
        }
    }

    private void SetupLandmarkSites()
    {
        _landmarksRoot = new Node3D { Name = "LandmarksRoot" };
        AddChild(_landmarksRoot);

        StandardMaterial3D stoneChurchMat = new()
        {
            AlbedoColor = new Color("c8c4bc"),
            Roughness = 0.92f
        };

        StandardMaterial3D churchRoofMat = new()
        {
            AlbedoColor = new Color("586356"),
            Roughness = 0.75f
        };

        StandardMaterial3D copperSpireMat = new()
        {
            AlbedoColor = new Color("427a65"),
            Roughness = 0.50f,
            Metallic = 0.35f
        };

        StandardMaterial3D redMastMat = new()
        {
            AlbedoColor = new Color("c4352b"),
            Roughness = 0.65f,
            Metallic = 0.25f
        };

        StandardMaterial3D whiteMastMat = new()
        {
            AlbedoColor = new Color("e8e8ea"),
            Roughness = 0.65f
        };

        StandardMaterial3D beaconMat = new()
        {
            AlbedoColor = new Color("ff3322"),
            EmissionEnabled = true,
            Emission = new Color("ff2211"),
            EmissionEnergyMultiplier = 3.0f
        };

        foreach (UstyePlanData.Site site in _plan.Sites)
        {
            Vector2 pos = new(site.Position.X, site.Position.Y);
            float baseElevation = _plan.SampleTerrainElevation(pos.X, pos.Y);

            if (site.Id == "OBS-W")
            {
                Node3D church = new() { Name = "Landmark_Church" };
                _landmarksRoot.AddChild(church);

                MeshInstance3D nave = new()
                {
                    Name = "Nave",
                    Mesh = new BoxMesh { Size = new Vector3(32f, 12f, 16f) },
                    MaterialOverride = stoneChurchMat,
                    Position = new Vector3(pos.X, baseElevation + 6f, pos.Y)
                };
                church.AddChild(nave);

                MeshInstance3D roof = new()
                {
                    Name = "NaveRoof",
                    Mesh = new BoxMesh { Size = new Vector3(33f, 5f, 17f) },
                    MaterialOverride = churchRoofMat,
                    Position = new Vector3(pos.X, baseElevation + 14.5f, pos.Y)
                };
                church.AddChild(roof);

                MeshInstance3D tower = new()
                {
                    Name = "BellTower",
                    Mesh = new BoxMesh { Size = new Vector3(9f, 22f, 9f) },
                    MaterialOverride = stoneChurchMat,
                    Position = new Vector3(pos.X - 16f, baseElevation + 11f, pos.Y)
                };
                church.AddChild(tower);

                MeshInstance3D spire = new()
                {
                    Name = "Spire",
                    Mesh = new CylinderMesh { TopRadius = 0.2f, BottomRadius = 4.8f, Height = 9f },
                    MaterialOverride = copperSpireMat,
                    Position = new Vector3(pos.X - 16f, baseElevation + 26.5f, pos.Y)
                };
                church.AddChild(spire);
            }
            else if (site.Id == "OBS-E")
            {
                Node3D mast = new() { Name = "Landmark_RadioMast" };
                _landmarksRoot.AddChild(mast);

                int sections = 6;
                float secHeight = 6.0f;
                for (int s = 0; s < sections; s++)
                {
                    float ySec = baseElevation + s * secHeight + secHeight * 0.5f;
                    float width = Mathf.Lerp(2.8f, 1.2f, (float)s / sections);
                    MeshInstance3D seg = new()
                    {
                        Name = $"MastSegment_{s}",
                        Mesh = new BoxMesh { Size = new Vector3(width, secHeight, width) },
                        MaterialOverride = s % 2 == 0 ? redMastMat : whiteMastMat,
                        Position = new Vector3(pos.X, ySec, pos.Y)
                    };
                    mast.AddChild(seg);
                }

                MeshInstance3D crossArm = new()
                {
                    Name = "AntennaCrossArm",
                    Mesh = new BoxMesh { Size = new Vector3(10f, 0.4f, 0.4f) },
                    MaterialOverride = whiteMastMat,
                    Position = new Vector3(pos.X, baseElevation + sections * secHeight - 1.5f, pos.Y)
                };
                mast.AddChild(crossArm);

                MeshInstance3D beacon = new()
                {
                    Name = "MastBeacon",
                    Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.8f, 0.8f) },
                    MaterialOverride = beaconMat,
                    Position = new Vector3(pos.X, baseElevation + sections * secHeight + 0.5f, pos.Y)
                };
                mast.AddChild(beacon);
            }
            else if (site.Id == "LIGHT")
            {
                Node3D lighthouse = new() { Name = "Landmark_Lighthouse" };
                _landmarksRoot.AddChild(lighthouse);

                float towerH = 20f;
                MeshInstance3D tower = new()
                {
                    Name = "LighthouseTower",
                    Mesh = new CylinderMesh { TopRadius = 3.6f, BottomRadius = 5.2f, Height = towerH },
                    MaterialOverride = whiteMastMat,
                    Position = new Vector3(pos.X, baseElevation + towerH * 0.5f, pos.Y)
                };
                lighthouse.AddChild(tower);

                MeshInstance3D gallery = new()
                {
                    Name = "GalleryRing",
                    Mesh = new CylinderMesh { TopRadius = 4.6f, BottomRadius = 4.6f, Height = 0.8f },
                    MaterialOverride = redMastMat,
                    Position = new Vector3(pos.X, baseElevation + towerH + 0.4f, pos.Y)
                };
                lighthouse.AddChild(gallery);

                MeshInstance3D lantern = new()
                {
                    Name = "LanternHouse",
                    Mesh = new CylinderMesh { TopRadius = 2.8f, BottomRadius = 2.8f, Height = 3.5f },
                    MaterialOverride = beaconMat,
                    Position = new Vector3(pos.X, baseElevation + towerH + 2.5f, pos.Y)
                };
                lighthouse.AddChild(lantern);

                MeshInstance3D dome = new()
                {
                    Name = "LanternDome",
                    Mesh = new CylinderMesh { TopRadius = 0.3f, BottomRadius = 3.0f, Height = 1.8f },
                    MaterialOverride = redMastMat,
                    Position = new Vector3(pos.X, baseElevation + towerH + 4.8f, pos.Y)
                };
                lighthouse.AddChild(dome);
            }
        }
    }

    private static ArrayMesh CreateOffsetMesh(PrimitiveMesh primitive, Vector3 offset)
    {
        using Godot.Collections.Array arrays = primitive.GetMeshArrays();
        Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] += offset;
        }
        arrays[(int)Mesh.ArrayType.Vertex] = verts;

        ArrayMesh mesh = new();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static Mesh BuildConiferTreeMesh()
    {
        CylinderMesh primitive = new()
        {
            TopRadius = 0.15f,
            BottomRadius = 3.2f,
            Height = 13.0f,
            RadialSegments = 7,
            Rings = 1
        };
        return CreateOffsetMesh(primitive, new Vector3(0f, 13.0f * 0.5f, 0f));
    }

    private static Mesh BuildDeciduousTreeMesh()
    {
        SphereMesh primitive = new()
        {
            Radius = 4.0f,
            Height = 8.5f,
            RadialSegments = 8,
            Rings = 5
        };
        return CreateOffsetMesh(primitive, new Vector3(0f, 8.5f * 0.5f, 0f));
    }

    private void SetupVegetation()
    {
        _vegetationRoot = new Node3D { Name = "VegetationRoot" };
        AddChild(_vegetationRoot);

        _coniferTreeMesh = BuildConiferTreeMesh();
        StandardMaterial3D coniferMat = new()
        {
            AlbedoColor = new Color("234226"),
            Roughness = 0.95f
        };

        _deciduousTreeMesh = BuildDeciduousTreeMesh();
        StandardMaterial3D deciduousMat = new()
        {
            AlbedoColor = new Color("42562c"),
            Roughness = 0.95f
        };

        const float chunkSize = 500f;
        int chunkCols = (int)(_plan.ExtentX / chunkSize);
        int chunkRows = (int)(_plan.ExtentZ / chunkSize);

        List<Transform3D>[,] coniferBuckets = new List<Transform3D>[chunkCols, chunkRows];
        List<Transform3D>[,] deciduousBuckets = new List<Transform3D>[chunkCols, chunkRows];

        for (int cz = 0; cz < chunkRows; cz++)
        {
            for (int cx = 0; cx < chunkCols; cx++)
            {
                coniferBuckets[cx, cz] = [];
                deciduousBuckets[cx, cz] = [];
            }
        }

        foreach (UstyePlanData.Region region in _plan.Regions)
        {
            Vector2[] poly = new Vector2[region.Polygon.Length];
            for (int i = 0; i < region.Polygon.Length; i++)
                poly[i] = new Vector2(region.Polygon[i].X, region.Polygon[i].Y);

            if (region.Kind == "dense_forest")
            {
                CollectChunkedTrees(poly, step: 26f, chunkSize, chunkCols, chunkRows, coniferBuckets);
            }
            else if (region.Kind == "sparse_forest")
            {
                CollectChunkedTrees(poly, step: 42f, chunkSize, chunkCols, chunkRows, deciduousBuckets);
            }
        }

        Vector2[] townCourtyardTrees =
        [
            new(2120f, 2400f), new(2190f, 2405f),
            new(2110f, 2505f), new(2185f, 2515f),
            new(3010f, 2400f), new(3090f, 2405f),
            new(3015f, 2505f), new(3085f, 2515f)
        ];
        foreach (Vector2 p in townCourtyardTrees)
        {
            float y = _plan.SampleTerrainElevation(p.X, p.Y);
            int cx = Math.Clamp((int)(p.X / chunkSize), 0, chunkCols - 1);
            int cz = Math.Clamp((int)(p.Y / chunkSize), 0, chunkRows - 1);
            Transform3D t = new(Basis.Identity.Scaled(Vector3.One * 0.9f), new Vector3(p.X, y, p.Y));
            deciduousBuckets[cx, cz].Add(t);
        }

        _coniferMultiMeshes.Clear();
        _deciduousMultiMeshes.Clear();

        for (int cz = 0; cz < chunkRows; cz++)
        {
            for (int cx = 0; cx < chunkCols; cx++)
            {
                List<Transform3D> conifers = coniferBuckets[cx, cz];
                if (conifers.Count > 0)
                {
                    MultiMesh mm = new()
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        Mesh = _coniferTreeMesh,
                        InstanceCount = conifers.Count
                    };
                    for (int i = 0; i < conifers.Count; i++)
                    {
                        mm.SetInstanceTransform(i, conifers[i]);
                    }

                    MultiMeshInstance3D mmi = new()
                    {
                        Name = $"Conifers_{cx}_{cz}",
                        Multimesh = mm,
                        MaterialOverride = coniferMat
                    };
                    _vegetationRoot.AddChild(mmi);
                    _coniferMultiMeshes.Add(mmi);
                }

                List<Transform3D> deciduous = deciduousBuckets[cx, cz];
                if (deciduous.Count > 0)
                {
                    MultiMesh mm = new()
                    {
                        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                        Mesh = _deciduousTreeMesh,
                        InstanceCount = deciduous.Count
                    };
                    for (int i = 0; i < deciduous.Count; i++)
                    {
                        mm.SetInstanceTransform(i, deciduous[i]);
                    }

                    MultiMeshInstance3D mmi = new()
                    {
                        Name = $"Deciduous_{cx}_{cz}",
                        Multimesh = mm,
                        MaterialOverride = deciduousMat
                    };
                    _vegetationRoot.AddChild(mmi);
                    _deciduousMultiMeshes.Add(mmi);
                }
            }
        }
    }

    private bool IsInClearing(Vector2 pt)
    {
        foreach (UstyePlanData.Region reg in _plan.Regions)
        {
            if (reg.Kind != "dense_forest" && reg.Kind != "sparse_forest")
            {
                if (UstyePlanData.PointInPolygon(new NumericsVector2(pt.X, pt.Y), reg.Polygon))
                    return true;
            }
        }
        foreach (UstyePlanData.Structure st in _plan.Structures)
        {
            if (pt.X >= st.Min.X - 12f && pt.X <= st.Min.X + st.Size.X + 12f &&
                pt.Y >= st.Min.Y - 12f && pt.Y <= st.Min.Y + st.Size.Y + 12f)
            {
                return true;
            }
        }
        foreach (UstyePlanData.Road road in _plan.Roads)
        {
            if (UstyePlanData.DistanceToPolyline(new NumericsVector2(pt.X, pt.Y), road.Points) < 14f)
                return true;
        }
        foreach (UstyePlanData.Road access in _plan.AccessRoads)
        {
            if (UstyePlanData.DistanceToPolyline(new NumericsVector2(pt.X, pt.Y), access.Points) < 14f)
                return true;
        }
        return false;
    }

    private void CollectChunkedTrees(
        Vector2[] polygon,
        float step,
        float chunkSize,
        int chunkCols,
        int chunkRows,
        List<Transform3D>[,] buckets)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (Vector2 p in polygon)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minZ) minZ = p.Y;
            if (p.Y > maxZ) maxZ = p.Y;
        }

        minX = MathF.Max(0f, minX);
        maxX = MathF.Min(_plan.ExtentX, maxX);
        minZ = MathF.Max(0f, minZ);
        maxZ = MathF.Min(_plan.ExtentZ, maxZ);

        for (float z = minZ; z <= maxZ; z += step)
        {
            for (float x = minX; x <= maxX; x += step)
            {
                float jX = (SmoothNoise2D(x + 13f, z - 37f, 0.05f) - 0.5f) * (step * 0.7f);
                float jZ = (SmoothNoise2D(x - 47f, z + 29f, 0.05f) - 0.5f) * (step * 0.7f);
                Vector2 samplePt = new(x + jX, z + jZ);

                NumericsVector2[] numPoly = new NumericsVector2[polygon.Length];
                for (int i = 0; i < polygon.Length; i++) numPoly[i] = new(polygon[i].X, polygon[i].Y);

                if (UstyePlanData.PointInPolygon(new NumericsVector2(samplePt.X, samplePt.Y), numPoly) && !IsInClearing(samplePt))
                {
                    float y = _plan.SampleTerrainElevation(samplePt.X, samplePt.Y);
                    if (y > 2.2f)
                    {
                        int cx = Math.Clamp((int)(samplePt.X / chunkSize), 0, chunkCols - 1);
                        int cz = Math.Clamp((int)(samplePt.Y / chunkSize), 0, chunkRows - 1);

                        float scale = 0.85f + 0.35f * SmoothNoise2D(samplePt.X, samplePt.Y, 0.08f);
                        float rot = SmoothNoise2D(samplePt.X + 100f, samplePt.Y, 0.03f) * MathF.PI * 2f;

                        Basis basis = Basis.Identity.Rotated(Vector3.Up, rot).Scaled(Vector3.One * scale);
                        Transform3D t = new(basis, new Vector3(samplePt.X, y, samplePt.Y));
                        buckets[cx, cz].Add(t);
                    }
                }
            }
        }
    }

    private void SetupScaleVehicles()
    {
        _vehiclesRoot = new Node3D { Name = "ScaleVehiclesRoot" };
        AddChild(_vehiclesRoot);
        _scaleVehicles.Clear();

        PlaceScaleVehicle("t62", "soviet", new Vector3(2160f, 0f, 2435f), -0.1f);
        PlaceScaleVehicle("btr70", "soviet", new Vector3(2140f, 0f, 2465f), 0.2f);
        PlaceScaleVehicle("brdm2", null, new Vector3(2500f, 0f, 2450f), 0f);
        PlaceScaleVehicle("m60a1", "us", new Vector3(2800f, 0f, 2450f), MathF.PI);
        PlaceScaleVehicle("m60a1", "us", new Vector3(3050f, 0f, 2435f), MathF.PI + 0.15f);
        PlaceScaleVehicle("btr70", "soviet", new Vector3(2530f, 0f, 3630f), 0.8f);

        UpdateVehicleLods(4500f);
    }

    private void PlaceScaleVehicle(string id, string? faction, Vector3 pos, float yaw)
    {
        VehicleVisual? visual = VehicleVisualCatalog.InstantiateVisual(id, faction);
        if (visual is null) return;

        Node3D root = visual.Root;
        float groundY = _plan.SampleTerrainElevation(pos.X, pos.Z);
        Vector3 groundPos = new(pos.X, groundY, pos.Z);
        root.Position = groundPos;
        root.Rotation = new Vector3(0f, yaw, 0f);
        _vehiclesRoot?.AddChild(root);

        _scaleVehicles.Add(new ScaleVehicle(id, faction, visual, groundPos));
    }

    public void UpdateVehicleLods(float cameraSize)
    {
        foreach (ScaleVehicle sv in _scaleVehicles)
        {
            sv.UpdateLod(cameraSize);
        }
    }

    public bool HasUpwardFrontFaces()
    {
        if (_terrainRoot is null)
        {
            SetupTerrain();
        }
        if (_terrainRoot is null || _terrainRoot.GetChildCount() == 0) return false;
        if (_terrainRoot.GetChild(0) is not MeshInstance3D firstChunk) return false;
        ArrayMesh? mesh = firstChunk.Mesh as ArrayMesh;
        if (mesh is null || mesh.GetSurfaceCount() == 0) return false;
        using Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        if (verts.Length < 3 || indices.Length < 3) return false;

        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a = verts[indices[i]], b = verts[indices[i + 1]], c = verts[indices[i + 2]];
            if ((b - a).Cross(c - a).Y >= 0) return false;
        }
        return indices.Length > 0;
    }
}
