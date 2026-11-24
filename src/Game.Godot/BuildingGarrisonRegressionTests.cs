using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

/// <summary>
/// Headless Godot regression tests for Stage 2 PR 2.2 (building garrison client presentation & controls):
/// - Own window/roof figures, Variant A X-Ray silhouettes, ground UnitView hiding, and clickable badges.
/// - Strict Fog-of-War isolation for hostile garrisons (zero figures/X-Ray for concealed or LastKnown enemies).
/// - ContextActionPanel Garrison/Exit/Roof controls with 0 B steady-state allocations.
/// - 120-section MultiMesh draw-call budget (<= 2 draw calls) and 0 B steady-state allocations.
/// </summary>
internal static class BuildingGarrisonRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckOwnAndHostileGarrisonFowVisuals(scene);
        CheckContextActionPanelGarrisonHudAndZeroAlloc(scene);
        Check120SectionsBudgetAndZeroAlloc(scene);
        GD.Print("BUILDING_GARRISON_REGRESSIONS_OK 3/3 passed: own_xray_and_hostile_fow context_action_hud_zero_alloc benchmark_120_sections_2_draws");
    }

    private static void CheckOwnAndHostileGarrisonFowVisuals(Node3D scene)
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ushort rifleIdx = catalog.RequireDefinitionIndex("us_rifleman");
        StructureId section101 = new(101);
        StructureId section102 = new(102);

        Node3D host3D = new() { Name = "GarrisonViewTestHost" };
        Control overlay = new() { Name = "GarrisonBadgeOverlayTest" };
        Camera3D cam = new()
        {
            Position = new Vector3(485f, 45f, 450f)
        };
        scene.AddChild(host3D);
        scene.AddChild(overlay);
        host3D.AddChild(cam);
        cam.LookAt(new Vector3(485f, 8f, 518f), Vector3.Up);

        try
        {
            StructureId clickedBadge = default;
            using BuildingGarrisonView view = new(
                host3D,
                geometry,
                geometry.DestructibleBuildings,
                overlay,
                sid => clickedBadge = sid);

            Require(view.RegisteredBuildingCount == 12, $"Expected 12 registered panelka sections, got {view.RegisteredBuildingCount}.");
            Require(view.IsGarrisonable(section101), "Section 101 must be garrisonable.");

            UnitViewSet unitViews = new(host3D, geometry.HeightMap, catalog, geometry: geometry);
            ContactViewSet contactViews = new(host3D, geometry.HeightMap, catalog, geometry: geometry);
            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            HashSet<EntityId> selected = [];

            // 1. Empty buildings -> 0 figures, 0 X-Ray, 0 badges, 0 draw calls
            ApplySnapshot(replica, snapBuf, seq: 1, tick: 1, [], []);
            view.Update(replica, selected, default, showEntranceHint: false, cam);
            Require(view.ActiveFigureCount == 0 && view.ActiveXRayCount == 0 && view.VisibleBadgeCount == 0,
                "Empty buildings must render zero figures, zero X-Ray silhouettes, and zero badges.");
            Require(view.ActiveMultiMeshDrawCalls == 0, "Empty buildings must consume 0 draw calls.");

            // 2. Two own infantry enter section 101 Interior -> ground meshes hidden, 2 window figures + 2 X-Ray silhouettes
            OwnUnitObservation own1 = new(
                Id: new EntityId(10),
                Definition: rifleIdx,
                Position: new NumericsVector2(478.4f, 517.9f),
                SupportSurface: 0,
                Health: 100,
                GarrisonStructureId: section101,
                GarrisonSlotKind: GarrisonSlotKind.Interior);
            OwnUnitObservation own2 = new(
                Id: new EntityId(11),
                Definition: rifleIdx,
                Position: new NumericsVector2(478.4f, 517.9f),
                SupportSurface: 0,
                Health: 100,
                GarrisonStructureId: section101,
                GarrisonSlotKind: GarrisonSlotKind.Interior);
            selected.Add(own1.Id);

            ApplySnapshot(replica, snapBuf, seq: 2, tick: 2, [own1, own2], []);
            unitViews.Sync(replica, 2);
            Require(!unitViews.Views[own1.Id].Root.Visible && !unitViews.Views[own2.Id].Root.Visible,
                "Ground UnitView must be hidden while infantry is garrisoned.");
            Require(selected.Contains(own1.Id), "Garrisoned own infantry must remain selectable.");

            view.Update(replica, selected, section101, showEntranceHint: false, cam);
            Require(view.ActiveFigureCount == 2, $"Expected 2 own window figures, got {view.ActiveFigureCount}.");
            Require(view.ActiveXRayCount == 2, $"Expected 2 Variant A X-Ray silhouettes, got {view.ActiveXRayCount}.");
            Require(view.ActiveMultiMeshDrawCalls == 2, $"Expected exactly 2 MultiMesh draw calls, got {view.ActiveMultiMeshDrawCalls}.");
            Require(view.VisibleBadgeCount == 1, $"Expected 1 garrison badge for section 101, got {view.VisibleBadgeCount}.");

            // 3. LastKnown hostile contact in section 102 -> ZERO live figures and ZERO X-Ray silhouettes (strict FoW)
            ContactObservation hostileLastKnown = new(
                Id: new ContactId(201),
                State: ContactState.LastKnown,
                Classification: ContactClassification.Infantry,
                Type: rifleIdx,
                Position: new NumericsVector2(492.8f, 517.9f),
                LastSeenTick: 2,
                GarrisonStructureId: section102,
                GarrisonSlotKind: GarrisonSlotKind.Interior);
            ApplySnapshot(replica, snapBuf, seq: 3, tick: 3, [], [hostileLastKnown]);
            contactViews.Sync(replica, 3);
            Require(!contactViews.Views[hostileLastKnown.Id].Root.Visible,
                "Ground ContactView must be hidden for garrisoned hostile contacts.");

            view.Update(replica, selected, default, showEntranceHint: false, cam);
            Require(view.ActiveFigureCount == 0, $"LastKnown hostile contact must render 0 live figures, got {view.ActiveFigureCount}.");
            Require(view.ActiveXRayCount == 0, $"Hostile contact must NEVER render X-Ray silhouettes, got {view.ActiveXRayCount}.");
            Require(view.VisibleBadgeCount == 1, "LastKnown hostile contact should show a faded tactical badge.");

            // 4. Current spotted hostile rooftop contact in section 102 -> 1 hostile figure, still ZERO X-Ray
            ContactObservation hostileCurrentRoof = hostileLastKnown with
            {
                State = ContactState.Current,
                LastSeenTick = 4,
                GarrisonSlotKind = GarrisonSlotKind.Roof
            };
            ApplySnapshot(replica, snapBuf, seq: 4, tick: 4, [], [hostileCurrentRoof]);
            view.Update(replica, selected, default, showEntranceHint: false, cam);
            Require(view.ActiveFigureCount == 1, $"Spotted Current hostile roof contact must render 1 figure, got {view.ActiveFigureCount}.");
            Require(view.ActiveXRayCount == 0, $"Spotted hostile roof contact must render 0 X-Ray silhouettes, got {view.ActiveXRayCount}.");
            _ = clickedBadge;
        }
        finally
        {
            overlay.GetParent()?.RemoveChild(overlay);
            overlay.QueueFree();
            host3D.GetParent()?.RemoveChild(host3D);
            host3D.QueueFree();
        }
    }

    private static void CheckContextActionPanelGarrisonHudAndZeroAlloc(Node3D scene)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ushort rifleIdx = catalog.RequireDefinitionIndex("us_rifleman");
        ContextActionPanel panel = new(catalog);
        scene.AddChild(panel);

        try
        {
            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            HashSet<EntityId> selected = [new EntityId(50)];

            // 1. Infantry outside building -> GARRISON [N] visible, EXIT / ROOF hidden
            OwnUnitObservation outside = new(new EntityId(50), rifleIdx, new NumericsVector2(490f, 470f), 0, Health: 100);
            ApplySnapshot(replica, snapBuf, seq: 1, tick: 1, [outside], []);
            panel.UpdateContext(selected, replica);
            Require(panel.Visible && panel.EnterBuildingButton.Visible && !panel.ExitBuildingButton.Visible && !panel.GarrisonRoofButton.Visible,
                "Outside infantry selection must show EnterBuildingButton and hide Exit/Roof buttons.");

            // 2. Infantry garrisoned inside -> EXIT BLDG [U] and TO ROOF [Y] visible, BOARD hidden
            OwnUnitObservation inside = outside with { GarrisonStructureId = new StructureId(101), GarrisonSlotKind = GarrisonSlotKind.Interior };
            ApplySnapshot(replica, snapBuf, seq: 2, tick: 2, [inside], []);
            panel.UpdateContext(selected, replica);
            Require(panel.Visible && !panel.BoardButton.Visible && panel.ExitBuildingButton.Visible && panel.GarrisonRoofButton.Visible,
                "Garrisoned interior infantry must show Exit and Roof buttons and hide Board.");
            Require(panel.GarrisonRoofButton.Text.Contains("ROOF", StringComparison.OrdinalIgnoreCase),
                $"Expected TO ROOF button text, got '{panel.GarrisonRoofButton.Text}'.");

            // 3. Infantry on roof -> INSIDE [Y] label + verify steady-state 0 B allocations
            OwnUnitObservation onRoof = inside with { GarrisonSlotKind = GarrisonSlotKind.Roof };
            ApplySnapshot(replica, snapBuf, seq: 3, tick: 3, [onRoof], []);
            panel.UpdateContext(selected, replica);
            Require(panel.GarrisonRoofButton.Text.Contains("INSIDE", StringComparison.OrdinalIgnoreCase),
                $"Expected INSIDE button text on roof, got '{panel.GarrisonRoofButton.Text}'.");

            for (int w = 0; w < 10; w++) panel.UpdateContext(selected, replica);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++) panel.UpdateContext(selected, replica);
            long allocAfter = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Require(allocAfter == 0L, $"ContextActionPanel garrison steady-state UpdateContext must allocate 0 bytes, got {allocAfter}.");
        }
        finally
        {
            panel.GetParent()?.RemoveChild(panel);
            panel.QueueFree();
        }
    }

    private static void Check120SectionsBudgetAndZeroAlloc(Node3D scene)
    {
        HeightMap map = UrbanDestructionPlaygroundScenario.CreateHeightMap();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        StructureDestructionProfile destProfile = catalog.RequireBuildingProfile(UrbanDestructionPlaygroundScenario.PanelkaProfileId);
        BuildingGarrisonProfile garrProfile = catalog.RequireBuildingGarrisonProfile(UrbanDestructionPlaygroundScenario.PanelkaProfileId);
        ushort rifleIdx = catalog.RequireDefinitionIndex("us_rifleman");

        const int sectionCount = 120;
        OpaqueStructure[] structures = new OpaqueStructure[sectionCount];
        DestructibleBuildingSpec[] specs = new DestructibleBuildingSpec[sectionCount];
        OwnUnitObservation[] ownUnits = new OwnUnitObservation[sectionCount];

        for (int i = 0; i < sectionCount; i++)
        {
            int col = i % 12;
            int row = i / 12;
            float minX = 100f + col * 24f;
            float minZ = 100f + row * 24f;
            float gy = map.GetHeight(minX + 7.2f, minZ + 5.9f);
            StructureId sid = new(2000 + i);
            structures[i] = new OpaqueStructure(sid, minX, gy, minZ, minX + 14.4f, gy + 13.75f, minZ + 11.8f, isGrounded: true);
            specs[i] = new DestructibleBuildingSpec(sid, destProfile, MathF.PI, garrProfile);
            ownUnits[i] = new OwnUnitObservation(
                Id: new EntityId((uint)(1000 + i)),
                Definition: rifleIdx,
                Position: new NumericsVector2(minX + 7.2f, minZ + 5.9f),
                SupportSurface: 0,
                Health: 100,
                GarrisonStructureId: sid,
                GarrisonSlotKind: GarrisonSlotKind.Interior);
        }

        StaticGeometry geometry = new(map, structures, [], [], [], [], destructibleBuildings: specs);
        Node3D host3D = new() { Name = "Garrison120BenchHost" };
        scene.AddChild(host3D);

        try
        {
            using BuildingGarrisonView view = new(host3D, geometry, geometry.DestructibleBuildings);
            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            ApplySnapshot(replica, snapBuf, seq: 1, tick: 1, ownUnits, []);
            HashSet<EntityId> selected = [];

            // Warmup JIT
            for (int w = 0; w < 10; w++)
            {
                view.Update(replica, selected, default, showEntranceHint: false, camera: null);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                view.Update(replica, selected, default, showEntranceHint: false, camera: null);
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;

            Require(view.ActiveFigureCount == 120, $"Expected 120 active garrison figures, got {view.ActiveFigureCount}.");
            Require(view.ActiveXRayCount == 120, $"Expected 120 active Variant A X-Ray silhouettes, got {view.ActiveXRayCount}.");
            Require(view.ActiveMultiMeshDrawCalls == 2, $"Expected exactly 2 MultiMesh draw calls across 120 occupied sections, got {view.ActiveMultiMeshDrawCalls}.");
            Require(alloc == 0L, $"BuildingGarrisonView steady-state Update across 120 sections must allocate 0 bytes, got {alloc}.");
        }
        finally
        {
            host3D.GetParent()?.RemoveChild(host3D);
            host3D.QueueFree();
        }
    }

    private static void ApplySnapshot(
        ClientReplica replica,
        byte[] buffer,
        uint seq,
        int tick,
        ReadOnlySpan<OwnUnitObservation> units,
        ReadOnlySpan<ContactObservation> contacts)
    {
        int len = WireProtocol.WriteSnapshot(buffer, seq, tick, units, contacts);
        Require(replica.TryApply(buffer.AsSpan(0, len)), $"Failed to apply test snapshot seq={seq}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"BuildingGarrisonRegressionTests failed: {message}");
        }
    }
}
