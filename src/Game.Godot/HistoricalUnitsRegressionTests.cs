using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

// Historical unit types in the client (TDD section 17.1): models at 1:1 scale, real names, and a
// readable grid on kilometer maps.
internal static class HistoricalUnitsRegressionTests
{
    internal static void Run()
    {
        CheckModels();
        CheckNames();
        CheckGridSpacing();
    }

    private static void CheckModels()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        foreach (UnitDefinition def in catalog.Units)
        {
            UnitView view = new(def.Id, def.Faction);
            try
            {
                Require(view.HasVehicleModel, $"{def.Id} has no vehicle model.");
                Require(Mathf.IsEqualApprox(view.Model.Scale.X, 1f), $"{def.Id} must be shown at 1:1 scale.");
            }
            finally { view.Root.Free(); }
        }

        (string Id, string Scene)[] built =
        [
            ("t62", "T62Alt/t62alt.glb"),
            ("m60a1", "M60/m60.glb"),
            ("btr70", "BTR70/btr70.glb"),
        ];
        foreach ((string id, string scene) in built)
        {
            UnitView view = new(id, catalog.RequireDefinition(id).Faction);
            try
            {
                Require(view.LodCount == 3 && view.GetLod(0)!.SceneFilePath.EndsWith(scene, StringComparison.Ordinal),
                    $"{id} did not use {scene} with three LODs.");
                Require(view.LeftMotionMeshCount > 0, $"{id} lost its running gear motion.");
            }
            finally { view.Root.Free(); }
        }
    }

    private static void CheckNames()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        Require(SelectionCard.FormatUnitName(catalog.RequireDefinition("t62")) == "T-62", "The card must show the real type name.");
        Require(SelectionCard.FormatUnitName(catalog.RequireDefinition("m113a1")) == "M113A1", "The card must show the real type name.");
        UnitCatalog prototype = PrototypeScenario.LoadCatalog();
        Require(SelectionCard.FormatUnitName(prototype.RequireDefinition("prototype_tank")) == "Prototype Tank",
            "Prototype units keep their readable names.");

        RoleBadgeOverlay historical = new(catalog);
        RoleBadgeOverlay test = new(prototype);
        try
        {
            Require(historical.GetOwnUnitRoleText(catalog.RequireDefinitionIndex("t62")) == "T-62", "Own badges must show the type.");
            Require(historical.GetOwnUnitRoleText(catalog.RequireDefinitionIndex("brdm2")) == "BRDM-2", "Own badges must show the type.");
            Require(test.GetOwnUnitRoleText(prototype.RequireDefinitionIndex("prototype_tank")) == "TANK",
                "Prototype units keep their role badge.");
        }
        finally
        {
            historical.Free();
            test.Free();
        }
    }

    private static void CheckGridSpacing()
    {
        Require(TerrainView.GridSpacing(HeightMap.CreatePrototype()) == 8f, "Test maps keep the 8 m grid.");
        const int samples = 61;
        HeightMap kilometer = new(samples, samples, 10f, 0.01f, new ushort[samples * samples]);
        Require(TerrainView.GridSpacing(kilometer) == 100f, "Kilometer maps need a 100 m grid.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
