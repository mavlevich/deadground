using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class SmokeTests
{
    private sealed class DictionarySmokeCatalog : IUnitSmokeCatalog
    {
        private readonly Dictionary<ushort, SmokeProfile> _profiles = new();

        public DictionarySmokeCatalog Register(ushort def, SmokeProfile profile)
        {
            _profiles[def] = profile;
            return this;
        }

        public bool TryGetSmokeProfile(ushort definition, out SmokeProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private static SmokeProfile CreateTdaProfile(
        int charges = 2,
        int cooldownTicks = 100,
        int emitDurationTicks = 60,
        int emitIntervalTicks = 10,
        float minCloudSpacing = 10f,
        float cloudRadius = 16f,
        float cloudHeight = 7f,
        int cloudDurationTicks = 400,
        int bloomTicks = 20,
        int fadeTicks = 40,
        float opticalDensity = 0.25f,
        float exhaustOffset = 3.5f) =>
        new(
            SmokeDeliveryKind.EngineScreen,
            charges,
            cooldownTicks,
            emitDurationTicks,
            emitIntervalTicks,
            minCloudSpacing,
            cloudRadius,
            cloudHeight,
            cloudDurationTicks,
            bloomTicks,
            fadeTicks,
            opticalDensity,
            exhaustOffset,
            0f);

    private static HeightMap CreateFlatMap(int size = 101, float cellSize = 4f) =>
        new(size, size, cellSize, 0.01f, new ushort[size * size]);

    [Fact]
    public void SmokeLos_BlocksSightSymmetrically_WhileBallisticLineOfFireStaysClear()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        LineOfSightContext ctx = new();

        SmokeVolume[] volumes =
        [
            new SmokeVolume(
                CenterX: 100f,
                CenterZ: 100f,
                BaseY: 0f,
                TopY: 7f,
                Radius: 16f,
                OpticalDensity: 0.25f,
                Id: new SmokeCloudId(1))
        ];
        ctx.SetSmokeVolumes(volumes, 1);

        Vector3 pointA = new(60f, 2.3f, 100f);
        Vector3 pointB = new(140f, 2.3f, 100f);

        LineOfSightResult resAB = geometry.CheckLineOfSight(pointA, pointB, ctx);
        LineOfSightResult resBA = geometry.CheckLineOfSight(pointB, pointA, ctx);

        Assert.False(resAB.IsClear);
        Assert.Equal(ObstructionKind.Smoke, resAB.ObstructionKind);
        Assert.False(resBA.IsClear);
        Assert.Equal(ObstructionKind.Smoke, resBA.ObstructionKind);
        Assert.False(geometry.IsLineOfSightClear(pointA, pointB, ctx, out bool blockedByTerrainAB));
        Assert.False(blockedByTerrainAB);
        Assert.False(geometry.IsLineOfSightClear(pointB, pointA, ctx, out bool blockedByTerrainBA));
        Assert.False(blockedByTerrainBA);

        // Symmetric distance to smoke threshold (tau = 1.0 at 4m inside cloud)
        Assert.InRange(MathF.Abs(resAB.Distance - resBA.Distance), 0f, 0.05f);

        // Physical ballistic line of fire ignores smoke
        Assert.True(geometry.IsLineOfFireClear(pointA, pointB, ctx));
        Assert.True(geometry.CheckLineOfFire(pointA, pointB, ctx).IsClear);
    }

    [Fact]
    public void SmokeLos_ElevatedObserverLooksOverSmokeCloud()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        LineOfSightContext ctx = new();

        SmokeVolume[] volumes =
        [
            new SmokeVolume(
                CenterX: 100f,
                CenterZ: 100f,
                BaseY: 0f,
                TopY: 7f,
                Radius: 16f,
                OpticalDensity: 0.25f,
                Id: new SmokeCloudId(1))
        ];
        ctx.SetSmokeVolumes(volumes, 1);

        // Ray passing strictly above Y = 7m over the cloud center
        Vector3 ridgeObserver = new(60f, 22f, 100f);
        Vector3 hillTarget = new(140f, 12f, 100f);

        Assert.True(geometry.IsLineOfSightClear(ridgeObserver, hillTarget, ctx));
        Assert.True(geometry.CheckLineOfSight(ridgeObserver, hillTarget, ctx).IsClear);
    }

    [Fact]
    public void SmokeCloudEvaluation_BloomSustainAndFadePhasesWorkAsExpected()
    {
        SmokeCloudState cloud = new(
            Id: new SmokeCloudId(1),
            Owner: 1,
            Position: new Vector2(100f, 100f),
            SupportSurface: 0,
            Radius: 16f,
            Height: 7f,
            CreatedTick: 10,
            ExpiresTick: 210,
            BloomTicks: 40,
            FadeTicks: 40,
            OpticalDensity: 0.25f);

        // Tick 10 (age 0): initial bloom density is 0, radius starts at 25%
        cloud.EvaluateAtTick(10, out float r0, out float d0);
        Assert.Equal(4f, r0, 3);
        Assert.Equal(0f, d0, 3);

        // Mid-bloom (age 20 / 40)
        cloud.EvaluateAtTick(30, out float rMid, out float dMid);
        Assert.Equal(10f, rMid, 3);
        Assert.Equal(0.125f, dMid, 3);

        // Full sustain (age 60)
        cloud.EvaluateAtTick(70, out float rFull, out float dFull);
        Assert.Equal(16f, rFull, 3);
        Assert.Equal(0.25f, dFull, 3);

        // Halfway through fade (remaining 20 / 40)
        cloud.EvaluateAtTick(190, out float rFade, out float dFade);
        Assert.Equal(16f, rFade, 3);
        Assert.Equal(0.125f, dFade, 3);

        // Expired (tick == ExpiresTick)
        cloud.EvaluateAtTick(210, out float rExp, out float dExp);
        Assert.Equal(0f, rExp, 3);
        Assert.Equal(0f, dExp, 3);
    }

    [Fact]
    public void SmokeSystem_EngineScreen_LaysTrailWhileMoving_PreservesMoveOrder_AndEnforcesCooldown()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        SmokeProfile tda = CreateTdaProfile(charges: 2, cooldownTicks: 60, emitDurationTicks: 40, emitIntervalTicks: 10, minCloudSpacing: 6f);
        DictionarySmokeCatalog catalog = new DictionarySmokeCatalog().Register(1, tda);
        SmokeSystem smokeSystem = new(geometry, catalog);

        WorldState world = new(map);
        EntityId tank = world.CreateUnit(1, 1, new Vector2(100f, 100f), speed: 12f, maxHealth: 100);
        RuleSet rules = new(new MovementSystem(), smokeSystem);
        using Simulation sim = new(world, rules);

        // Initialized on tick 0 with full charges
        Assert.True(world.TryGetSmokeState(tank, out WorldState.UnitSmokeState initial));
        Assert.True(initial.HasCapability);
        Assert.Equal(2, initial.ChargesRemaining);
        Assert.Equal(0, initial.CooldownTicksRemaining);
        Assert.Equal(0, initial.ActiveEmitTicksRemaining);

        // Move tank and activate smoke while moving (must not cancel move order)
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, tank, new Vector2(220f, 100f))));
        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(2, 0, tank)));
        // Duplicate deploy in same tick must be rejected
        Assert.False(sim.TryEnqueue(1, new DeploySmokeCommand(3, 0, tank)));

        sim.Step();
        Assert.True(world.TryGetUnit(tank, out UnitState movedTank));
        Assert.True(movedTank.Moving, "Deploying tactical smoke must not cancel an active move order.");
        Assert.Equal(1, world.SmokeCloudCount);

        // Run through emit duration (40 ticks): multiple spaced clouds laid along movement path
        for (int t = 0; t < 40; t++)
        {
            sim.Step();
        }

        Assert.True(world.SmokeCloudCount >= 3, $"Moving TDA unit should lay multiple overlapping clouds, got {world.SmokeCloudCount}.");
        Assert.True(world.TryGetSmokeState(tank, out WorldState.UnitSmokeState postEmit));
        Assert.Equal(1, postEmit.ChargesRemaining);
        Assert.Equal(0, postEmit.ActiveEmitTicksRemaining);
        Assert.True(postEmit.CooldownTicksRemaining > 0, "Cooldown must start once emission finishes.");

        // Deploying during cooldown must be rejected
        Assert.False(sim.TryEnqueue(1, new DeploySmokeCommand(10, sim.Tick, tank)));

        // Advance past cooldown -> second charge becomes usable
        for (int t = 0; t < 65; t++)
        {
            sim.Step();
        }

        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(11, sim.Tick, tank)));
        sim.Step();
        Assert.True(world.TryGetSmokeState(tank, out WorldState.UnitSmokeState secondUse));
        Assert.Equal(0, secondUse.ChargesRemaining);

        // Advance past second emit + cooldown -> 0 charges left, activation permanently rejected
        for (int t = 0; t < 110; t++)
        {
            sim.Step();
        }

        Assert.False(sim.TryEnqueue(1, new DeploySmokeCommand(12, sim.Tick, tank)));
    }

    [Fact]
    public void SmokeSystem_DestroyedUnitStopsEmitting_ExistingCloudsPersistUntilExpiry()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        SmokeProfile tda = CreateTdaProfile(emitDurationTicks: 60, emitIntervalTicks: 10, cloudDurationTicks: 100);
        DictionarySmokeCatalog catalog = new DictionarySmokeCatalog().Register(1, tda);
        SmokeSystem smokeSystem = new(geometry, catalog);

        WorldState world = new(map);
        EntityId tank = world.CreateUnit(1, 1, new Vector2(100f, 100f), speed: 12f, maxHealth: 100);
        RuleSet rules = new(new MovementSystem(), smokeSystem);
        using Simulation sim = new(world, rules);

        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(1, 0, tank)));
        sim.Step();
        int cloudsBeforeDestroy = world.SmokeCloudCount;
        Assert.Equal(1, cloudsBeforeDestroy);

        // Destroy tank mid-emission
        Assert.True(world.RemoveUnit(tank));

        for (int t = 0; t < 50; t++)
        {
            sim.Step();
        }

        // No new clouds emitted after destruction, but existing cloud persists
        Assert.Equal(cloudsBeforeDestroy, world.SmokeCloudCount);

        // Advance past cloud lifetime -> expires cleanly
        for (int t = 0; t < 60; t++)
        {
            sim.Step();
        }

        Assert.Equal(0, world.SmokeCloudCount);
        Assert.Equal(0, smokeSystem.ActiveVolumeCount);
    }

    [Fact]
    public void SmokeSystem_SteadyStateUpdateAndLos_AllocatesZeroBytes()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        SmokeProfile tda = CreateTdaProfile(emitDurationTicks: 200, emitIntervalTicks: 10, cloudDurationTicks: 600);
        DictionarySmokeCatalog catalog = new DictionarySmokeCatalog().Register(1, tda);
        SmokeSystem smokeSystem = new(geometry, catalog);
        LineOfSightContext losCtx = new();

        WorldState world = new(map);
        EntityId tank = world.CreateUnit(1, 1, new Vector2(80f, 100f), speed: 10f, maxHealth: 100);
        RuleSet rules = new(new MovementSystem(), smokeSystem);
        using Simulation sim = new(world, rules);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, tank, new Vector2(300f, 100f))));
        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(2, 0, tank)));

        // Warmup ticks to trigger JIT and initial smoke clouds
        for (int t = 0; t < 30; t++)
        {
            sim.Step();
            smokeSystem.CopyActiveVolumesTo(losCtx);
            _ = geometry.IsLineOfSightClear(new Vector3(50f, 2f, 100f), new Vector3(150f, 2f, 100f), losCtx);
            _ = geometry.CheckLineOfSight(new Vector3(50f, 2f, 100f), new Vector3(150f, 2f, 100f), losCtx);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < 100; t++)
        {
            sim.Step();
            smokeSystem.CopyActiveVolumesTo(losCtx);
            _ = geometry.IsLineOfSightClear(new Vector3(50f, 2f, 100f), new Vector3(150f, 2f, 100f), losCtx);
            _ = geometry.CheckLineOfSight(new Vector3(50f, 2f, 100f), new Vector3(150f, 2f, 100f), losCtx);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void SmokeSystem_Determinism_IdenticalRunsProduceIdenticalCloudsAndHashes()
    {
        static ulong RunAndHash()
        {
            HeightMap map = CreateFlatMap();
            StaticGeometry geometry = new(map, []);
            SmokeProfile tda = CreateTdaProfile();
            DictionarySmokeCatalog catalog = new DictionarySmokeCatalog().Register(1, tda);
            SmokeSystem smokeSystem = new(geometry, catalog);

            WorldState world = new(map);
            EntityId tank = world.CreateUnit(1, 1, new Vector2(100f, 100f), speed: 10f, maxHealth: 100);
            RuleSet rules = new(new MovementSystem(), smokeSystem);
            using Simulation sim = new(world, rules, matchSeed: 999UL);

            sim.TryEnqueue(1, new MoveCommand(1, 0, tank, new Vector2(260f, 140f)));
            sim.TryEnqueue(1, new DeploySmokeCommand(2, 0, tank));

            for (int t = 0; t < 90; t++)
            {
                sim.Step();
            }

            ulong hash = 14695981039346656037UL;
            ReadOnlySpan<SmokeCloudState> clouds = world.SmokeClouds;
            Assert.True(clouds.Length >= 3);
            for (int i = 0; i < clouds.Length; i++)
            {
                hash ^= clouds[i].Id.Value; hash *= 1099511628211UL;
                hash ^= (uint)BitConverter.SingleToInt32Bits(clouds[i].Position.X); hash *= 1099511628211UL;
                hash ^= (uint)BitConverter.SingleToInt32Bits(clouds[i].Position.Y); hash *= 1099511628211UL;
                hash ^= (uint)clouds[i].CreatedTick; hash *= 1099511628211UL;
                hash ^= (uint)clouds[i].ExpiresTick; hash *= 1099511628211UL;
            }
            return hash;
        }

        ulong run1 = RunAndHash();
        ulong run2 = RunAndHash();
        Assert.Equal(run1, run2);
    }
}
