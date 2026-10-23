using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

// Local spectacle and load-test scenario. Combat values still come from Cold War content.
public static class TankBattleScenario
{
    public const int UnitsPerSide = 100;
    public const int WorldCapacity = 256;
    public const int TimeLimitTicks = 10 * 60 * Simulation.TickRate;
    public const ulong DefaultSeed = 20261022;
    public const float MapSize = 3200f;
    public const int TanksPerRow = 50;
    public const float RowSpacing = 55f;
    public const float LateralSpacing = 22f;
    public const float StartOffset = 1250f;
    public const float FiringOffset = 350f;
    public static readonly Vector2 Center = new(MapSize / 2f, MapSize / 2f);

    public static StaticGeometry CreateStaticGeometry()
    {
        const int resolution = 201;
        const float spacing = 16f;
        ushort[] heights = new ushort[resolution * resolution];
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                // Keep the fighting ground open. Low hills frame the arena without hiding the battle.
                float edge = MathF.Max(0f, MathF.Abs(z * spacing - Center.Y) - 700f);
                float h = 4f + edge * 0.025f;
                heights[z * resolution + x] = (ushort)MathF.Round(h * 100f);
            }
        }
        return new StaticGeometry(new HeightMap(resolution, resolution, spacing, 0.01f, heights), []);
    }

    public static Simulation CreateSimulation(StaticGeometry? geometry = null, UnitCatalog? catalog = null,
        ulong seed = DefaultSeed)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();
        WorldState world = new(geometry.HeightMap, WorldCapacity);
        for (int player = 1; player <= 2; player++)
        {
            string type = player == 1 ? "t62" : "m60a1";
            ushort definition = catalog.RequireDefinitionIndex(type);
            UnitDefinition unit = catalog.RequireDefinition(type);
            for (int i = 0; i < UnitsPerSide; i++)
            {
                Vector2 position = GetFormationPosition(player, i, StartOffset);
                EntityId id = world.CreateUnit(player, definition, position, unit.Speed,
                    unit.MaxHealth, catalog.GetAmmoCapacity(unit), player == 1 ? -MathF.PI / 2f : MathF.PI / 2f);
                if (world.TryGetCombatState(id, out WorldState.CombatState state))
                {
                    // Stagger the opening shots over one real reload cycle instead of one giant volley.
                    world.SetCombatState(id, state with { ReloadTicksRemaining = 30 + (i * 37 + player * 11) % 170 });
                }
            }
        }

        OpsPerceptionCatalog perception = new(catalog);
        NavigationSystem navigation = new(new NavigationGrid(geometry, NavigationTuning.DefaultCellSize,
            enableHierarchical: true), new OpsMovementCatalog(catalog));
        DetectionSystem detection = new(geometry, perception, PrototypeScenario.CreateDetectionTuning(catalog));
        CombatSystem combat = new(geometry, new OpsCombatCatalog(catalog), perception, detection, navigation);
        TankBattleDirector director = new(geometry, catalog);
        Simulation simulation = new(world, new OpsRules(navigation, detection, combat, director), seed);
        director.Attach(simulation);
        simulation.SetPlayerAdmitted(1, true);
        simulation.SetPlayerAdmitted(2, true);
        return simulation;
    }

    public static Vector2 GetFormationPosition(int player, int index, float offset)
    {
        if (player is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(player));
        if ((uint)index >= UnitsPerSide) throw new ArgumentOutOfRangeException(nameof(index));
        int row = index / TanksPerRow;
        float direction = player == 1 ? -1f : 1f;
        return new Vector2(Center.X + direction * (offset + row * RowSpacing),
            Center.Y + (index % TanksPerRow - (TanksPerRow - 1) * 0.5f) * LateralSpacing + row * LateralSpacing * 0.5f);
    }

    public static MatchSetup CreateMatchSetup(StaticGeometry geometry, UnitCatalog catalog) => new(
        MatchSetup.CurrentProtocolVersion, catalog.ContentHash, MatchSetup.ComputeMapHash(geometry), "TankBattleDemo",
        PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
}

// The observer has both views; each bot receives only its own side's knowledge.
public sealed class TankBattleDirector : IMissionSystem
{
    public const int DecisionIntervalTicks = 10;
    private readonly PlayerKnowledge _soviet = new(1, TankBattleScenario.WorldCapacity);
    private readonly PlayerKnowledge _us = new(2, TankBattleScenario.WorldCapacity);
    private readonly TankBattleBot _sovietBot;
    private readonly TankBattleBot _usBot;
    private readonly SimulationBotCommandSink _sovietSink = new(null, 1);
    private readonly SimulationBotCommandSink _usSink = new(null, 2);
    private int _finishedTick;
    private MissionEndReason _endReason;

    public bool IsFinished { get; private set; }
    public int SovietRemaining { get; private set; } = TankBattleScenario.UnitsPerSide;
    public int UsRemaining { get; private set; } = TankBattleScenario.UnitsPerSide;
    public int RejectedOrders => _sovietBot.RejectedOrders + _usBot.RejectedOrders;

    public TankBattleDirector(StaticGeometry geometry, UnitCatalog catalog)
    {
        _sovietBot = new TankBattleBot(1, geometry, catalog);
        _usBot = new TankBattleBot(2, geometry, catalog);
    }

    public void Attach(Simulation simulation)
    {
        _sovietSink.SetSimulation(simulation);
        _usSink.SetSimulation(simulation);
    }

