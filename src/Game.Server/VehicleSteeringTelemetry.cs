using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Server;

public static class VehicleSteeringTelemetry
{
    private sealed class TelemetryMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public void Register(ushort defId, float speed, float radius, float turnRate, float reverseSpeed = 0f, float acceleration = 3f, float deceleration = 4f, bool canPivot = true, float minTurnRadius = 0f)
        {
            _profiles[defId] = new UnitMovementProfile(speed, radius, TraversabilityCapabilities.DefaultVehicle, turnRate, reverseSpeed, acceleration, deceleration, canPivot, minTurnRadius);
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            return _profiles.TryGetValue(definition, out profile);
        }
    }

    public static void GenerateScenarios(string outDir)
    {
        Directory.CreateDirectory(outDir);

        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        // 1. S-curve smooth path following
        {
            TelemetryMovementCatalog cat = new();
            cat.Register(1, speed: 7f, radius: 1.0f, turnRate: 45f, acceleration: 3f, deceleration: 4f);
            NavigationSystem nav = new(grid, cat);
            WorldState world = new(map);
            using Simulation sim = new(world, new RuleSet(nav));
            EntityId u = world.CreateUnit(1, 1, new Vector2(15f, 20f), 7f, initialTurretYaw: -MathF.PI / 2f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, u, new Vector2(45f, 35f)));

            using var writer = File.CreateText(Path.Combine(outDir, "scenario1_scurve.csv"));
            writer.WriteLine("tick,pos_x,pos_z,heading_deg,speed_mps");

            Vector2 prevPos = new(15f, 20f);
            for (int t = 0; t < 120; t++)
            {
                sim.Step();
                UnitState s = world.GetUnitAt(0);
                world.TryGetCombatState(u, out WorldState.CombatState cs);
                float spd = Vector2.Distance(s.Position, prevPos) / Simulation.TickSeconds;
                writer.WriteLine($"{t},{s.Position.X:F2},{s.Position.Y:F2},{cs.HullHeading * 180f / MathF.PI:F1},{spd:F2}");
                prevPos = s.Position;
                if (Vector2.Distance(s.Position, new Vector2(45f, 35f)) <= 0.05f && !s.Moving) break;
            }
        }

        // 2. Wheeled turn radius
        {
            TelemetryMovementCatalog cat = new();
            cat.Register(1, speed: 7f, radius: 1.0f, turnRate: 45f, acceleration: 3f, deceleration: 4f, canPivot: false, minTurnRadius: 5f);
            NavigationSystem nav = new(grid, cat);
            WorldState world = new(map);
            using Simulation sim = new(world, new RuleSet(nav));
            EntityId u = world.CreateUnit(1, 1, new Vector2(20f, 20f), 7f, initialTurretYaw: 0f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, u, new Vector2(40f, 20f)));

            using var writer = File.CreateText(Path.Combine(outDir, "scenario2_wheeled.csv"));
            writer.WriteLine("tick,pos_x,pos_z,heading_deg,speed_mps,omega_deg_s");

            Vector2 prevPos = new(20f, 20f);
            float prevH = 0f;
            for (int t = 0; t < 80; t++)
            {
                sim.Step();
                UnitState s = world.GetUnitAt(0);
                world.TryGetCombatState(u, out WorldState.CombatState cs);
                float spd = Vector2.Distance(s.Position, prevPos) / Simulation.TickSeconds;
                float omega = MathF.Abs(CombatSystem.ShorterArcDelta(prevH, cs.HullHeading)) / Simulation.TickSeconds * (180f / MathF.PI);
                writer.WriteLine($"{t},{s.Position.X:F2},{s.Position.Y:F2},{cs.HullHeading * 180f / MathF.PI:F1},{spd:F2},{omega:F1}");
                prevPos = s.Position;
                prevH = cs.HullHeading;
                if (Vector2.Distance(s.Position, new Vector2(40f, 20f)) <= 0.05f && !s.Moving) break;
            }
        }

        // 3. Reverse gear
        {
            TelemetryMovementCatalog cat = new();
            cat.Register(1, speed: 7f, radius: 1.0f, turnRate: 45f, reverseSpeed: 2.5f, acceleration: 3f, deceleration: 4f);
            NavigationSystem nav = new(grid, cat);
            WorldState world = new(map);
            using Simulation sim = new(world, new RuleSet(nav));
            EntityId u = world.CreateUnit(1, 1, new Vector2(20f, 20f), 7f, initialTurretYaw: 0f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, u, new Vector2(20f, 35f), Mode: MoveMode.Reverse));

            using var writer = File.CreateText(Path.Combine(outDir, "scenario3_reverse.csv"));
            writer.WriteLine("tick,pos_x,pos_z,heading_deg,speed_mps");

            Vector2 prevPos = new(20f, 20f);
            for (int t = 0; t < 150; t++)
            {
                sim.Step();
                UnitState s = world.GetUnitAt(0);
                world.TryGetCombatState(u, out WorldState.CombatState cs);
                float spd = Vector2.Distance(s.Position, prevPos) / Simulation.TickSeconds;
                writer.WriteLine($"{t},{s.Position.X:F2},{s.Position.Y:F2},{cs.HullHeading * 180f / MathF.PI:F1},{spd:F2}");
                prevPos = s.Position;
                if (Vector2.Distance(s.Position, new Vector2(20f, 35f)) <= 0.05f && !s.Moving) break;
            }
        }

        // 4. On-the-fly redirection
        {
            TelemetryMovementCatalog cat = new();
            cat.Register(1, speed: 8f, radius: 1.0f, turnRate: 45f, acceleration: 3f, deceleration: 4f);
            NavigationSystem nav = new(grid, cat);
            WorldState world = new(map);
            using Simulation sim = new(world, new RuleSet(nav));
            EntityId u = world.CreateUnit(1, 1, new Vector2(20f, 20f), 8f, initialTurretYaw: -MathF.PI / 2f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, u, new Vector2(50f, 20f)));

            using var writer = File.CreateText(Path.Combine(outDir, "scenario4_redirect.csv"));
            writer.WriteLine("tick,pos_x,pos_z,heading_deg,speed_mps,order");

            Vector2 prevPos = new(20f, 20f);
            for (int t = 0; t < 70; t++)
            {
                if (t == 25)
                {
                    sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, u, new Vector2(55f, 30f)));
                }
                sim.Step();
                UnitState s = world.GetUnitAt(0);
                world.TryGetCombatState(u, out WorldState.CombatState cs);
                float spd = Vector2.Distance(s.Position, prevPos) / Simulation.TickSeconds;
                int orderId = t < 25 ? 1 : 2;
                writer.WriteLine($"{t},{s.Position.X:F2},{s.Position.Y:F2},{cs.HullHeading * 180f / MathF.PI:F1},{spd:F2},{orderId}");
                prevPos = s.Position;
            }
        }

        // 5. Unachievable wheeled final facing rejection
        {
            OpaqueStructure wall = new(new StructureId(1), 28f, 0f, 10f, 32f, 4f, 30f, isGrounded: true);
            StaticGeometry wallGeo = new(map, [wall]);
            NavigationGrid wallGrid = new(wallGeo, cellSize: 0.5f);
            TelemetryMovementCatalog cat = new();
            cat.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, canPivot: false, minTurnRadius: 5f);
            NavigationSystem nav = new(wallGrid, cat);
            WorldState world = new(map);
            using Simulation sim = new(world, new RuleSet(nav));
            EntityId u = world.CreateUnit(1, 1, new Vector2(15f, 20f), 6f, initialTurretYaw: -MathF.PI / 2f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, u, new Vector2(25f, 20f), FinalFacing: MathF.PI / 2f));
            sim.Step();

            using var writer = File.CreateText(Path.Combine(outDir, "scenario5_unachievable.csv"));
            writer.WriteLine("unit_pos_x,unit_pos_z,status,moving");
            UnitState s = world.GetUnitAt(0);
            writer.WriteLine($"{s.Position.X:F2},{s.Position.Y:F2},{nav.GetUnitStatus(u)},{s.Moving}");
        }

        Console.WriteLine($"STEERING_TELEMETRY_OK {outDir}");
    }
}
