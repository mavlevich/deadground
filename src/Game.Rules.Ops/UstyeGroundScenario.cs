using System.IO;
using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Playable 5000 x 5000 m Ustye ground scenario.
/// Uses authentic Cold War combat vehicles across riverside town,
/// panel blocks, industrial structures, river channel, and three road bridges.
/// Provides 4 switchable starting setups for tactical evaluation:
/// 0. Central bridge crossing (CROSS-C)
/// 1. Southern crossing column (CROSS-S)
/// 2. City maneuvering among panel blocks
/// 3. Reconnaissance and line-of-sight occlusion
/// </summary>
public static class UstyeGroundScenario
{
    public const int WorldCapacity = 64;
    public const float MapExtent = 5000f;
    public const float HeightMapSpacing = 2.5f;
    public const int HeightMapResolution = (int)(MapExtent / HeightMapSpacing) + 1; // 2001 x 2001

    public static NavigationBuildLimits CreateNavigationBuildLimits() => new(
        MaxSectors: 16_384,
        MaxDetailedCells: 16_000_000,
        MaxPortals: 131_072,
        MaxIntraEdges: 1_000_000);

    public static NavigationTuning CreateNavigationTuning() => new(
        MaxWaypointsPerRoute: 256,
        MaxTotalExpansionsPerRequest: 50_000,
        MaxGlobalExpansionsPerTick: 1000,
        MaxLocalExpansionsPerTick: 2000,
        MaxLocalWindowSize: 320.0f,
        GlobalPlanCacheEntries: 32,
        MaxOrderExecutionTicks: 10_000);