    public void Update(WorldState world, int tick)
    {
        if (IsFinished || tick % DecisionIntervalTicks != 0) return;
        _sovietSink.UpdateKnowledge(_soviet);
        _usSink.UpdateKnowledge(_us);
        SovietRemaining = _soviet.Count;
        UsRemaining = _us.Count;
        if (SovietRemaining == 0 || UsRemaining == 0 || tick >= TankBattleScenario.TimeLimitTicks)
        {
            IsFinished = true;
            _finishedTick = tick;
            _endReason = tick >= TankBattleScenario.TimeLimitTicks
                ? MissionEndReason.TimeLimitExpired : MissionEndReason.AllArmedUnitsDestroyed;
            return;
        }
        _sovietBot.Update(_soviet, tick, _sovietSink);
        _usBot.Update(_us, tick, _usSink);
        // SimulationHost owns event acknowledgement for the two local clients.
    }

    public MissionObservation GetObservation(int player)
    {
        MissionStatus status = MissionStatus.InProgress;
        if (IsFinished)
        {
            bool won = player == 1 ? SovietRemaining > 0 && UsRemaining == 0 : UsRemaining > 0 && SovietRemaining == 0;
            status = won ? MissionStatus.Victory : MissionStatus.Defeat;
        }
        return new MissionObservation(2, status, _endReason, TankBattleScenario.TimeLimitTicks, _finishedTick,
            default, TankBattleScenario.Center, 500f, 0);
    }
}

public sealed class TankBattleBot
{
    public const int ContactLossHoldTicks = Simulation.TickRate;
    private readonly int _player;
    private readonly StaticGeometry _geometry;
    private readonly OpsCombatCatalog _combatCatalog;
    private readonly OpsPerceptionCatalog _perceptionCatalog;
    private readonly LineOfSightContext _losContext;
    private readonly Vector2[] _advanceDestinations = new Vector2[TankBattleScenario.WorldCapacity + 1];
    private readonly bool[] _initialized = new bool[TankBattleScenario.WorldCapacity + 1];
    private readonly bool[] _ordered = new bool[TankBattleScenario.WorldCapacity + 1];
    private readonly bool[] _holding = new bool[TankBattleScenario.WorldCapacity + 1];
    private readonly int[] _lastOpportunityTick = new int[TankBattleScenario.WorldCapacity + 1];
    private uint _sequence = 1;
    public int RejectedOrders { get; private set; }

    public TankBattleBot(int player, StaticGeometry geometry, UnitCatalog catalog)
    {
        if (player is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(player));
        _player = player;
        _geometry = geometry;
        _combatCatalog = new OpsCombatCatalog(catalog);
        _perceptionCatalog = new OpsPerceptionCatalog(catalog);
        _losContext = new LineOfSightContext(geometry.Structures.Count);
    }

    public void Update(PlayerKnowledge knowledge, int tick, IBotCommandSink sink)
    {
        if (knowledge.Player != _player) throw new ArgumentException("Bot knowledge belongs to another player.", nameof(knowledge));
        foreach (OwnUnitObservation unit in knowledge.OwnUnits)
        {
            int key = checked((int)unit.Id.Value);
            if (key >= _ordered.Length) throw new InvalidOperationException("Tank demo bot capacity exceeded.");
            if (!_initialized[key])
            {
                float advance = TankBattleScenario.StartOffset - TankBattleScenario.FiringOffset;
                _advanceDestinations[key] = unit.Position + new Vector2(_player == 1 ? advance : -advance, 0);
                _initialized[key] = true;
            }

            bool opportunity = HasFiringOpportunity(unit, knowledge.Contacts);
            if (opportunity) _lastOpportunityTick[key] = tick;
            // Hold through reloads and brief contact loss. Only the first accepted halt cancels movement.
            bool hold = opportunity || _holding[key] && tick - _lastOpportunityTick[key] < ContactLossHoldTicks;
            if (_ordered[key] && _holding[key] == hold) continue;
            Vector2 destination = hold ? unit.Position : _advanceDestinations[key];
            if (sink.EnqueueMove(new MoveCommand(_sequence++, tick, unit.Id, destination)))
            {
                _ordered[key] = true;
                _holding[key] = hold;
            }
            else RejectedOrders++;
        }
    }

    private bool HasFiringOpportunity(in OwnUnitObservation unit, ReadOnlySpan<ContactObservation> contacts)
    {
        if (unit.Ammunition == 0 || unit.Stance != FireStance.FireAtWill ||
            !_combatCatalog.TryGetProfile(unit.Definition, out UnitCombatProfile profile) || profile.Weapon is null)
            return false;
        WeaponDefinition weapon = profile.Weapon;
        float ground = _geometry.HeightMap.GetHeight(unit.Position.X, unit.Position.Y);
        Vector3 muzzle = new(unit.Position.X, ground + weapon.MuzzleHeight, unit.Position.Y);
        foreach (ContactObservation contact in contacts)
        {
            if (!contact.IsCurrent || Vector2.DistanceSquared(unit.Position, contact.Position) > weapon.Range * weapon.Range ||
                !CombatSystem.CanPenetrateObservedContact(unit.Position, weapon, contact, _combatCatalog))
                continue;

            // Known contact position, type and heading plus static geometry are sufficient. No enemy truth lookup.
            float targetGround = _geometry.HeightMap.GetHeight(contact.Position.X, contact.Position.Y);
            if (_perceptionCatalog.TryGetProfile(contact.Type, out UnitPerceptionProfile target) &&
                target.TargetPoints is { RelativeHeights.Length: > 0 } points)
            {
                for (int p = points.RelativeHeights.Length - 1; p >= 0; p--)
                {
                    Vector3 point = new(contact.Position.X, targetGround + points.RelativeHeights[p], contact.Position.Y);
                    if (_geometry.IsLineOfSightClear(muzzle, point, _losContext)) return true;
                }
            }
            else if (_geometry.IsLineOfSightClear(muzzle,
                new Vector3(contact.Position.X, targetGround + 1.5f, contact.Position.Y), _losContext)) return true;
        }
        return false;
    }
}
