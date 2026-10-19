using System.Numerics;
using System.Xml.Linq;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DeadGround.Rules.Tests;

public sealed class PrototypeTests
{
    [Fact]
    public void ContentLoadsAndSessionOnlyReceivesItsTwelveUnits()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.True(catalog.Units[0].Speed > 0);
        using LocalSession session = new(PrototypeScenario.Create(HeightMap.CreatePrototype()));
        Assert.Equal(12, session.Replica.OwnUnits.Length);
        foreach (OwnUnitObservation unit in session.Replica.OwnUnits) Assert.InRange(unit.Id.Value, 1u, 12u);
    }

    [Fact]
    public void MovementTravelsThroughSerializationAndSnapshotsWithoutAView()
    {
        using LocalSession session = new(PrototypeScenario.Create(HeightMap.CreatePrototype()));
        OwnUnitObservation first = session.Replica.OwnUnits[0];
        Assert.True(session.Move(first.Id, first.Position + new Vector2(0, 60)));
        for (int i = 0; i < 20; i++) session.Advance(0.05);
        Assert.Equal(20, session.Tick);
        Assert.InRange(session.Replica.OwnUnits[0].Position.Y, first.Position.Y + 5.999f, first.Position.Y + 6.001f);
    }

    [Fact]
    public void WarmedSimulationAndSnapshotTicksAllocateNoManagedMemory()
    {
        using LocalSession session = new(PrototypeScenario.Create(HeightMap.CreatePrototype()));
        OwnUnitObservation first = session.Replica.OwnUnits[0];
        session.Move(first.Id, new Vector2(120, 120));
        for (int i = 0; i < 40; i++) session.Advance(0.05);
        bool sent = true;
        using NoGcAllocationWindow window = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            sent &= session.Move(first.Id, new Vector2(120, 120));
            session.Advance(0.05);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, window.Describe(allocated));
        Assert.True(sent);
        Assert.True(session.LastCommandResult!.Value.Queued);
    }

    [Fact]
    public void DifferentFrameRatesProduceTheSameSimulationResult()
    {
        static (int Tick, Vector2 Position) Run(double frame, int frames)
        {
            using LocalSession session = new(PrototypeScenario.Create(HeightMap.CreatePrototype()));
            OwnUnitObservation first = session.Replica.OwnUnits[0];
            session.Move(first.Id, new Vector2(100, 100));
            for (int i = 0; i < frames; i++) session.Advance(frame);
            return (session.Tick, session.Replica.OwnUnits[0].Position);
        }
        Assert.Equal(Run(1d / 30, 60), Run(1d / 120, 240));
    }

    [Fact]
    public void ProjectReferencesRespectTheLayerBoundaries()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeadGround.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        foreach (string file in Directory.GetFiles(Path.Combine(directory.FullName, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            XDocument xml = XDocument.Load(file);
            foreach (XElement reference in xml.Descendants("ProjectReference"))
            {
                string target = Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/'));
                if (name.StartsWith("Engine.", StringComparison.Ordinal)) Assert.StartsWith("Engine.", target);
                if (name != "Game.Godot") Assert.NotEqual("Game.Godot", target);
            }
            if (name != "Game.Godot")
                Assert.DoesNotContain(xml.Descendants("PackageReference"), x =>
                    x.Attribute("Include")!.Value.StartsWith("Godot", StringComparison.Ordinal));
        }
    }
}