    public static HeightMap CreateHeightMap(UstyePlanData? plan = null)
    {
        plan ??= UstyePlanData.Load();
        ushort[] heights = new ushort[HeightMapResolution * HeightMapResolution];

        for (int r = 0; r < HeightMapResolution; r++)
        {
            float wz = r * HeightMapSpacing;
            int rowOffset = r * HeightMapResolution;
            for (int c = 0; c < HeightMapResolution; c++)
            {
                float wx = c * HeightMapSpacing;
                float h = plan.SampleTerrainElevation(wx, wz);
                heights[rowOffset + c] = (ushort)Math.Clamp((int)MathF.Round(MathF.Max(0f, h) * 100f), 0, ushort.MaxValue);
            }
        }

        return new HeightMap(HeightMapResolution, HeightMapResolution, HeightMapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry(UstyePlanData? plan = null, HeightMap? map = null)
    {
        plan ??= UstyePlanData.Load();
        map ??= CreateHeightMap(plan);

        // 1. Bridges from layout.json
        List<BridgeStructure> bridges = new(3);
        foreach (UstyePlanData.Crossing cr in plan.Crossings)
        {
            float hA = map.GetHeight(cr.EndA.X, cr.EndA.Y) + 0.12f;
            float hB = map.GetHeight(cr.EndB.X, cr.EndB.Y) + 0.12f;
            float thickness = cr.Id == "CROSS-C" ? 1.6f : 1.2f;
            int bridgeId = cr.Id switch
            {
                "CROSS-N" => 1,
                "CROSS-C" => 2,
                "CROSS-S" => 3,
                _ => bridges.Count + 1
            };

            bridges.Add(new BridgeStructure(
                new StructureId(bridgeId),
                cr.EndA,
                cr.EndB,
                cr.DeckWidth,
                hA,
                hB,
                thickness));
        }

        // 2. Opaque structures (buildings, warehouses, water tower)
        List<OpaqueStructure> structures = [];
        int structId = 101;
        foreach (UstyePlanData.Structure st in plan.Structures)
        {
            if (st.Kind is not ("panel_block" or "warehouse" or "water_tower" or "log_stack"))
                continue;

            float minX = st.Min.X;
            float minZ = st.Min.Y;
            float maxX = minX + st.Size.X;
            float maxZ = minZ + st.Size.Y;

            float groundY = map.GetHeight((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
            float topY = groundY + st.Height;

            structures.Add(new OpaqueStructure(
                new StructureId(structId++),
                minX, 0f, minZ,
                maxX, topY, maxZ,
                isGrounded: true));
        }

        // 3. Water obstacles (river slices and bay slices)
        List<TerrainObstacle> waterObstacles = [];
        const float riverSliceZ = 25f;
        int riverSlices = (int)(MapExtent / riverSliceZ);
        float riverHalfWidth = plan.RiverWidth * 0.50f;

        for (int i = 0; i < riverSlices; i++)
        {
            float z0 = i * riverSliceZ;
            float z1 = MathF.Min(MapExtent, (i + 1) * riverSliceZ);

            float minX = float.MaxValue;
            float maxX = float.MinValue;

            // Sample across this Z slice to find river channel bounds
            for (float sZ = z0; sZ <= z1; sZ += 2.5f)
            {
                // Find river center X at this Z from RiverSpline
                float rCenterX = SampleSplineXAtZ(plan.RiverSpline, sZ);
                float left = rCenterX - riverHalfWidth;
                float right = rCenterX + riverHalfWidth;
                if (left < minX) minX = left;
                if (right > maxX) maxX = right;
            }

            if (minX < maxX)
            {
                waterObstacles.Add(new TerrainObstacle(
                    minX, z0, maxX, z1,
                    TraversabilityCapabilities.Water));
            }
        }

        // Bay water slices in southeast corner [3300..5000, 3300..5000]
        const float baySliceZ = 5.0f;
        for (float z0 = 3300f; z0 < MapExtent; z0 += baySliceZ)
        {
            float z1 = MathF.Min(MapExtent, z0 + baySliceZ);
            float zMid = (z0 + z1) * 0.5f;

            float runStart = -1f;
            for (float x = 3250f; x <= MapExtent; x += 2.5f)
            {
                float h = map.GetHeight(x, zMid);
                bool isWater = h < 2.0f;
                if (isWater)
                {
                    if (runStart < 0f) runStart = x;
                }
                else
                {
                    if (runStart >= 0f)
                    {
                        float runEnd = x;
                        if (runEnd > runStart + 0.1f)
                        {
                            waterObstacles.Add(new TerrainObstacle(
                                runStart, z0, runEnd, z1,
                                TraversabilityCapabilities.Water));
                        }
                        runStart = -1f;
                    }
                }
            }
            if (runStart >= 0f)
            {
                float runEnd = MapExtent;
                if (runEnd > runStart + 0.1f)
                {
                    waterObstacles.Add(new TerrainObstacle(
                        runStart, z0, runEnd, z1,
                        TraversabilityCapabilities.Water));
                }
            }
        }

        return new StaticGeometry(
            map,
            structures.ToArray(),
            bridges.ToArray(),
            waterObstacles.ToArray());
    }

    private static float SampleSplineXAtZ(IReadOnlyList<Vector2> spline, float z)
    {
        for (int i = 0; i < spline.Count - 1; i++)
        {
            Vector2 p0 = spline[i];
            Vector2 p1 = spline[i + 1];
            float minZ = MathF.Min(p0.Y, p1.Y);
            float maxZ = MathF.Max(p0.Y, p1.Y);
            if (z >= minZ && z <= maxZ && MathF.Abs(p1.Y - p0.Y) > 1e-4f)
            {
                float t = (z - p0.Y) / (p1.Y - p0.Y);
                return p0.X + (p1.X - p0.X) * t;
            }
        }

        float bestDist = float.MaxValue;
        float bestX = 2500f;
        for (int i = 0; i < spline.Count; i++)
        {
            float d = MathF.Abs(spline[i].Y - z);
            if (d < bestDist)
            {
                bestDist = d;
                bestX = spline[i].X;
            }
        }
        return bestX;
    }

    private static float SampleBayCoastXAtZ(IReadOnlyList<Vector2> bayCoast, float z)
    {
        for (int i = 0; i < bayCoast.Count - 1; i++)
        {
            Vector2 p0 = bayCoast[i];
            Vector2 p1 = bayCoast[i + 1];
            float minZ = MathF.Min(p0.Y, p1.Y);
            float maxZ = MathF.Max(p0.Y, p1.Y);
            if (z >= minZ && z <= maxZ && MathF.Abs(p1.Y - p0.Y) > 1e-4f)
            {
                float t = (z - p0.Y) / (p1.Y - p0.Y);
                return p0.X + (p1.X - p0.X) * t;
            }
        }

        return 3300f;
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry geometry, UnitCatalog? catalog = null)
    {
        catalog ??= ColdWarContent.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            "UstyeGround",
            PlayerTeams: [1, 2],
            PlayerFactions: ["soviet", "us"]);
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        int setupIndex = 0,
        ulong matchSeed = 42UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        NavigationTuning navTuning = CreateNavigationTuning();
        NavigationBuildLimits buildLimits = CreateNavigationBuildLimits();

        OpsRules rules = new(geometry, catalog, navTuning, buildLimits: buildLimits);
        WorldState world = new(geometry.HeightMap, WorldCapacity);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        ushort btr70Idx = catalog.RequireDefinitionIndex("btr70");
        ushort brdm2Idx = catalog.RequireDefinitionIndex("brdm2");

        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
        ushort m151Idx = catalog.RequireDefinitionIndex("m151a2");

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        UnitDefinition btr70Def = catalog.RequireDefinition("btr70");
        UnitDefinition brdm2Def = catalog.RequireDefinition("brdm2");

        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
        UnitDefinition m151Def = catalog.RequireDefinition("m151a2");

        int mode = Math.Clamp(setupIndex, 0, 3);

        switch (mode)
        {
            case 0: // Setup 0: Group in front of central bridge (CROSS-C)
                {
                    Vector2 bridgeCenter = new(2650f, 2450f);
                    // Soviet (Side 1): West bank approaching CROSS-C
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2460f, 2446f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2460f, 2454f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2440f, 2446f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2440f, 2454f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2420f, 2446f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2420f, 2454f), bridgeCenter);

                    // US (Side 2): East bank defending/facing CROSS-C
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(2840f, 2446f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(2840f, 2454f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(2860f, 2446f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(2860f, 2454f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(2880f, 2446f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(2880f, 2454f), bridgeCenter);
                    break;
                }

            case 1: // Setup 1: Column in front of narrow southern crossing (CROSS-S)
                {
                    Vector2 bridgeCenter = new(2860f, 3550f);
                    // Soviet (Side 1): Single-file road column approaching CROSS-S
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2710f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2690f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2670f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2650f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2630f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2610f, 3550f), bridgeCenter);

                    // US (Side 2): Eastern road approach
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(3010f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(3030f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(3050f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(3070f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(3090f, 3550f), bridgeCenter);
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(3110f, 3550f), bridgeCenter);
                    break;
                }

            case 2: // Setup 2: City maneuvering among panel blocks (TOWN-WEST)
                {
                    // Soviet (Side 1): Maneuvering in street corridors between panel blocks
                    Vector2 eastFacing = new(2350f, 2260f);
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2160f, 2220f), eastFacing);
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2190f, 2220f), eastFacing);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2160f, 2260f), eastFacing);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2190f, 2260f), eastFacing);
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2160f, 2300f), eastFacing);
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2190f, 2300f), eastFacing);

                    // US (Side 2): Across western district
                    Vector2 westFacing = new(2160f, 2260f);
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(2320f, 2220f), westFacing);
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(2350f, 2220f), westFacing);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(2320f, 2260f), westFacing);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(2350f, 2260f), westFacing);
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(2320f, 2300f), westFacing);
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(2350f, 2300f), westFacing);
                    break;
                }

            case 3: // Setup 3: Reconnaissance and line-of-sight occlusion
                {
                    // Soviet BRDM-2 and US M151 separated by BLOCK-WEST-1-1 (min: [2040, 2160], size: [80, 18], height: 15)
                    Vector2 northFacing = new(2080f, 2100f);
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2080f, 2200f), northFacing); // South of block
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2060f, 2220f), northFacing);
                    SpawnUnit(world, catalog, 1, t62Idx, t62Def, new Vector2(2080f, 2220f), northFacing);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2060f, 2250f), northFacing);
                    SpawnUnit(world, catalog, 1, btr70Idx, btr70Def, new Vector2(2080f, 2250f), northFacing);
                    SpawnUnit(world, catalog, 1, brdm2Idx, brdm2Def, new Vector2(2100f, 2250f), northFacing);

                    Vector2 southFacing = new(2080f, 2200f);
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(2080f, 2130f), southFacing); // North of block (LOS blocked)
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(2060f, 2100f), southFacing);
                    SpawnUnit(world, catalog, 2, m60Idx, m60Def, new Vector2(2080f, 2100f), southFacing);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(2060f, 2070f), southFacing);
                    SpawnUnit(world, catalog, 2, m113Idx, m113Def, new Vector2(2080f, 2070f), southFacing);
                    SpawnUnit(world, catalog, 2, m151Idx, m151Def, new Vector2(2100f, 2070f), southFacing);
                    break;
                }
        }

        return new Simulation(world, rules, matchSeed);
    }

    private static void SpawnUnit(WorldState world, UnitCatalog catalog, int player, ushort defIdx, UnitDefinition def, Vector2 position, Vector2 enemyFacing)
    {
        Vector2 dir = enemyFacing - position;
        float initialYaw = MathF.Atan2(-dir.X, -dir.Y);
        world.CreateUnit(player, defIdx, position, def.SpeedRoad, def.MaxHealth, catalog.GetAmmoCapacity(def), initialYaw);
    }
}
