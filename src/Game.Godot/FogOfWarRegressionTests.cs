using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using Vector3 = Godot.Vector3;

namespace DeadGround.Client;

internal static class FogOfWarRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckFlatMapRange();
        CheckStructureOcclusion();
        CheckBattleScenarioSovietStart();
        CheckLineOfSightContextAgreement();
        CheckSteadyStateZeroAllocations();
        CheckReliefShadingAndHeightTint();
        CheckTimeBudgetWith40Units();
        CheckCachedViewsheds();
        CheckToggle();
        GD.Print("FOG_OF_WAR_REGRESSIONS_OK 9/9 passed: flat_range structure_occlusion battle_soviet_start los_agreement zero_allocations relief_shading budget_40_units cached_viewsheds toggle");
    }

    private static void CheckFlatMapRange()
    {
        int size = 65;
        float cellSize = 2.0f;
        ushort[] samples = new ushort[size * size];
        HeightMap map = new(size, size, cellSize, 0.01f, samples);
        using FogOfWarView fog = new(map, null, customCellSize: 4.0f);

        System.Numerics.Vector2 unitPos = new(64f, 64f);
        float sensorRange = 40.0f;
        float eyeHeight = 2.0f;

        fog.UpdateUnitNow(unitPos, eyeHeight, sensorRange);

        // 0.9 x sensor range (36 m away) -> (100, 64) is seen
        float distNear = 0.9f * sensorRange;
        bool nearSeen = fog.IsWorldPointSeen(unitPos.X + distNear, unitPos.Y);
        Require(nearSeen, "A cell at 0.9 x sensor range from an observer must be seen.");

        // 1.1 x sensor range (44 m away) -> (108, 64) is not seen
        float distFar = 1.1f * sensorRange;
        bool farSeen = fog.IsWorldPointSeen(unitPos.X + distFar, unitPos.Y);
        Require(!farSeen, "A cell at 1.1 x sensor range from an observer must not be seen.");
    }

    private static void CheckStructureOcclusion()
    {
        int size = 65;
        float cellSize = 2.0f;
        ushort[] samples = new ushort[size * size];
        HeightMap map = new(size, size, cellSize, 0.01f, samples);

        // Wall at X in [30, 32], Z in [15, 25], height 4 m
        OpaqueStructure wall = new(new StructureId(1), 30f, 0f, 15f, 32f, 4f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        using FogOfWarView fog = new(map, geo, customCellSize: 4.0f);

        System.Numerics.Vector2 unitPos = new(20f, 20f);
        float eyeHeight = 2.0f;
        float sensorRange = 60.0f;

        fog.UpdateUnitNow(unitPos, eyeHeight, sensorRange);

        // Cell directly behind wall at (40, 20) is hidden (dist 20m < 60m)
        bool behindWallSeen = fog.IsWorldPointSeen(40f, 20f);
        Require(!behindWallSeen, "A structure (wall) between an observer and a cell must hide that cell.");

        // Cell to the side of the wall at (20, 40) is unobstructed and seen
        bool unobstructedSeen = fog.IsWorldPointSeen(20f, 40f);
        Require(unobstructedSeen, "Unobstructed ground within sensor range must be seen.");
    }

    private static void CheckBattleScenarioSovietStart()
    {
        StaticGeometry geo = BattleScenario.CreateStaticGeometry();
        HeightMap map = geo.HeightMap;
        using FogOfWarView fog = new(map, geo);

        System.Numerics.Vector2 sovietStart = BattleScenario.SovietStart; // (380, 1640)
        float eyeHeight = 2.3f;
        float sensorRange = 2000.0f;

        fog.UpdateUnitNow(sovietStart, eyeHeight, sensorRange);

        // Near slope of the ridge (500, 1500) is visible
        bool nearSlopeSeen = fog.IsWorldPointSeen(500f, 1500f);
        Require(nearSlopeSeen, "The near slope facing the Soviet start must be seen.");

        // Ground behind the ridge (e.g. US start at 1640, 380) is hidden by the 22 m high ridge
        bool behindRidgeSeen = fog.IsWorldPointSeen(BattleScenario.UsStart.X, BattleScenario.UsStart.Y);
        Require(!behindRidgeSeen, "Ground behind the ridge from the Soviet start must be unseen.");
    }

    private static void CheckLineOfSightContextAgreement()
    {
        StaticGeometry geo = BattleScenario.CreateStaticGeometry();
        HeightMap map = geo.HeightMap;
        using FogOfWarView fog = new(map, geo);

        System.Numerics.Vector2 sovietStart = BattleScenario.SovietStart;
        float eyeHeight = 2.3f;
        float sensorRange = 2000.0f;

        fog.UpdateUnitNow(sovietStart, eyeHeight, sensorRange);

        LineOfSightContext context = new();
        float eyeY = map.GetHeight(sovietStart.X, sovietStart.Y) + eyeHeight;
        System.Numerics.Vector3 eye = new(sovietStart.X, eyeY, sovietStart.Y);

        int sampleCount = 0;
        int agreeCount = 0;

        int rows = fog.Rows;
        int cols = fog.Columns;
        float cellSize = fog.CellSize;

        for (int cz = 5; cz < rows - 5; cz += 4)
        {
            for (int cx = 5; cx < cols - 5; cx += 4)
            {
                float cellCenterX = (cx + 0.5f) * cellSize;
                float cellCenterZ = (cz + 0.5f) * cellSize;
                float dist = System.Numerics.Vector2.Distance(sovietStart, new System.Numerics.Vector2(cellCenterX, cellCenterZ));
                if (dist > sensorRange) continue;

                bool viewshedSeen = fog.IsCellSeen(cx, cz);

                // Filter out cells near fog edges (adjacent to any cell with opposite visibility state)
                bool nearEdge = false;
                for (int dz = -1; dz <= 1 && !nearEdge; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int ncx = cx + dx, ncz = cz + dz;
                        if (ncx >= 0 && ncx < cols && ncz >= 0 && ncz < rows)
                        {
                            if (fog.IsCellSeen(ncx, ncz) != viewshedSeen)
                            {
                                nearEdge = true;
                                break;
                            }
                        }
                    }
                }
                if (nearEdge) continue;

                float targetY = map.GetHeight(cellCenterX, cellCenterZ);
                System.Numerics.Vector3 target = new(cellCenterX, targetY, cellCenterZ);
                bool losClear = geo.IsLineOfSightClear(eye, target, context);

                sampleCount++;
                if (viewshedSeen == losClear)
                {
                    agreeCount++;
                }
            }
        }

        Require(sampleCount >= 200, $"Must evaluate at least 200 sample cells away from fog edges, got {sampleCount}.");
        float agreement = (float)agreeCount / sampleCount;
        GD.Print($"FOG_LOS_AGREEMENT samples={sampleCount} agreed={agreeCount} agreement={agreement * 100f:F2}%");
        Require(agreement >= 0.99f, $"Viewshed agreement with LineOfSightContext must be at least 99%, got {agreement * 100f:F2}%.");
    }

    private static void CheckSteadyStateZeroAllocations()
    {
        StaticGeometry geo = BattleScenario.CreateStaticGeometry();
        HeightMap map = geo.HeightMap;
        using FogOfWarView fog = new(map, geo);
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");

        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];

        ushort t62Def = catalog.RequireDefinitionIndex("t62");
        OwnUnitObservation u1 = new(new EntityId(1), t62Def, BattleScenario.SovietStart, 0);
        OwnUnitObservation u2 = new(new EntityId(2), t62Def, BattleScenario.SovietStart + new System.Numerics.Vector2(40, 0), 0);

        Apply(replica, bytes, 1, 10, [u1, u2]);

        // Warm up fog of war update loop and buffer caches
        for (int i = 0; i < 100; i++)
        {
            fog.Update(0.016f, replica, catalog);
        }

        // Measure allocations over up to three windows of 100 frames
        long allocated = long.MaxValue;
        for (int window = 0; window < 3 && allocated != 0; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                fog.Update(0.016f, replica, catalog);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Require(allocated == 0L, $"Steady-state FogOfWarView.Update must produce 0 managed allocations, got {allocated} bytes.");

        // Measure update time with 6 units on battle map
        OwnUnitObservation[] sixUnits = new OwnUnitObservation[6];
        for (int i = 0; i < 6; i++)
        {
            sixUnits[i] = new OwnUnitObservation(new EntityId((uint)(10 + i)), t62Def, BattleScenario.SovietStart + new System.Numerics.Vector2(i * 30f, 0), 0);
        }
        Apply(replica, bytes, 2, 20, sixUnits);
        long start6 = System.Diagnostics.Stopwatch.GetTimestamp();
        fog.Update(FogOfWarView.CycleInterval, replica, catalog);
        double time6Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start6) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        GD.Print($"FOG_6_UNITS_TIME update_ms={time6Ms:F3}");
    }

    private static void CheckReliefShadingAndHeightTint()
    {
        // 1. Shading from terrain normals with light from North-West (azimuth 315 deg, altitude 45 deg)
        Vector3 nwNormal = new Vector3(-0.5f, 0.7071f, -0.5f).Normalized();
        Vector3 seNormal = new Vector3(0.5f, 0.7071f, 0.5f).Normalized();

        float nwShade = TerrainView.ComputeHillshade(nwNormal);
        float seShade = TerrainView.ComputeHillshade(seNormal);

        Require(nwShade > seShade + 0.15f,
            $"Slopes facing north-west must receive clearly brighter shading than south-east: NW={nwShade:F3}, SE={seShade:F3}.");

        // 2. Height tint scaling and brightness limit on battle map
        HeightMap battleMap = BattleScenario.CreateMap();
        MeshInstance3D terrain = TerrainView.Create(battleMap);
        try
        {
            using Godot.Collections.Array arrays = terrain.Mesh.SurfaceGetArrays(0);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Color[] colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();

            float minHeight = battleMap.MinHeight;
            float maxHeight = battleMap.MaxHeight;
            Require(maxHeight > minHeight + 15f, "Battle map must have significant elevation variation.");

            int lowestIdx = -1;
            int highestIdx = -1;
            float lowestH = float.MaxValue;
            float highestH = float.MinValue;

            for (int i = 0; i < vertices.Length; i++)
            {
                float h = vertices[i].Y;
                if (h < lowestH) { lowestH = h; lowestIdx = i; }
                if (h > highestH) { highestH = h; highestIdx = i; }

                Color c = colors[i];
                Require(c.R <= 0.75f && c.G <= 0.75f && c.B <= 0.75f,
                    $"Seen ground vertex colors must stay below brightness limit 0.75, got ({c.R:F3}, {c.G:F3}, {c.B:F3}).");
                float lum = 0.299f * c.R + 0.587f * c.G + 0.114f * c.B;
                Require(lum <= 0.65f,
                    $"Seen ground luminance must stay below 0.65, got {lum:F3}.");
            }

            Color lowestColor = colors[lowestIdx];
            Color highestColor = colors[highestIdx];

            // Shaded colors should distinctly reflect the low vs high tint gradient
            float colorDiff = MathF.Abs(highestColor.R - lowestColor.R) +
                              MathF.Abs(highestColor.G - lowestColor.G) +
                              MathF.Abs(highestColor.B - lowestColor.B);

            Require(colorDiff > 0.05f,
                $"Height tint must differ between lowest ground ({lowestH:F1}m, color {lowestColor}) and highest ground ({highestH:F1}m, color {highestColor}).");
        }
        finally
        {
            terrain.Mesh.Dispose();
            terrain.Free();
        }
    }

    private static void CheckTimeBudgetWith40Units()
    {
        StaticGeometry geo = BattleScenario.CreateStaticGeometry();
        HeightMap map = geo.HeightMap;
        using FogOfWarView fog = new(map, geo);
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");

        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];

        ushort t62Def = catalog.RequireDefinitionIndex("t62");
        OwnUnitObservation[] units = new OwnUnitObservation[40];
        int uIdx = 0;
        for (int r = 0; r < 5; r++)
        {
            for (int c = 0; c < 8; c++)
            {
                float x = 200f + c * 80f;
                float z = 1300f + r * 100f;
                units[uIdx] = new OwnUnitObservation(new EntityId((uint)(100 + uIdx)), t62Def, new System.Numerics.Vector2(x, z), 0);
                uIdx++;
            }
        }

        Apply(replica, bytes, 1, 10, units);

        // Warm up JIT compilation and caches
        fog.Update(FogOfWarView.CycleInterval, replica, catalog);
        while (fog.IsCycleInProgress)
        {
            fog.Update(0.016f, replica, catalog);
        }

        // Every unit moves, so the measured cycle sweeps all 40 of them again.
        for (int i = 0; i < units.Length; i++)
        {
            units[i] = units[i] with { Position = units[i].Position + new System.Numerics.Vector2(10f, 0f) };
        }
        Apply(replica, bytes, 2, 20, units);

        // Run frames until full cycle completes
        int frames = 0;
        double maxFrameTimeMs = 0.0;
        double totalTimeMs = 0.0;

        // Trigger cycle start and measure initial frame
        long start1 = System.Diagnostics.Stopwatch.GetTimestamp();
        fog.Update(FogOfWarView.CycleInterval, replica, catalog);
        double frameTime1Ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        maxFrameTimeMs = frameTime1Ms;
        totalTimeMs = frameTime1Ms;
        Require(frameTime1Ms < 2.0, $"Per-frame time must stay under 2.0 ms with 40 units on cycle start, got {frameTime1Ms:F3} ms.");
        frames++;

        while (fog.IsCycleInProgress && frames < 60)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            fog.Update(0.016f, replica, catalog);
            double frameTimeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (frameTimeMs > maxFrameTimeMs) maxFrameTimeMs = frameTimeMs;
            totalTimeMs += frameTimeMs;
            Require(frameTimeMs < 2.0, $"Per-frame time must stay under 2.0 ms with 40 units, got {frameTimeMs:F3} ms on frame {frames}.");
            frames++;
        }

        Require(!fog.IsCycleInProgress, $"Full update cycle with 40 units did not complete within bounded frames, took {frames} frames.");
        Require(frames <= 50, $"Full update cycle with 40 units must complete within at most 50 frames, took {frames}.");
        Require(fog.SweepsInLastCycle == 40, $"All 40 moved units must be swept, got {fog.SweepsInLastCycle}.");
        GD.Print($"FOG_40_UNITS_TIME frames={frames} max_frame_ms={maxFrameTimeMs:F3} total_ms={totalTimeMs:F3}");
    }

    // Units that hold their ground keep their viewshed; only movers are swept again. A unit that
    // moves away or disappears takes its view with it.
    private static void CheckCachedViewsheds()
    {
        // A 4 km flat map, so a far scout sees ground that no one in the main group can see.
        const int size = 201;
        HeightMap flat = new(size, size, 20f, 0.01f, new ushort[size * size]);
        using FogOfWarView fog = new(flat, null, 20f);
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");
        ushort t62 = catalog.RequireDefinitionIndex("t62");
        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];

        System.Numerics.Vector2 scoutStart = new(3800f, 3800f);
        System.Numerics.Vector2 scoutEnd = new(3800f, 200f);
        OwnUnitObservation[] units = new OwnUnitObservation[40];
        units[0] = new OwnUnitObservation(new EntityId(500), t62, scoutStart, 0);
        for (int i = 1; i < units.Length; i++)
        {
            units[i] = new OwnUnitObservation(new EntityId((uint)(500 + i)), t62, new System.Numerics.Vector2(200f + (i % 8) * 20f, 200f + (i / 8) * 20f), 0);
        }
        Apply(replica, bytes, 1, 10, units);
        RunCycle(fog, replica, catalog);
        Require(fog.SweepsInLastCycle == 40, $"The first cycle must sweep every unit, got {fog.SweepsInLastCycle}.");
        Require(fog.IsWorldPointSeen(scoutStart.X, scoutStart.Y), "The scout must see its own position.");
        Require(!fog.IsWorldPointSeen(scoutEnd.X, scoutEnd.Y), "No one sees the scout's destination yet.");
        int seenAtStart = fog.GetSeenCellCount();

        int frames = RunCycle(fog, replica, catalog);
        Require(fog.SweepsInLastCycle == 0, $"Stationary units must not be swept again, got {fog.SweepsInLastCycle}.");
        Require(frames == 1, $"A cycle without movers must finish in one frame, took {frames}.");
        Require(fog.GetSeenCellCount() == seenAtStart, "Cached viewsheds must keep the same seen area.");

        // The scout drives away: only it is swept, its old ground goes dark, its new ground is seen.
        units[0] = units[0] with { Position = scoutEnd };
        Apply(replica, bytes, 2, 20, units);
        RunCycle(fog, replica, catalog);
        Require(fog.SweepsInLastCycle == 1, $"Only the moved unit must be swept, got {fog.SweepsInLastCycle}.");
        Require(!fog.IsWorldPointSeen(scoutStart.X, scoutStart.Y), "Ground only the scout saw must go dark when it leaves.");
        Require(fog.IsWorldPointSeen(scoutEnd.X, scoutEnd.Y), "The scout must see its new position.");

        // The scout is destroyed: everything only it saw is unseen again.
        int seenWithScout = fog.GetSeenCellCount();
        Apply(replica, bytes, 3, 30, units[1..]);
        RunCycle(fog, replica, catalog);
        Require(!fog.IsWorldPointSeen(scoutEnd.X, scoutEnd.Y), "A removed unit must take its view with it.");
        Require(fog.GetSeenCellCount() < seenWithScout, "Removing the scout must shrink the seen area.");
        Require(fog.IsWorldPointSeen(200f, 200f), "The main group still sees its own ground.");
    }

    private static int RunCycle(FogOfWarView fog, ClientReplica replica, UnitCatalog catalog)
    {
        int frames = 1;
        fog.Update(FogOfWarView.CycleInterval, replica, catalog);
        while (fog.IsCycleInProgress && frames < 200)
        {
            fog.Update(0.016f, replica, catalog);
            frames++;
        }
        Require(!fog.IsCycleInProgress, "A fog cycle did not finish.");
        return frames;
    }

    private static void CheckToggle()
    {
        int size = 65;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        using FogOfWarView fog = new(map, null);

        Require(fog.IsEnabled, "Fog of war must be enabled by default.");
        Require((bool)fog.Material.GetShaderParameter("fog_enabled"), "Shader parameter fog_enabled must be true by default.");

        fog.Toggle();
        Require(!fog.IsEnabled, "Toggle() must disable fog of war.");
        Require(!(bool)fog.Material.GetShaderParameter("fog_enabled"), "Shader parameter fog_enabled must be false after toggle.");

        fog.Toggle();
        Require(fog.IsEnabled, "Toggle() again must re-enable fog of war.");
        Require((bool)fog.Material.GetShaderParameter("fog_enabled"), "Shader parameter fog_enabled must be true after second toggle.");
    }

    private static void Apply(ClientReplica replica, byte[] bytes, uint sequence, int tick, OwnUnitObservation[] units)
    {
        int size = WireProtocol.WriteSnapshot(bytes, sequence, tick, units, []);
        Require(replica.TryApply(bytes.AsSpan(0, size)), "Fog regression snapshot was rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
