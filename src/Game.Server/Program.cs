using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;

HeightMap map = HeightMap.CreatePrototype();
using LocalSession session = new(PrototypeScenario.Create(map));
EntityId first = session.Replica.OwnUnits[0].Id;
Vector2 start = session.Replica.OwnUnits[0].Position;
for (int i = 0; i < session.Replica.OwnUnits.Length; i++)
{
    OwnUnitObservation unit = session.Replica.OwnUnits[i];
    session.Move(unit.Id, unit.Position + new Vector2(0, 60));
}

if (args.Contains("--smoke-test"))
{
    for (int i = 0; i < 120; i++) session.Advance(1d / Simulation.TickRate);
    Vector2 end = session.Replica.SamplePosition(first, session.Replica.LatestTick);
    if (session.Tick != 120 || session.Replica.OwnUnits.Length != 12 || end.Y <= start.Y)
        throw new InvalidOperationException("M0 smoke test failed.");
    Console.WriteLine($"M0_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length} start={start} end={end}");
    return;
}

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.WriteLine("Dead Ground M0 host. 20 Hz simulation, 10 Hz snapshots. Ctrl+C to stop.");
using PeriodicTimer timer = new(TimeSpan.FromSeconds(1d / Simulation.TickRate));
ElapsedTimeClock clock = new();
int nextReportTick = Simulation.TickRate;
try
{
    while (await timer.WaitForNextTickAsync(cancellation.Token))
    {
        session.Advance(clock.ReadSeconds());
        if (session.Tick >= nextReportTick)
        {
            Console.WriteLine($"tick={session.Tick} own_units={session.Replica.OwnUnits.Length} tick_ms={session.LastTickMilliseconds:F3} alloc={session.LastTickAllocatedBytes} backlog_s={session.PendingSeconds:F3} received={session.BytesReceived}");
            nextReportTick = session.Tick + Simulation.TickRate;
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
