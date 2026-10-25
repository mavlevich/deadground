using System.IO;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Headless smoke and regression tests for the Ustye map preview.
/// Validates plan data loading, dimensions, terrain chunks, watercourses, crossings,
/// structures, landmark sites, vegetation meshes and chunking, scale vehicles and LODs, and camera navigation.
/// </summary>
public static class UstyeMapPreviewRegressionTests
{
    public static bool Run(UstyeMapPreview preview)
    {
        try
        {
            CheckPlanGeometry(preview.Plan);
            CheckPlanDimensionVariations();
            CheckTerrainChunks(preview);
            CheckBayCoastContinuity(preview.Plan);
            CheckWaterAndCrossings(preview);
            CheckRoadAndBridgeIntegration(preview);
            CheckGroundColoringSmoothness();
            CheckStructuresAndLandmarks(preview);
            CheckVegetation(preview);
            CheckScaleVehicles(preview);
            CheckCameraNavigation(preview);
            CheckCameraInput(preview);

            GD.Print("USTYE_MAP_PREVIEW_SMOKE_OK: All geometry, terrain chunks, crossings, structures, landmarks, vegetation, and camera targets verified.");
            return true;
        }
        catch (Exception ex)
        {
            GD.PushError($"USTYE_MAP_PREVIEW_REGRESSION_FAILED: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckPlanGeometry(UstyePlanData plan)
    {
        Require(plan.ExtentX == 5000f && plan.ExtentZ == 5000f,
            $"Expected extent 5000 x 5000 m, got {plan.ExtentX} x {plan.ExtentZ}");
        Require(Mathf.IsEqualApprox(plan.BaseHeight, 4.0f),
            $"Expected base height 4.0 m, got {plan.BaseHeight}");
        Require(plan.Peaks.Length == 3,
            $"Expected 3 height model peaks, got {plan.Peaks.Length}");

        // Check Gaussian peak calculations
        float churchRidgeHeight = plan.SampleHeight(1700f, 1350f);
        Require(churchRidgeHeight >= 34f + 4f - 0.5f,
            $"Expected peak 1 height >= 37.5 m, got {churchRidgeHeight}");

        float radioHillHeight = plan.SampleHeight(3800f, 3380f);
        Require(radioHillHeight >= 41f + 4f - 0.5f,
            $"Expected peak 2 height >= 44.5 m, got {radioHillHeight}");

        float cornerHeight = plan.SampleHeight(50f, 50f);
        Require(Mathf.IsEqualApprox(cornerHeight, 4.0f, 0.2f),
            $"Expected corner height near base 4.0 m, got {cornerHeight}");

        // Check road widths read from plan
        UstyePlanData.Road? centralRoad = plan.Roads.FirstOrDefault(r => r.Id == "central");
        UstyePlanData.Road? northRoad = plan.Roads.FirstOrDefault(r => r.Id == "north");
        UstyePlanData.Road? southRoad = plan.Roads.FirstOrDefault(r => r.Id == "south");
        UstyePlanData.Road? footRoad = plan.Roads.FirstOrDefault(r => r.Id == "foot");
        UstyePlanData.Road? depotAccess = plan.AccessRoads.FirstOrDefault(r => r.Id == "ACCESS-DEPOT");
        UstyePlanData.Road? millAccess = plan.AccessRoads.FirstOrDefault(r => r.Id == "ACCESS-MILL");

        Require(centralRoad is not null && Mathf.IsEqualApprox(centralRoad.Width, 14f),
            $"Expected central road width 14 m, got {centralRoad?.Width}");
        Require(northRoad is not null && Mathf.IsEqualApprox(northRoad.Width, 8f),
            $"Expected north road width 8 m, got {northRoad?.Width}");
        Require(southRoad is not null && Mathf.IsEqualApprox(southRoad.Width, 6f),
            $"Expected south road width 6 m, got {southRoad?.Width}");
        Require(footRoad is not null && Mathf.IsEqualApprox(footRoad.Width, 2f),
            $"Expected foot path width 2 m, got {footRoad?.Width}");
        Require(depotAccess is not null && Mathf.IsEqualApprox(depotAccess.Width, 8f),
            $"Expected depot access width 8 m, got {depotAccess?.Width}");
        Require(millAccess is not null && Mathf.IsEqualApprox(millAccess.Width, 6f),
            $"Expected sawmill access width 6 m, got {millAccess?.Width}");

        // Check water properties
        Require(Mathf.IsEqualApprox(plan.RiverWidth, 100f), $"Expected river width 100 m, got {plan.RiverWidth}");
        Require(Mathf.IsEqualApprox(plan.BankSlope, 24f), $"Expected bank slope 24 m, got {plan.BankSlope}");
    }

    private static void CheckPlanDimensionVariations()
    {
        string baseJson;
        const string resPath = "res://Maps/ustye/layout.json";
        if (Godot.FileAccess.FileExists(resPath))
        {
            baseJson = Godot.FileAccess.GetFileAsString(resPath);
        }
        else
        {
            baseJson = File.ReadAllText(UstyePlanData.ResolvePlanPath());
        }

        // 1. Verify that modifying river width in plan data modifies terrain elevation derivation
        string modifiedRiverJson = baseJson.Replace("\"river_width_m\": 100", "\"river_width_m\": 60");
        UstyePlanData planNarrowRiver = UstyePlanData.Parse(modifiedRiverJson);
        Require(Mathf.IsEqualApprox(planNarrowRiver.RiverWidth, 60f), "Modified river width must be parsed.");

        // At distance 40 m from river centerline:
        // In standard plan (river_width = 100m, halfWidth = 48m): dist 40m is within riverbed (h = 0.8m)
        // In narrow plan (river_width = 60m, halfWidth = 28.8m, bankSlope = 24m): dist 40m is on sloping bank (> 0.8m)
        float hStandard = UstyePlanData.Parse(baseJson).SampleTerrainElevation(2650f + 40f, 2450f);
        float hNarrow = planNarrowRiver.SampleTerrainElevation(2650f + 40f, 2450f);
        Require(hNarrow > hStandard,
            $"Narrower river must elevate bank terrain earlier (standard={hStandard}, narrow={hNarrow})");

        // 2. Verify that invalid road width throws InvalidDataException
        string invalidRoadJson = baseJson.Replace("\"width_m\": 14", "\"width_m\": -5");
        bool threwInvalidRoad = false;
        try
        {
            UstyePlanData.Parse(invalidRoadJson);
        }
        catch (InvalidDataException)
        {
            threwInvalidRoad = true;
        }
        Require(threwInvalidRoad, "Negative road width must throw InvalidDataException.");

        // 3. Verify that invalid river width throws InvalidDataException
        string invalidRiverJson = baseJson.Replace("\"river_width_m\": 100", "\"river_width_m\": 0");
        bool threwInvalidRiver = false;
        try
        {
            UstyePlanData.Parse(invalidRiverJson);
        }
        catch (InvalidDataException)
        {
            threwInvalidRiver = true;
        }
        Require(threwInvalidRiver, "Zero river width must throw InvalidDataException.");
    }

    private static void CheckTerrainChunks(UstyeMapPreview preview)
    {
        Node3D terrainRoot = preview.TerrainRoot;
        Require(terrainRoot is not null, "TerrainRoot node is missing");

        int chunkCount = terrainRoot!.GetChildCount();
        Require(chunkCount == 100, $"Expected 100 terrain chunks (10x10), got {chunkCount}");

        int totalVertices = 0;
        int totalTriangles = 0;
        int fineChunkCount = 0;
        int coarseChunkCount = 0;

        for (int cz = 0; cz < 10; cz++)
        {
            for (int cx = 0; cx < 10; cx++)
            {
                Node? child = terrainRoot.FindChild($"TerrainChunk_{cx}_{cz}", false, false);
                Require(child is MeshInstance3D, $"Terrain chunk TerrainChunk_{cx}_{cz} is missing or not a MeshInstance3D");
                MeshInstance3D chunk = (MeshInstance3D)child!;
                Require(chunk.Mesh is ArrayMesh, $"Terrain chunk {chunk.Name} mesh is not an ArrayMesh");
                ArrayMesh mesh = (ArrayMesh)chunk.Mesh!;
                Require(mesh.GetSurfaceCount() > 0, $"Terrain chunk {chunk.Name} has no surfaces");

                using Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
                Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

                bool isFine = preview.IsRiparianChunk(cx, cz);
                if (isFine)
                {
                    fineChunkCount++;
                    Require(verts.Length == 81 * 81, $"Fine chunk {chunk.Name} expected 6561 vertices (80x80), got {verts.Length}");
                    Require(indices.Length == 80 * 80 * 6, $"Fine chunk {chunk.Name} expected 38400 indices, got {indices.Length}");
                }
                else
                {
                    coarseChunkCount++;
                    Require(verts.Length == 21 * 21, $"Coarse chunk {chunk.Name} expected 441 vertices (20x20), got {verts.Length}");
                    Require(indices.Length == 20 * 20 * 6, $"Coarse chunk {chunk.Name} expected 2400 indices, got {indices.Length}");
                }

                for (int i = 0; i < indices.Length; i += 3)
                {
                    Vector3 a = verts[indices[i]];
                    Vector3 b = verts[indices[i + 1]];
                    Vector3 c = verts[indices[i + 2]];
                    Vector3 cross = (b - a).Cross(c - a);
                    Require(cross.Y <= 0.001f, $"Terrain triangle {i / 3} in {chunk.Name} is not facing upward");
                }

                totalVertices += verts.Length;
                totalTriangles += indices.Length / 3;
            }
        }

        Require(fineChunkCount > 0, "Expected at least one fine riparian chunk");
        Require(coarseChunkCount > 0, "Expected at least one coarse inland chunk");
        Require(fineChunkCount + coarseChunkCount == 100, "Expected 100 chunks in total");
        Require(totalVertices == fineChunkCount * 6561 + coarseChunkCount * 441, "Total vertex count mismatch");
        Require(totalTriangles == fineChunkCount * 12800 + coarseChunkCount * 800, "Total triangle count mismatch");

        // Verify zero-gap chunk boundary seams
        CheckTerrainChunkSeams(preview);
    }

    private static void CheckTerrainChunkSeams(UstyeMapPreview preview)
    {
        Node3D terrainRoot = preview.TerrainRoot;

        // Check horizontal seams: chunk (cx, cz) and chunk (cx + 1, cz)
        for (int cz = 0; cz < 10; cz++)
        {
            for (int cx = 0; cx < 9; cx++)
            {
                MeshInstance3D chunkA = (MeshInstance3D)terrainRoot.FindChild($"TerrainChunk_{cx}_{cz}", false, false)!;
                MeshInstance3D chunkB = (MeshInstance3D)terrainRoot.FindChild($"TerrainChunk_{cx + 1}_{cz}", false, false)!;

                using Godot.Collections.Array arrA = ((ArrayMesh)chunkA.Mesh!).SurfaceGetArrays(0);
                using Godot.Collections.Array arrB = ((ArrayMesh)chunkB.Mesh!).SurfaceGetArrays(0);
                Vector3[] vertsA = arrA[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                Vector3[] vertsB = arrB[(int)Mesh.ArrayType.Vertex].AsVector3Array();

                bool fineA = preview.IsRiparianChunk(cx, cz);
                bool fineB = preview.IsRiparianChunk(cx + 1, cz);

                int colsA = fineA ? 81 : 21;
                int colsB = fineB ? 81 : 21;

                if (fineA == fineB)
                {
                    int rows = colsA;
                    for (int lz = 0; lz < rows; lz++)
                    {
                        Vector3 vA = vertsA[lz * colsA + (colsA - 1)];
                        Vector3 vB = vertsB[lz * colsB + 0];
                        Require(Mathf.IsEqualApprox(vA.X, vB.X, 0.01f), $"Seam X mismatch at ({cx},{cz}): vA={vA.X}, vB={vB.X}");
                        Require(Mathf.IsEqualApprox(vA.Z, vB.Z, 0.01f), $"Seam Z mismatch at ({cx},{cz}): vA={vA.Z}, vB={vB.Z}");
                        Require(Mathf.Abs(vA.Y - vB.Y) < 0.005f, $"Elevation gap between ({cx},{cz}) and ({cx + 1},{cz}) at Z={vA.Z}: A={vA.Y}, B={vB.Y}");
                    }
                }
                else if (fineA && !fineB)
                {
                    for (int lzFine = 0; lzFine <= 80; lzFine++)
                    {
                        Vector3 vA = vertsA[lzFine * 81 + 80];
                        int k = lzFine / 4;
                        int rem = lzFine % 4;
                        Vector3 vB0 = vertsB[k * 21 + 0];
                        Vector3 vB1 = vertsB[Math.Min(k + 1, 20) * 21 + 0];
                        float expectedY = Mathf.Lerp(vB0.Y, vB1.Y, rem / 4f);
                        Require(Mathf.Abs(vA.Y - expectedY) < 0.005f,
                            $"Elevation gap on fine-coarse seam between ({cx},{cz}) and ({cx + 1},{cz}) at Z={vA.Z}: A={vA.Y}, expected={expectedY}");
                    }
                }
                else
                {
                    for (int lzFine = 0; lzFine <= 80; lzFine++)
                    {
                        Vector3 vB = vertsB[lzFine * 81 + 0];
                        int k = lzFine / 4;
                        int rem = lzFine % 4;
                        Vector3 vA0 = vertsA[k * 21 + 20];
                        Vector3 vA1 = vertsA[Math.Min(k + 1, 20) * 21 + 20];
                        float expectedY = Mathf.Lerp(vA0.Y, vA1.Y, rem / 4f);
                        Require(Mathf.Abs(vB.Y - expectedY) < 0.005f,
                            $"Elevation gap on coarse-fine seam between ({cx},{cz}) and ({cx + 1},{cz}) at Z={vB.Z}: B={vB.Y}, expected={expectedY}");
                    }
                }
            }
        }

        // Check vertical seams: chunk (cx, cz) and chunk (cx, cz + 1)
        for (int cz = 0; cz < 9; cz++)
        {
            for (int cx = 0; cx < 10; cx++)
            {
                MeshInstance3D chunkA = (MeshInstance3D)terrainRoot.FindChild($"TerrainChunk_{cx}_{cz}", false, false)!;
                MeshInstance3D chunkB = (MeshInstance3D)terrainRoot.FindChild($"TerrainChunk_{cx}_{cz + 1}", false, false)!;

                using Godot.Collections.Array arrA = ((ArrayMesh)chunkA.Mesh!).SurfaceGetArrays(0);
                using Godot.Collections.Array arrB = ((ArrayMesh)chunkB.Mesh!).SurfaceGetArrays(0);
                Vector3[] vertsA = arrA[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                Vector3[] vertsB = arrB[(int)Mesh.ArrayType.Vertex].AsVector3Array();

                bool fineA = preview.IsRiparianChunk(cx, cz);
                bool fineB = preview.IsRiparianChunk(cx, cz + 1);

                int colsA = fineA ? 81 : 21;
                int colsB = fineB ? 81 : 21;
                int rowsA = colsA;

                if (fineA == fineB)
                {
                    for (int lx = 0; lx < colsA; lx++)
                    {
                        Vector3 vA = vertsA[(rowsA - 1) * colsA + lx];
                        Vector3 vB = vertsB[0 * colsB + lx];
                        Require(Mathf.IsEqualApprox(vA.X, vB.X, 0.01f), $"Seam X mismatch at ({cx},{cz})-(cx,{cz + 1}): vA={vA.X}, vB={vB.X}");
                        Require(Mathf.IsEqualApprox(vA.Z, vB.Z, 0.01f), $"Seam Z mismatch at ({cx},{cz})-(cx,{cz + 1}): vA={vA.Z}, vB={vB.Z}");
                        Require(Mathf.Abs(vA.Y - vB.Y) < 0.005f, $"Elevation gap between ({cx},{cz}) and ({cx},{cz + 1}) at X={vA.X}: A={vA.Y}, B={vB.Y}");
                    }
                }
                else if (fineA && !fineB)
                {
                    for (int lxFine = 0; lxFine <= 80; lxFine++)
                    {
                        Vector3 vA = vertsA[80 * 81 + lxFine];
                        int k = lxFine / 4;
                        int rem = lxFine % 4;
                        Vector3 vB0 = vertsB[0 * 21 + k];
                        Vector3 vB1 = vertsB[0 * 21 + Math.Min(k + 1, 20)];
                        float expectedY = Mathf.Lerp(vB0.Y, vB1.Y, rem / 4f);
                        Require(Mathf.Abs(vA.Y - expectedY) < 0.005f,
                            $"Elevation gap on fine-coarse seam between ({cx},{cz}) and ({cx},{cz + 1}) at X={vA.X}: A={vA.Y}, expected={expectedY}");
                    }
                }
                else
                {
                    for (int lxFine = 0; lxFine <= 80; lxFine++)
                    {
                        Vector3 vB = vertsB[0 * 81 + lxFine];
                        int k = lxFine / 4;
                        int rem = lxFine % 4;
                        Vector3 vA0 = vertsA[20 * 21 + k];
                        Vector3 vA1 = vertsA[20 * 21 + Math.Min(k + 1, 20)];
                        float expectedY = Mathf.Lerp(vA0.Y, vA1.Y, rem / 4f);
                        Require(Mathf.Abs(vB.Y - expectedY) < 0.005f,
                            $"Elevation gap on coarse-fine seam between ({cx},{cz}) and ({cx},{cz + 1}) at X={vB.X}: B={vB.Y}, expected={expectedY}");
                    }
                }
            }
        }
    }

    private static void CheckBayCoastContinuity(UstyePlanData plan)
    {
        // 1. Verify height continuity at the user's reported problem point [4762; 3408.8]
        float hA = plan.SampleTerrainElevation(4762.01f, 3408.8f);
        float hB = plan.SampleTerrainElevation(4761.99f, 3408.8f);
        float deltaReported = Mathf.Abs(hA - hB);
        Require(deltaReported < 0.05f,
            $"Bay coast height tear at (4762, 3408.8): delta={deltaReported:F3} m (hA={hA:F2}, hB={hB:F2})");

        // 2. Dense continuity scan along the entire BayCoastSpline
        Vector2[] coast = plan.BayCoastSpline;
        for (int i = 1; i < coast.Length - 1; i++)
        {
            Vector2 p = coast[i];
            Vector2 tangent = (coast[i + 1] - coast[i - 1]).Normalized();
            Vector2 normal = new(-tangent.Y, tangent.X);

            Vector2 p1 = p + normal * 0.2f;
            Vector2 p2 = p - normal * 0.2f;

            float h1 = plan.SampleTerrainElevation(p1.X, p1.Y);
            float h2 = plan.SampleTerrainElevation(p2.X, p2.Y);
            float delta = Mathf.Abs(h1 - h2);

            Require(delta < 0.35f,
                $"Bay coast cliff across coastline at ({p.X:F1},{p.Y:F1}): delta={delta:F3} m (h1={h1:F2}, h2={h2:F2})");
        }
    }

    private static void CheckWaterAndCrossings(UstyeMapPreview preview)
    {
        UstyePlanData plan = preview.Plan;

        Require(plan.River.Length == 9, $"Expected 9 river points, got {plan.River.Length}");
        Require(Mathf.IsEqualApprox(plan.RiverWidth, 100f), $"Expected 100 m river width, got {plan.RiverWidth}");
        Require(plan.Bay.Length == 7, $"Expected 7 bay polygon points, got {plan.Bay.Length}");

        // Verify Catmull-Rom smooth splines
        Require(plan.RiverSpline.Length >= 200, $"Expected >= 200 points on RiverSpline, got {plan.RiverSpline.Length}");
        Require(plan.BayCoastSpline.Length >= 100, $"Expected >= 100 points on BayCoastSpline, got {plan.BayCoastSpline.Length}");

        // Verify that river spline interpolates all 9 original river waypoints exactly
        foreach (Vector2 p in plan.River)
        {
            float dist = UstyePlanData.DistanceToPolyline(p, plan.RiverSpline);
            Require(dist < 0.05f, $"River spline must interpolate declared point ({p.X},{p.Y}), distance={dist}");
        }

        // Verify water mesh instances and shader
        Node3D waterRoot = preview.WaterRoot;
        Require(waterRoot is not null, "WaterRoot node is missing");

        MeshInstance3D? riverInstance = waterRoot!.FindChild("RiverWater", false, false) as MeshInstance3D;
        Require(riverInstance is not null, "RiverWater MeshInstance3D is missing");
        Require(riverInstance!.Mesh is ArrayMesh, "RiverWater mesh is not an ArrayMesh");
        Require(riverInstance.MaterialOverride is ShaderMaterial, "RiverWater material must be a ShaderMaterial");
        ShaderMaterial riverMat = (ShaderMaterial)riverInstance.MaterialOverride!;
        Require(riverMat.Shader is not null && riverMat.Shader.ResourcePath == "res://UstyeWater.gdshader",
            $"Expected RiverWater shader res://UstyeWater.gdshader, got {riverMat.Shader?.ResourcePath}");

        MeshInstance3D? bayInstance = waterRoot.FindChild("BayWater", false, false) as MeshInstance3D;
        Require(bayInstance is not null, "BayWater MeshInstance3D is missing");
        Require(bayInstance!.Mesh is ArrayMesh, "BayWater mesh is not an ArrayMesh");
        Require(bayInstance.MaterialOverride is ShaderMaterial, "BayWater material must be a ShaderMaterial");
        ShaderMaterial bayMat = (ShaderMaterial)bayInstance.MaterialOverride!;
        Require(bayMat.Shader is not null && bayMat.Shader.ResourcePath == "res://UstyeWater.gdshader",
            $"Expected BayWater shader res://UstyeWater.gdshader, got {bayMat.Shader?.ResourcePath}");

        // Verify crossings
        Require(plan.Crossings.Length == 3, $"Expected 3 crossings, got {plan.Crossings.Length}");

        UstyePlanData.Crossing? crossN = plan.Crossings.FirstOrDefault(c => c.Id == "CROSS-N");
        UstyePlanData.Crossing? crossC = plan.Crossings.FirstOrDefault(c => c.Id == "CROSS-C");
        UstyePlanData.Crossing? crossS = plan.Crossings.FirstOrDefault(c => c.Id == "CROSS-S");

        Require(crossN is not null, "CROSS-N is missing");
        Require(crossC is not null, "CROSS-C is missing");
        Require(crossS is not null, "CROSS-S is missing");

        Require(Mathf.IsEqualApprox(crossN!.DeckWidth, 8f), $"Expected CROSS-N deck width 8 m, got {crossN.DeckWidth}");
        Require(Mathf.IsEqualApprox(crossC!.DeckWidth, 14f), $"Expected CROSS-C deck width 14 m, got {crossC.DeckWidth}");
        Require(Mathf.IsEqualApprox(crossS!.DeckWidth, 6f), $"Expected CROSS-S deck width 6 m, got {crossS.DeckWidth}");

        // Check crossing bridge positions and channel depth
        foreach (UstyePlanData.Crossing cr in plan.Crossings)
        {
            Vector2 mid = (cr.EndA + cr.EndB) * 0.5f;
            float rDist = UstyePlanData.DistanceToPolyline(mid, plan.RiverSpline);
            Require(rDist < 50f, $"Crossing {cr.Id} center must align with RiverSpline, got dist {rDist}");

            float channelBedY = plan.SampleTerrainElevation(mid.X, mid.Y);
            Require(channelBedY <= 1.2f, $"Crossing {cr.Id} channel bed ({channelBedY} m) must be below water (2.0 m)");
        }

        // Check river carving elevation along centerline
        float riverMidY = plan.SampleTerrainElevation(2650f, 2450f);
        Require(riverMidY <= 1.0f, $"Expected riverbed below water level (<= 1.0 m), got {riverMidY}");
    }

    private static void CheckRoadAndBridgeIntegration(UstyeMapPreview preview)
    {
        UstyePlanData plan = preview.Plan;
        Node3D roadsRoot = preview.RoadsRoot;
        Require(roadsRoot is not null, "RoadsRoot node is missing");

        foreach (UstyePlanData.Crossing crossing in plan.Crossings)
        {
            Node? bridgeNode = roadsRoot!.FindChild($"Bridge_{crossing.Id}", false, false);
            Require(bridgeNode is Node3D, $"Bridge node Bridge_{crossing.Id} is missing");
            Node3D bridge = (Node3D)bridgeNode!;

            MeshInstance3D? deck = bridge.FindChild("Deck", false, false) as MeshInstance3D;
            Require(deck is not null, $"Bridge deck for {crossing.Id} is missing");

            // Verify bridge deck top surface elevation matches road approaches at EndA and EndB
            float roadElevA = plan.SampleTerrainElevation(crossing.EndA.X, crossing.EndA.Y) + 0.12f;
            float roadElevB = plan.SampleTerrainElevation(crossing.EndB.X, crossing.EndB.Y) + 0.12f;

            Require(MathF.Min(roadElevA, roadElevB) >= 3.5f,
                $"Bridge {crossing.Id} deck is too close to water surface (2.0 m): EndA={roadElevA:F2}, EndB={roadElevB:F2}");

            // Verify abutments
            Require(bridge.FindChild("Abutment_A", false, false) is not null, $"Bridge {crossing.Id} Abutment_A is missing");
            Require(bridge.FindChild("Abutment_B", false, false) is not null, $"Bridge {crossing.Id} Abutment_B is missing");

            // Verify road under bridge is actually cut (no road triangles inside the excluded bridge span)
            MeshInstance3D? roadInstance = roadsRoot.FindChild($"Road_{crossing.RoadId}", false, false) as MeshInstance3D;
            Require(roadInstance is not null, $"Road instance Road_{crossing.RoadId} is missing");
            Require(roadInstance!.Mesh is ArrayMesh, $"Road mesh Road_{crossing.RoadId} is not an ArrayMesh");

            ArrayMesh roadMesh = (ArrayMesh)roadInstance.Mesh!;
            using Godot.Collections.Array arrays = roadMesh.SurfaceGetArrays(0);
            Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

            Vector2 span = crossing.EndB - crossing.EndA;
            float spanLen = span.Length();
            Vector2 spanDir = span / spanLen;

            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 v0 = verts[indices[i]];
                Vector3 v1 = verts[indices[i + 1]];
                Vector3 v2 = verts[indices[i + 2]];

                Vector3 centroid = (v0 + v1 + v2) / 3f;
                Vector2 c2 = new(centroid.X, centroid.Z);

                float proj = (c2 - crossing.EndA).Dot(spanDir);
                float perp = MathF.Abs((c2 - crossing.EndA).Cross(spanDir));

                // Inside the span between EndA and EndB (with 5 m clearance buffer from ends)
                bool insideSpan = proj > 5.0f && proj < spanLen - 5.0f && perp < crossing.DeckWidth * 1.5f;
                Require(!insideSpan,
                    $"Road triangle under bridge span {crossing.Id} was not cut at centroid ({centroid.X:F1}, {centroid.Z:F1})");
            }
        }
    }

    private static void CheckGroundColoringSmoothness()
    {
        // Verify SmoothNoise2D is continuous and bounded
        for (float x = 1000f; x <= 4000f; x += 300f)
        {
            for (float z = 1000f; z <= 4000f; z += 300f)
            {
                float n = UstyeMapPreview.SmoothNoise2D(x, z, 1f / 350f);
                Require(!float.IsNaN(n) && !float.IsInfinity(n), $"SmoothNoise2D returned invalid float at ({x},{z})");
                Require(n >= 0f && n <= 1f, $"SmoothNoise2D out of bounds [0, 1] at ({x},{z}): {n}");

                // Check continuity: stepping 6.25 m produces delta < 0.20
                float nStep = UstyeMapPreview.SmoothNoise2D(x + 6.25f, z, 1f / 350f);
                float delta = MathF.Abs(n - nStep);
                Require(delta < 0.20f, $"SmoothNoise2D has excessive gradient at ({x},{z}): delta={delta}");
            }
        }
    }

    private static void CheckStructuresAndLandmarks(UstyeMapPreview preview)
    {
        UstyePlanData plan = preview.Plan;
        Require(plan.Structures.Length == 23, $"Expected 23 structures in layout.json, got {plan.Structures.Length}");

        int panelCount = plan.Structures.Count(s => s.Kind == "panel_block");
        int shedCount = plan.Structures.Count(s => s.Kind == "warehouse");
        int logCount = plan.Structures.Count(s => s.Kind == "log_stack");
        int towerCount = plan.Structures.Count(s => s.Kind == "water_tower");

        Require(panelCount == 12, $"Expected 12 panel blocks, got {panelCount}");
        Require(shedCount == 6, $"Expected 6 warehouse sheds, got {shedCount}");
        Require(logCount == 4, $"Expected 4 log stacks, got {logCount}");
        Require(towerCount == 1, $"Expected 1 water tower, got {towerCount}");

        foreach (UstyePlanData.Structure s in plan.Structures.Where(st => st.Kind == "panel_block"))
        {
            Require(Mathf.IsEqualApprox(s.Height, 15f), $"Panel block {s.Id} height should be 15 m, got {s.Height}");
            Require(Mathf.IsEqualApprox(s.Size.X, 80f) && Mathf.IsEqualApprox(s.Size.Y, 18f),
                $"Panel block {s.Id} size should be 80 x 18 m, got {s.Size}");
        }

        UstyePlanData.Structure waterTower = plan.Structures.First(s => s.Kind == "water_tower");
        Require(Mathf.IsEqualApprox(waterTower.Height, 22f),
            $"Water tower height should be 22 m, got {waterTower.Height}");

        Node3D landmarksRoot = preview.LandmarksRoot;
        Require(landmarksRoot is not null, "LandmarksRoot is missing");
        Require(landmarksRoot!.FindChild("Landmark_Church", true, false) is not null, "Landmark_Church is missing");
        Require(landmarksRoot.FindChild("Landmark_RadioMast", true, false) is not null, "Landmark_RadioMast is missing");
        Require(landmarksRoot.FindChild("Landmark_Lighthouse", true, false) is not null, "Landmark_Lighthouse is missing");
    }

    private static void CheckVegetation(UstyeMapPreview preview)
    {
        // 1. Verify tree pivot: mesh base at local Y = 0 (sits on ground, no underground sinking)
        Mesh coniferMesh = preview.ConiferTreeMesh;
        Mesh deciduousMesh = preview.DeciduousTreeMesh;
        Require(coniferMesh is not null, "ConiferTreeMesh is missing");
        Require(deciduousMesh is not null, "DeciduousTreeMesh is missing");

        using Godot.Collections.Array coniferArrays = coniferMesh!.SurfaceGetArrays(0);
        Vector3[] coniferVerts = coniferArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        float coniferMinY = coniferVerts.Min(v => v.Y);
        float coniferMaxY = coniferVerts.Max(v => v.Y);
        Require(coniferMinY >= -0.001f, $"Conifer tree base must have Y >= 0 (on ground), got {coniferMinY}");
        Require(coniferMaxY >= 12.9f && coniferMaxY <= 13.1f, $"Conifer tree height should be 13 m, got {coniferMaxY}");

        using Godot.Collections.Array deciduousArrays = deciduousMesh!.SurfaceGetArrays(0);
        Vector3[] deciduousVerts = deciduousArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        float deciduousMinY = deciduousVerts.Min(v => v.Y);
        float deciduousMaxY = deciduousVerts.Max(v => v.Y);
        Require(deciduousMinY >= -0.001f, $"Deciduous tree base must have Y >= 0 (on ground), got {deciduousMinY}");
        Require(deciduousMaxY >= 8.4f && deciduousMaxY <= 8.6f, $"Deciduous tree height should be 8.5 m, got {deciduousMaxY}");

        // 2. Verify spatial chunking: vegetation is partitioned across multiple spatial chunks
        Require(preview.ConiferMultiMeshes.Count > 1,
            $"Expected multiple chunked conifer MultiMeshes, got {preview.ConiferMultiMeshes.Count}");
        Require(preview.DeciduousMultiMeshes.Count > 1,
            $"Expected multiple chunked deciduous MultiMeshes, got {preview.DeciduousMultiMeshes.Count}");

        int coniferCount = preview.TotalConiferCount;
        int deciduousCount = preview.TotalDeciduousCount;

        Require(coniferCount > 5000, $"Expected > 5000 conifer trees in dense forest, got {coniferCount}");
        Require(deciduousCount > 500, $"Expected > 500 deciduous trees in sparse forest, got {deciduousCount}");
        Require(coniferCount > deciduousCount * 5,
            $"Expected dense forest to be substantially denser than sparse woodland (conifer={coniferCount}, deciduous={deciduousCount})");
    }

    private static void CheckScaleVehicles(UstyeMapPreview preview)
    {
        IReadOnlyList<UstyeMapPreview.ScaleVehicle> vehicles = preview.ScaleVehicles;
        Require(vehicles.Count == 6, $"Expected 6 scale reference vehicles, got {vehicles.Count}");

        // 1. Verify scale 1.0 (1 Godot unit = 1 meter)
        foreach (UstyeMapPreview.ScaleVehicle sv in vehicles)
        {
            Vector3 scale = sv.Visual.Root.Scale;
            Require(Mathf.IsEqualApprox(scale.X, 1.0f) &&
                    Mathf.IsEqualApprox(scale.Y, 1.0f) &&
                    Mathf.IsEqualApprox(scale.Z, 1.0f),
                $"Vehicle {sv.Id} must have scale 1.0, got {scale}");

            // Verify position matches ground elevation
            float groundY = preview.Plan.SampleTerrainElevation(sv.GroundPosition.X, sv.GroundPosition.Z);
            Require(Mathf.IsEqualApprox(sv.Visual.Root.Position.Y, groundY, 0.05f),
                $"Vehicle {sv.Id} Y position ({sv.Visual.Root.Position.Y}) must match ground ({groundY})");

            // Verify vehicle does not intersect structure footprints
            foreach (UstyePlanData.Structure st in preview.Plan.Structures)
            {
                bool inside = sv.GroundPosition.X >= st.Min.X - 3f && sv.GroundPosition.X <= st.Min.X + st.Size.X + 3f &&
                              sv.GroundPosition.Z >= st.Min.Y - 3f && sv.GroundPosition.Z <= st.Min.Y + st.Size.Y + 3f;
                Require(!inside, $"Vehicle {sv.Id} intersects structure {st.Id} footprint");
            }
        }

        // 2. Verify model identities
        Require(vehicles.Any(v => v.Id == "t62"), "Scale vehicle t62 is missing");
        Require(vehicles.Any(v => v.Id == "m60a1"), "Scale vehicle m60a1 is missing");
        Require(vehicles.Any(v => v.Id == "btr70"), "Scale vehicle btr70 is missing");
        Require(vehicles.Any(v => v.Id == "brdm2"), "Scale vehicle brdm2 is missing");

        // 3. Verify dynamic vehicle LOD switching
        UstyeMapPreview.ScaleVehicle t62 = vehicles.First(v => v.Id == "t62");
        Require(t62.Visual.LodCount == 3, $"Expected 3 LODs for t62, got {t62.Visual.LodCount}");

        // Zoom 30 m (< 45 m): LOD0 visible, other LODs hidden
        t62.UpdateLod(30f);
        Require(t62.ActiveLod == 0, $"Expected active LOD 0 at 30 m, got {t62.ActiveLod}");
        Require(t62.Visual.GetOrCreateLod(0).Visible, "LOD0 must be visible at 30 m");
        if (t62.Visual.IsLoaded(1)) Require(!t62.Visual.GetOrCreateLod(1).Visible, "LOD1 must be hidden at 30 m");
        if (t62.Visual.IsLoaded(2)) Require(!t62.Visual.GetOrCreateLod(2).Visible, "LOD2 must be hidden at 30 m");

        // Zoom 70 m (45..95 m): LOD1 visible, other LODs hidden
        t62.UpdateLod(70f);
        Require(t62.ActiveLod == 1, $"Expected active LOD 1 at 70 m, got {t62.ActiveLod}");
        Require(t62.Visual.GetOrCreateLod(1).Visible, "LOD1 must be visible at 70 m");
        Require(!t62.Visual.GetOrCreateLod(0).Visible, "LOD0 must be hidden at 70 m");
        if (t62.Visual.IsLoaded(2)) Require(!t62.Visual.GetOrCreateLod(2).Visible, "LOD2 must be hidden at 70 m");

        // Zoom 200 m (>= 95 m): LOD2 visible, other LODs hidden
        t62.UpdateLod(200f);
        Require(t62.ActiveLod == 2, $"Expected active LOD 2 at 200 m, got {t62.ActiveLod}");
        Require(t62.Visual.GetOrCreateLod(2).Visible, "LOD2 must be visible at 200 m");
        Require(!t62.Visual.GetOrCreateLod(0).Visible, "LOD0 must be hidden at 200 m");
        Require(!t62.Visual.GetOrCreateLod(1).Visible, "LOD1 must be hidden at 200 m");

        // Return to initial camera size
        t62.UpdateLod(preview.Rig.Size);
    }

    private static void CheckCameraInput(UstyeMapPreview preview)
    {
        CameraRig rig = preview.Rig;
        Viewport viewport = preview.GetViewport();
        preview.SelectLandmark(0, immediate: true);
        float initialTarget = rig.Model.TargetSize;

        using InputEventMouseButton wheel = new()
        {
            ButtonIndex = MouseButton.WheelUp,
            Pressed = true,
            Factor = 1f,
            Position = new Vector2(700f, 400f)
        };
        viewport.PushInput(wheel, true);
        Require(Mathf.IsEqualApprox(rig.Model.TargetSize, initialTarget * rig.Model.ZoomStepFactor),
            "Map preview wheel input must reach the camera exactly once through the viewport.");
        float sizeBefore = rig.Size;
        rig.Update(0.1, allowInput: false);
        Require(rig.Size < sizeBefore, "Map preview camera must move toward the wheel zoom target.");

        wheel.ButtonIndex = MouseButton.WheelDown;
        float zoomedTarget = rig.Model.TargetSize;
        viewport.PushInput(wheel, true);
        Require(rig.Model.TargetSize > zoomedTarget, "Wheel down must zoom out.");

        wheel.ButtonIndex = MouseButton.WheelUp;
        wheel.Factor = 0.25f;
        wheel.Position = new Vector2(30f, 25f);
        float beforeHud = rig.Model.TargetSize;
        viewport.PushInput(wheel, true);
        Require(Mathf.IsEqualApprox(rig.Model.TargetSize, beforeHud * MathF.Pow(rig.Model.ZoomStepFactor, 0.25f)),
            "Fractional wheel input must zoom over the read-only map HUD.");

        wheel.Pressed = false;
        float beforeRelease = rig.Model.TargetSize;
        viewport.PushInput(wheel, true);
        Require(rig.Model.TargetSize == beforeRelease, "Wheel release must not zoom again.");

        using InputEventMagnifyGesture pinch = new() { Factor = 1.25f };
        viewport.PushInput(pinch, true);
        Require(Mathf.IsEqualApprox(rig.Model.TargetSize, beforeRelease / 1.25f),
            "Map preview must forward trackpad magnification to the camera.");

        using InputEventKey landmark = new() { Keycode = Key.Key2, Pressed = true };
        viewport.PushInput(landmark, true);
        wheel.Pressed = true;
        wheel.Factor = 1f;
        viewport.PushInput(wheel, true);
        float interruptedTarget = rig.Model.TargetSize;
        preview._Process(0.1);
        Require(rig.Model.TargetSize == interruptedTarget, "Wheel zoom must cancel a landmark transition.");

        using InputEventKey projection = new() { Keycode = Key.V, Pressed = true };
        bool wasPerspective = rig.IsPerspective;
        viewport.PushInput(projection, true);
        Require(rig.IsPerspective != wasPerspective, "V must toggle the map camera projection.");

        using InputEventMouseMotion orbit = new()
        {
            ButtonMask = MouseButtonMask.Right,
            Relative = new Vector2(20f, 10f),
            Position = new Vector2(700f, 400f)
        };
        float oldYaw = rig.ViewYaw;
        viewport.PushInput(orbit, true);
        Require(rig.ViewYaw != oldYaw, "Right-button drag must orbit the map camera.");

        rig.SetPerspective(wasPerspective);
        preview.SelectLandmark(0, immediate: true);
    }

    private static void CheckCameraNavigation(UstyeMapPreview preview)
    {
        CameraRig rig = preview.Rig;
        Require(rig is not null, "CameraRig is missing");

        UstyeMapPreview.CameraLandmark[] landmarks = preview.Landmarks;
        Require(landmarks.Length >= 8, $"Expected at least 8 camera landmarks, got {landmarks.Length}");

        foreach (UstyeMapPreview.CameraLandmark lm in landmarks)
        {
            Require(lm.Focus.X >= 0f && lm.Focus.X <= 5000f,
                $"Landmark {lm.Name} X focus ({lm.Focus.X}) out of bounds");
            Require(lm.Focus.Z >= 0f && lm.Focus.Z <= 5000f,
                $"Landmark {lm.Name} Z focus ({lm.Focus.Z}) out of bounds");
            Require(lm.Size >= 100f && lm.Size <= 5000f,
                $"Landmark {lm.Name} size ({lm.Size}) out of bounds");
        }
    }
}
