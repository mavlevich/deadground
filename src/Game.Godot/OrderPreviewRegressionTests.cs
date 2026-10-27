using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

internal static class OrderPreviewRegressionTests
{
    internal static void Run(Node3D scene)
    {
        Require(scene is Main, "OrderPreviewRegressionTests requires Main scene.");
        Main main = (Main)scene;

        CheckMixedSelectionPreviewAndClick(main);
        CheckDelayedResultSequenceCorrelation(main);
        CheckUnitRemovalAndRestartClearsMarker(main);
        CheckInputEventHandling_PanelsBlock_NotificationPasses(main);
        CheckFogOfWar_NoHiddenEnemyLeak(main);
        CheckSteadyStateZeroAllocations(main);
        CheckPerUnitIndividualSnappedTargets(main);
        CheckGhostMarkerVisibleInTreeBeforeFirstOrder(main);
        CheckOrderExpirationAndCancellation(main);
        CheckSelectionOver256UnitsDispatchesAll(main);
        CheckSingleUnitSnappedTargetPosition(main);
        CheckOneRejectionOneSuccessYieldsPartial(main);
        CheckFacingGesture_ShortClickVsDrag_IssuesCorrectOrder(main);
        CheckFacingGesture_DragAndReturn_IssuesStandardMove(main);
        CheckFacingGesture_EscapeCancelsGesture_LeavesSelectionIntact(main);
        CheckFacingGesture_CameraRotationInvariance(main);
        CheckFacingGesture_ZeroAllocationsDuringDrag(main);
        CheckFacingGesture_CameraPanPreservesWorldDestination(main);
        CheckFacingGesture_ControlGroupRecallCancelsGesture(main);
        CheckFacingGesture_UnitDeletionCancelsWithoutMouseMotion(main);
        CheckReverseMove_PreviewAndDispatch(main);
        CheckReverseMove_ThroughMainUnhandledInput(main);
    }

    private static void CheckMixedSelectionPreviewAndClick(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog baseCatalog = PrototypeScenario.LoadCatalog();

            // Water from 40 to 60 on X and Z
            TerrainObstacle water = new(40f, 40f, 60f, 60f, TraversabilityCapabilities.Water);
            // Building from 70 to 85 on X and Z
            OpaqueStructure building = new(new StructureId(1), 70f, 0f, 70f, 85f, 10f, 85f);
            StaticGeometry geo = new(map, [building], ReadOnlySpan<BridgeStructure>.Empty, [water]);

            // Define tank (DefaultVehicle) and amphibious vehicle
            UnitDefinition tankDef = baseCatalog.RequireDefinition("prototype_tank");
            UnitDefinition amphDef = tankDef with
            {
                Id = "amph_vehicle",
                Name = "Amphibious Vehicle",
                Capabilities = TraversabilityCapabilities.DefaultVehicle | TraversabilityCapabilities.Water
            };

            UnitCatalog catalog = baseCatalog with { Units = [tankDef, amphDef] };

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);
            OrderNotification notification = new();
            parent.AddChild(notification);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView, notification);

            // Create 1 tank (cannot cross water) and 1 amphibious vehicle (can cross water)
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation uTank = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            OwnUnitObservation uAmph = new(new EntityId(102), 1, new NumericsVector2(22, 20), 0);

            ApplyWithContacts(replica, bytes, 1, 1, [uTank, uAmph], []);
            unitViews.Sync(replica, 1.0);

            // Select both units
            unitViews.Selected.Clear();
            unitViews.Selected.Add(uTank.Id);
            unitViews.Selected.Add(uAmph.Id);

            TestGameSession session = new() { Replica = replica };
            Vector2 viewport = main.GetViewport().GetVisibleRect().Size;

            // 1. Hover over water at (50, 50): only amphibious unit can cross -> Mixed group
            Vector2 waterScreen = camera.UnprojectPosition(new Vector3(50f, 0f, 50f));
            controller.UpdateHover(camera, waterScreen, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, replica);

            Require(overlay.IsPanelVisible, "Overlay must be visible over water.");
            Require(overlay.CurrentValidity == DestinationValidity.Valid, "Overlay validity should be Valid for usable mixed selection.");
            Require(overlay.CurrentIsMixed, "Overlay must report mixed group state over water.");
            Require(overlay.CurrentText == "Destination suitable for 1 of 2 units.",
                $"Expected 'Destination suitable for 1 of 2 units.' but got '{overlay.CurrentText}'.");

            // 2. Right click on water: sends order only to the amphibious unit, skips tank
            bool moveSuccess = controller.ExecuteMoveClick(camera, waterScreen, unitViews, session, out string moveMsg);
            Require(moveSuccess, "Move click should succeed for partial mixed group.");
            Require(session.SubmittedMoves.Count == 1, $"Expected 1 submitted move, got {session.SubmittedMoves.Count}.");
            Require(session.SubmittedMoves[0].Unit == uAmph.Id, "Only amphibious unit should receive move command in water.");
            Require(moveMsg.Contains("1 units ordered to move") && moveMsg.Contains("1 units skipped"),
                $"Notification should report 1 ordered and 1 skipped, got: {moveMsg}");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Sent, "Destination marker should show Sent.");

            // 3. Hover over dry land at (25, 50): both units can move
            Vector2 landScreen = camera.UnprojectPosition(new Vector3(25f, 0f, 50f));
            controller.UpdateHover(camera, landScreen, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, replica);

            Require(overlay.IsPanelVisible, "Overlay must be visible over dry land.");
            Require(overlay.CurrentValidity == DestinationValidity.Valid, "Overlay validity should be Valid on land.");
            Require(!overlay.CurrentIsMixed, "Overlay must not be mixed on clear land.");
            Require(overlay.CurrentText == "Move", $"Expected 'Move', got '{overlay.CurrentText}'.");

            // 4. Hover over building roof center at (77.5, 10, 77.5): too far from any edge to snap (>7m > 3m) -> Blocked
            Vector2 buildingScreen = camera.UnprojectPosition(new Vector3(77.5f, 10f, 77.5f));
            controller.UpdateHover(camera, buildingScreen, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, replica);

            Require(overlay.IsPanelVisible, "Overlay must be visible over building.");
            Require(overlay.CurrentValidity == DestinationValidity.Blocked, $"Overlay validity should be Blocked inside building center, got {overlay.CurrentValidity}.");
            Require(overlay.CurrentReason == DestinationBlockReason.Building, "Overlay reason should be Building.");
            Require(overlay.CurrentText == "Building blocks this position.",
                $"Expected 'Building blocks this position.', got '{overlay.CurrentText}'.");

            // 5. Right click on building center: command is rejected locally without sending
            session.SubmittedMoves.Clear();
            bool buildingClick = controller.ExecuteMoveClick(camera, buildingScreen, unitViews, session, out string blockedMsg);
            Require(!buildingClick, "Move click on building center must be rejected.");
            Require(session.SubmittedMoves.Count == 0, "No move command should be dispatched for blocked target.");
            Require(blockedMsg.Contains("Building blocks this position"),
                $"Blocked message should mention building, got: {blockedMsg}");

            // 6. Hover near building edge: snaps to outside, shows ghost ring, and reports Adjusted
            Vector2 edgeScreen = camera.UnprojectPosition(new Vector3(70.8f, 10f, 77.5f));
            controller.UpdateHover(camera, edgeScreen, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, replica);

            Require(overlay.CurrentValidity == DestinationValidity.Adjusted,
                $"Overlay validity should be Adjusted near edge, got {overlay.CurrentValidity}.");
            Require(destinationView.IsGhostVisible, "Ghost ring must be visible for adjusted destination.");
            Require(destinationView.GhostPosition.X < 70.0f,
                $"Ghost position X {destinationView.GhostPosition.X} must snap outside the west wall (X < 70).");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckDelayedResultSequenceCorrelation(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);
            OrderNotification notification = new();
            parent.AddChild(notification);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView, notification);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            EntityId unitId = new(201);
            OwnUnitObservation u1 = new(unitId, 0, new NumericsVector2(30, 30), 0, NavOrderSequence: 0, NavStatus: PathRequestStatus.None);

            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(unitId);

            TestGameSession session = new() { Replica = replica };

            // 1. Issue first move order (Sequence 1)
            Vector2 targetScreen1 = camera.UnprojectPosition(new Vector3(40f, 0f, 40f));
            Require(controller.ExecuteMoveClick(camera, targetScreen1, unitViews, session, out _), "First order should execute.");
            Require(session.SubmittedMoves.Count == 1 && session.SubmittedMoves[0].Sequence == 1, "First move sequence must be 1.");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Sent, "Marker state should be Sent.");

            // 2. Issue second move order (Sequence 2)
            Vector2 targetScreen2 = camera.UnprojectPosition(new Vector3(60f, 0f, 60f));
            Require(controller.ExecuteMoveClick(camera, targetScreen2, unitViews, session, out _), "Second order should execute.");
            Require(session.SubmittedMoves.Count == 2 && session.SubmittedMoves[1].Sequence == 2, "Second move sequence must be 2.");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Sent, "Marker state should be Sent for new order.");

            // 3. Stale snapshot arrives from order 1 with NoPath (NavOrderSequence = 1)
            OwnUnitObservation staleObs = new(unitId, 0, new NumericsVector2(30, 30), 0, NavOrderSequence: 1, NavStatus: PathRequestStatus.NoPath);
            ApplyWithContacts(replica, bytes, 2, 2, [staleObs], []);
            controller.UpdateOrders(session, delta: 0.05);

            // Stale failure must NOT recolor the marker for order 2!
            Require(destinationView.CurrentState != OrderMarkerVisualState.Failed,
                "Stale NoPath from previous order sequence (1) must not recolor newer order marker (2) to Failed.");
            Require(destinationView.CurrentState is OrderMarkerVisualState.Sent or OrderMarkerVisualState.Processing,
                "Marker must remain in Sent or Processing state while awaiting result for order 2.");

            // 4. Up-to-date snapshot arrives for order 2 with PathFound (NavOrderSequence = 2)
            OwnUnitObservation currentObs = new(unitId, 0, new NumericsVector2(30, 30), 0, NavOrderSequence: 2, NavStatus: PathRequestStatus.PathFound);
            ApplyWithContacts(replica, bytes, 3, 3, [currentObs], []);
            controller.UpdateOrders(session, delta: 0.05);

            Require(destinationView.CurrentState == OrderMarkerVisualState.PathFound,
                "Correlated result with NavOrderSequence = 2 must update marker state to PathFound.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckUnitRemovalAndRestartClearsMarker(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);
            OrderNotification notification = new();
            parent.AddChild(notification);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView, notification);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            EntityId unitId = new(301);
            OwnUnitObservation u1 = new(unitId, 0, new NumericsVector2(30, 30), 0);

            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(unitId);

            TestGameSession session = new() { Replica = replica };

            // Issue order -> marker visible
            Vector2 targetScreen = camera.UnprojectPosition(new Vector3(45f, 0f, 45f));
            Require(controller.ExecuteMoveClick(camera, targetScreen, unitViews, session, out _), "Order should execute.");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Sent, "Marker should be Sent.");
            Require(destinationView.IsSubmittedMarkerVisible, "Marker should be visible.");

            // 1. Unit is destroyed / removed from world replica
            ApplyWithContacts(replica, bytes, 2, 2, [], []);
            controller.UpdateOrders(session, delta: 0.05);

            Require(!destinationView.IsSubmittedMarkerVisible && destinationView.CurrentState == OrderMarkerVisualState.Hidden,
                "Removing the ordered unit must clear and hide the active destination marker.");
            Require(controller.TrackedOrderCount == 0, "Tracked orders must be cleared when unit is removed.");

            // 2. Restart / Reset clears marker and ghost
            ApplyWithContacts(replica, bytes, 3, 3, [u1], []);
            unitViews.Sync(replica, 3.0);
            Require(controller.ExecuteMoveClick(camera, targetScreen, unitViews, session, out _), "Order should execute.");
            Require(destinationView.IsSubmittedMarkerVisible, "Marker should be visible again.");

            controller.Reset();
            Require(!destinationView.IsSubmittedMarkerVisible && destinationView.CurrentState == OrderMarkerVisualState.Hidden,
                "Reset() must hide destination marker on scenario restart.");
            Require(!destinationView.IsGhostVisible, "Ghost ring must be hidden after Reset().");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckInputEventHandling_PanelsBlock_NotificationPasses(Main main)
    {
        // Case 10: Input event handling: click through transparent notification vs blocked click over HUD panels
        Vector2 viewport = main.GetViewport().GetVisibleRect().Size;

        // Verify OrderNotification has MouseFilter.Ignore
        Require(main.OrderNotification.MouseFilter == Control.MouseFilterEnum.Ignore,
            "OrderNotification must have MouseFilter.Ignore to allow clicks to pass through.");

        // Overlapping notification toast does not occlude clicks
        main.OrderNotification.ShowNotification("Test notification");
        Require(main.OrderNotification.Visible, "Notification must be visible.");

        main.RefreshOccludingPanels();
        Rect2 mainToastRect = main.OrderNotification.GetGlobalRect();

        // 1. Input occluding panels MUST NOT contain the toast
        for (int i = 0; i < main.InputOccludingPanels.Length; i++)
        {
            Require(main.InputOccludingPanels[i] != mainToastRect,
                "Main.InputOccludingPanels must not contain OrderNotification rect.");
        }

        // 2. Synthetic blocking HUD panel blocks clicks
        Rect2 hudPanel = new(24, 24, 300, 120);
        Vector2 insidePanel = new(100f, 60f);
        Vector2 outsidePanel = new(500f, 400f);

        Require(SelectionController.IsPointOccluded(insidePanel, [hudPanel]),
            "Point inside HUD panel must be occluded.");
        Require(!SelectionController.IsPointOccluded(outsidePanel, [hudPanel]),
            "Point outside HUD panel must not be occluded.");

        // 3. Settings panel when open adds to occluding panels and blocks input
        Require(!main.SettingsPanel.IsOpen, "Settings panel should be closed initially.");
        main.SettingsPanel.Toggle();
        try
        {
            Require(main.SettingsPanel.IsOpen, "Settings panel must be open after toggle.");
            main.RefreshOccludingPanels();
            Rect2 settingsRect = main.SettingsPanel.GetContainerRect();
            Require(settingsRect.Size.X > 0f && settingsRect.Size.Y > 0f, "Settings container rect must have positive size.");

            Vector2 settingsCenter = settingsRect.Position + settingsRect.Size * 0.5f;
            Require(SelectionController.IsPointOccluded(settingsCenter, main.InputOccludingPanels),
                "Settings panel center must be occluded in InputOccludingPanels while open.");
        }
        finally
        {
            main.SettingsPanel.Close();
            main.RefreshOccludingPanels();
        }
    }

    private static void CheckFogOfWar_NoHiddenEnemyLeak(Main main)
    {
        // Case 11: Cursor preview does not query or leak hidden enemy data under fog of war
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        OrderCursorOverlay overlay = new();
        OrderDestinationView destinationView = new();
        OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

        // Client replica has no knowledge of hidden enemy
        ClientReplica playerReplica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation myTank = new(new EntityId(501), 0, new NumericsVector2(10, 10), 0);
        ApplyWithContacts(playerReplica, bytes, 1, 1, [myTank], []);

        Camera3D camera = CreateTestCamera(main, new Vector3(50, 0, 50), 100f);
        Vector2 viewport = main.GetViewport().GetVisibleRect().Size;

        // Hidden enemy is located at (60, 60), completely unspotted
        Vector2 hiddenEnemyScreen = camera.UnprojectPosition(new Vector3(60f, 0f, 60f));

        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet unitViews = new(parent, map, catalog);
        unitViews.Sync(playerReplica, 1.0);
        unitViews.Selected.Clear();
        unitViews.Selected.Add(myTank.Id);

        try
        {
            controller.UpdateHover(camera, hiddenEnemyScreen, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, playerReplica);

            // Preview must show normal move cursor and must NOT leak enemy contact
            Require(overlay.CurrentValidity == DestinationValidity.Valid, "Destination must be evaluated purely as valid terrain.");
            Require(overlay.CurrentText == "Move", $"Expected 'Move', got '{overlay.CurrentText}'.");
            Require(overlay.CurrentReason == DestinationBlockReason.None, "No enemy block reason should be reported.");
        }
        finally
        {
            unitViews.Clear();
            parent.QueueFree();
            camera.QueueFree();
            overlay.QueueFree();
            destinationView.QueueFree();
        }
    }

    private static void CheckSteadyStateZeroAllocations(Main main)
    {
        // Case 12: Zero allocations after warmup at steady state
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet unitViews = new(parent, map, catalog);
        Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

        OrderCursorOverlay overlay = new();
        parent.AddChild(overlay);
        OrderDestinationView destinationView = new();
        parent.AddChild(destinationView);

        OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation u1 = new(new EntityId(601), 0, new NumericsVector2(20, 20), 0);
        ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
        unitViews.Sync(replica, 1.0);
        unitViews.Selected.Clear();
        unitViews.Selected.Add(u1.Id);

        Vector2 mousePos = camera.UnprojectPosition(new Vector3(30f, 0f, 30f));
        Vector2 viewport = main.GetViewport().GetVisibleRect().Size;

        try
        {
            // Warmup
            for (int i = 0; i < 20; i++)
            {
                controller.UpdateHover(camera, mousePos, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, replica);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 100; i++)
            {
                controller.UpdateHover(camera, mousePos, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, replica);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, $"Steady state UpdateHover allocated {allocated} bytes (expected 0).");
        }
        finally
        {
            unitViews.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckPerUnitIndividualSnappedTargets(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
            StaticGeometry geo = new(map, [building]);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            // Unit 1 is south-west (25, 45), Unit 2 is north-east (50, 75)
            EntityId id1 = new(401);
            EntityId id2 = new(402);
            OwnUnitObservation u1 = new(id1, 0, new NumericsVector2(25, 45), 0);
            OwnUnitObservation u2 = new(id2, 0, new NumericsVector2(50, 75), 0);

            ApplyWithContacts(replica, bytes, 1, 1, [u1, u2], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(id1);
            unitViews.Selected.Add(id2);

            TestGameSession session = new() { Replica = replica };

            // Target is inside corner of building at (41, 0, 59)
            Vector2 targetScreen = camera.UnprojectPosition(new Vector3(41f, 0f, 59f));
            bool success = controller.ExecuteMoveClick(camera, targetScreen, unitViews, session, out _);

            Require(success, "Move click should succeed with snapped targets.");
            Require(session.SubmittedMoves.Count == 2, $"Expected 2 moves, got {session.SubmittedMoves.Count}.");
            Require(session.SubmittedMoves[0].Unit == id1, "First move must be for unit 1.");
            Require(session.SubmittedMoves[1].Unit == id2, "Second move must be for unit 2.");

            NumericsVector2 targetUnit1 = session.SubmittedMoves[0].Target;
            NumericsVector2 targetUnit2 = session.SubmittedMoves[1].Target;

            // Unit 1 (starting west) must snap to west side of building (X < 40)
            Require(targetUnit1.X < 40.0f, $"Unit 1 target {targetUnit1} must be west of building.");
            // Unit 2 (starting north) must snap to north side of building (Y > 60)
            Require(targetUnit2.Y > 60.0f, $"Unit 2 target {targetUnit2} must be north of building.");
            // Targets must be distinct!
            Require(targetUnit1 != targetUnit2, "Units at different positions must receive their own distinct snapped targets.");
            Require(!destinationView.IsSubmittedMarkerVisible, "Divergent group targets must NOT display a single common destination marker.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckGhostMarkerVisibleInTreeBeforeFirstOrder(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            OpaqueStructure building = new(new StructureId(1), 70f, 0f, 70f, 85f, 10f, 85f);
            StaticGeometry geo = new(map, [building]);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            EntityId id1 = new(501);
            OwnUnitObservation u1 = new(id1, 0, new NumericsVector2(50, 77.5f), 0);

            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(id1);

            Vector2 viewport = main.GetViewport().GetVisibleRect().Size;

            // Hover near building edge (70.8, 10, 77.5) before any order has ever been submitted
            Vector2 edgeScreen = camera.UnprojectPosition(new Vector3(70.8f, 10f, 77.5f));
            controller.UpdateHover(camera, edgeScreen, viewport, isPointerOccluded: false, hasEnemyContactUnderMouse: false, unitViews, replica);

            // Crucial verification: ghost must be visible in tree!
            Require(destinationView.IsGhostVisible, "Ghost ring Visible flag must be true.");
            Require(destinationView.IsGhostVisibleInTree, "Ghost ring must be visible in tree before first order.");
            Require(!destinationView.IsSubmittedMarkerVisible, "Submitted order marker must not be visible before first order.");
            Require(!destinationView.IsSubmittedMarkerVisibleInTree, "Submitted marker must not be visible in tree.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckOrderExpirationAndCancellation(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderNotification notification = new();
            parent.AddChild(notification);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView, notification);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            EntityId unitId = new(601);
            OwnUnitObservation u1 = new(unitId, 0, new NumericsVector2(30, 30), 0);

            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(unitId);

            TestGameSession session = new() { Replica = replica };

            // 1. Submit move order
            Vector2 targetScreen = camera.UnprojectPosition(new Vector3(40f, 0f, 40f));
            Require(controller.ExecuteMoveClick(camera, targetScreen, unitViews, session, out _), "Order should execute.");
            Require(destinationView.IsSubmittedMarkerVisible, "Marker must be visible upon send.");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Sent, "State should be Sent.");

            // 2. Snapshot arrives: PathFound
            OwnUnitObservation pathFoundObs = new(unitId, 0, new NumericsVector2(30, 30), 0, NavOrderSequence: 1, NavStatus: PathRequestStatus.PathFound);
            ApplyWithContacts(replica, bytes, 2, 2, [pathFoundObs], []);
            controller.UpdateOrders(session, delta: 0.05);

            Require(destinationView.CurrentState == OrderMarkerVisualState.PathFound, "Marker state should be PathFound.");
            Require(destinationView.IsSubmittedMarkerVisible, "Marker should remain visible while active.");

            // 3. Time elapses: marker fades out and hides after 4.5s
            controller.UpdateOrders(session, delta: 5.0);
            Require(!destinationView.IsSubmittedMarkerVisible, "Marker must be hidden after expiring.");

            // 4. Repeated UpdateOrders with unit still in PathFound must NOT resurrect marker!
            controller.UpdateOrders(session, delta: 0.1);
            Require(!destinationView.IsSubmittedMarkerVisible, "Completed marker must NOT resurrect after expiration.");

            // 5. Cancellation by new action: issuing a new order immediately shows new marker at new target
            Vector2 targetScreen2 = camera.UnprojectPosition(new Vector3(60f, 0f, 60f));
            Require(controller.ExecuteMoveClick(camera, targetScreen2, unitViews, session, out _), "Second order should execute.");
            Require(destinationView.IsSubmittedMarkerVisible, "New order must immediately display marker.");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Sent, "New marker state must be Sent.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckSelectionOver256UnitsDispatchesAll(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            const int unitCount = 300;
            OwnUnitObservation[] army = new OwnUnitObservation[unitCount];
            unitViews.Selected.Clear();

            for (int i = 0; i < unitCount; i++)
            {
                EntityId id = new(1000 + (uint)i);
                army[i] = new OwnUnitObservation(id, 0, new NumericsVector2(20f + (i % 20) * 2f, 20f + (i / 20) * 2f), 0);
                unitViews.Selected.Add(id);
            }

            ApplyWithContacts(replica, bytes, 1, 1, army, []);
            unitViews.Sync(replica, 1.0);

            TestGameSession session = new() { Replica = replica };

            Vector2 targetScreen = camera.UnprojectPosition(new Vector3(50f, 0f, 50f));
            bool moveSuccess = controller.ExecuteMoveClick(camera, targetScreen, unitViews, session, out string msg);

            Require(moveSuccess, "Move click for 300 units should succeed.");
            Require(session.SubmittedMoves.Count == 300,
                $"All 300 units must receive move commands without truncation. Got {session.SubmittedMoves.Count}.");
            Require(controller.TrackedOrderCount == 300,
                $"All 300 units must be tracked. Got {controller.TrackedOrderCount}.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckSingleUnitSnappedTargetPosition(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
            StaticGeometry geo = new(map, [building]);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            EntityId id1 = new(801);
            OwnUnitObservation u1 = new(id1, 0, new NumericsVector2(25, 50), 0);

            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(id1);

            TestGameSession session = new() { Replica = replica };

            // Click inside building near west edge at (41, 0, 50)
            Vector2 targetScreen = camera.UnprojectPosition(new Vector3(41f, 0f, 50f));
            bool success = controller.ExecuteMoveClick(camera, targetScreen, unitViews, session, out string msg);

            Require(success, "Click near obstacle edge should succeed by snapping.");
            Require(destinationView.IsSubmittedMarkerVisible, "Marker must be visible for single unit.");
            Require(destinationView.TargetPosition.X < 40.0f,
                $"Marker X {destinationView.TargetPosition.X} must be at the snapped position outside building (X < 40), not inside at 41.");
            Require(session.SubmittedMoves[0].Target.X < 40.0f,
                "Submitted move target must match snapped position.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckOneRejectionOneSuccessYieldsPartial(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderNotification notification = new();
            parent.AddChild(notification);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView, notification);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            EntityId id1 = new(701);
            EntityId id2 = new(702);
            OwnUnitObservation u1 = new(id1, 0, new NumericsVector2(30, 30), 0);
            OwnUnitObservation u2 = new(id2, 0, new NumericsVector2(32, 30), 0);

            ApplyWithContacts(replica, bytes, 1, 1, [u1, u2], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(id1);
            unitViews.Selected.Add(id2);

            TestGameSession session = new() { Replica = replica };

            // Issue order for both units (same destination)
            Vector2 targetScreen = camera.UnprojectPosition(new Vector3(50f, 0f, 50f));
            Require(controller.ExecuteMoveClick(camera, targetScreen, unitViews, session, out _), "Order should execute.");
            Require(session.SubmittedMoves.Count == 2, "2 moves should be submitted.");
            Require(destinationView.IsSubmittedMarkerVisible, "Marker should be visible for common destination.");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Sent, "Initial state should be Sent.");

            uint seq1 = session.SubmittedMoves[0].Sequence;
            uint seq2 = session.SubmittedMoves[1].Sequence;

            // Step 1: Server rejects unit 1 (Queued = false)
            session.LastCommandResult = new CommandResult(seq1, 1, Queued: false);
            // Unit 2 is still searching in the world
            OwnUnitObservation u2Searching = new(id2, 0, new NumericsVector2(32, 30), 0, NavOrderSequence: seq2, NavStatus: PathRequestStatus.Searching);
            ApplyWithContacts(replica, bytes, 2, 2, [u1, u2Searching], []);

            controller.UpdateOrders(session, delta: 0.05);

            // Rejection of unit 1 must NOT finalize group tracking and must NOT mark order as Failed yet
            Require(destinationView.CurrentState != OrderMarkerVisualState.Failed,
                "Group marker must NOT be Failed while unit 2 is still searching.");
            Require(destinationView.CurrentState == OrderMarkerVisualState.Processing,
                $"Group marker should be in Processing while unit 2 is searching, got: {destinationView.CurrentState}.");

            // Step 2: Unit 2 finishes pathfinding and reports PathFound
            OwnUnitObservation u2PathFound = new(id2, 0, new NumericsVector2(32, 30), 0, NavOrderSequence: seq2, NavStatus: PathRequestStatus.PathFound);
            ApplyWithContacts(replica, bytes, 3, 3, [u1, u2PathFound], []);

            controller.UpdateOrders(session, delta: 0.05);

            // Now that all units are resolved (1 failed, 1 succeeded), marker must become Partial!
            Require(destinationView.CurrentState == OrderMarkerVisualState.Partial,
                $"Group with 1 rejection and 1 path found must resolve to Partial, got: {destinationView.CurrentState}.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckFacingGesture_ShortClickVsDrag_IssuesCorrectOrder(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(30, 0, 30), 80f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);

            TestGameSession session = new() { Replica = replica };
            Vector2 screenPos = camera.UnprojectPosition(new Vector3(30, 0, 30));

            // 1. Short click: press and immediately release without dragging
            Require(controller.BeginFacingGesture(camera, screenPos, unitViews, session, out _), "BeginFacingGesture failed");
            Require(controller.IsFacingGestureActive, "Facing gesture must be active");
            Require(destinationView.IsSubmittedMarkerVisible, "Marker must show on press");
            Require(!destinationView.IsFacingArrowVisible, "Facing arrow must not show on press");

            Require(controller.EndFacingGesture(camera, screenPos, unitViews, session, out _), "EndFacingGesture failed");
            Require(session.SubmittedMoves.Count == 1, "One move must be submitted");
            Require(session.SubmittedMoves[0].FinalFacing == null, "Short click must submit null final facing");
            Require(!destinationView.IsFacingArrowVisible, "Facing arrow must be hidden after dispatch");

            // 2. Drag: press at (30, 30) and drag to point East (+X in 3D, heading -pi/2)
            Require(controller.BeginFacingGesture(camera, screenPos, unitViews, session, out _), "BeginFacingGesture failed for drag");
            Vector2 screenDragEast = camera.UnprojectPosition(new Vector3(45, 0, 30));
            controller.UpdateFacingGesture(camera, screenDragEast);

            Require(controller.IsFacingDetermined, "Facing must be determined after drag past threshold");
            Require(destinationView.IsFacingArrowVisible, "Facing arrow must be visible during drag");
            Require(MathF.Abs(CombatSystem.ShorterArcDelta(destinationView.FacingArrowYaw, -MathF.PI / 2f)) < 0.05f,
                $"Arrow yaw must face East (-pi/2), got: {destinationView.FacingArrowYaw}");

            Require(controller.EndFacingGesture(camera, screenDragEast, unitViews, session, out _), "EndFacingGesture failed for drag");
            Require(session.SubmittedMoves.Count == 2, "Second move must be submitted");
            Require(session.SubmittedMoves[1].FinalFacing != null, "Drag gesture must submit non-null final facing");
            Require(MathF.Abs(CombatSystem.ShorterArcDelta(session.SubmittedMoves[1].FinalFacing!.Value, -MathF.PI / 2f)) < 0.05f,
                $"Final facing must match East (-pi/2), got: {session.SubmittedMoves[1].FinalFacing}");
            Require(!destinationView.IsFacingArrowVisible, "Facing arrow must be hidden after release");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckFacingGesture_DragAndReturn_IssuesStandardMove(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(30, 0, 30), 80f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);

            TestGameSession session = new() { Replica = replica };
            Vector2 screenPos = camera.UnprojectPosition(new Vector3(30, 0, 30));

            // Press and drag 50 pixels away
            Require(controller.BeginFacingGesture(camera, screenPos, unitViews, session, out _), "BeginFacingGesture failed");
            Vector2 screenDrag = screenPos + new Vector2(50, 0);
            controller.UpdateFacingGesture(camera, screenDrag);
            Require(controller.IsFacingDetermined, "Facing should be determined");
            Require(destinationView.IsFacingArrowVisible, "Arrow should be visible");

            // Drag back to start (within 3 pixels)
            Vector2 screenReturn = screenPos + new Vector2(2, 0);
            controller.UpdateFacingGesture(camera, screenReturn);
            Require(!controller.IsFacingDetermined, "Facing must reset to undetermined when returned to start");
            Require(!destinationView.IsFacingArrowVisible, "Arrow must hide when returned to start");

            // Release
            Require(controller.EndFacingGesture(camera, screenReturn, unitViews, session, out _), "EndFacingGesture failed");
            Require(session.SubmittedMoves.Count == 1, "Expected 1 submitted move");
            Require(session.SubmittedMoves[0].FinalFacing == null, "Move must have null final facing when returned to start");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckFacingGesture_EscapeCancelsGesture_LeavesSelectionIntact(Main main)
    {
        if (main.Session.Replica.OwnUnits.Length == 0) return;
        EntityId unitId = main.Session.Replica.OwnUnits[0].Id;
        main.UnitViews.Selected.Clear();
        main.UnitViews.Selected.Add(unitId);
        main.UnitViews.UpdateSelectionVisuals();

        Vector2 screenPos = main.Camera.UnprojectPosition(new Vector3(30, 0, 30));

        // Press and drag via main PreviewController
        Require(main.PreviewController.BeginFacingGesture(main.Camera, screenPos, main.UnitViews, main.Session, out _), "BeginFacingGesture failed");
        main.PreviewController.UpdateFacingGesture(main.Camera, screenPos + new Vector2(40, 20), main.UnitViews);
        Require(main.PreviewController.IsFacingGestureActive, "Expected facing gesture to be active");
        Require(main.DestinationMarker.IsFacingArrowVisible, "Expected facing arrow to be visible");

        // Cancel via real Escape key input dispatched through Main._UnhandledInput
        using InputEventKey escapeKey1 = new() { Keycode = Key.Escape, Pressed = true, Echo = false };
        main._UnhandledInput(escapeKey1);

        Require(!main.PreviewController.IsFacingGestureActive, "Gesture must be inactive after real Escape key");
        Require(!main.PreviewController.IsFacingDetermined, "Facing must be undetermined after real Escape key");
        Require(!main.DestinationMarker.IsFacingArrowVisible, "Arrow must hide after real Escape key");
        Require(!main.DestinationMarker.IsSubmittedMarkerVisible, "Marker must hide after real Escape key");

        // Verify selection is still intact after first Escape
        Require(main.UnitViews.Selected.Count == 1, "Unit selection must remain intact after first Escape");
        Require(main.UnitViews.Selected.Contains(unitId), "Selected unit must match");

        // Second Escape clears selection
        using InputEventKey escapeKey2 = new() { Keycode = Key.Escape, Pressed = true, Echo = false };
        main._UnhandledInput(escapeKey2);
        Require(main.UnitViews.Selected.Count == 0, "Second Escape must clear unit selection");
    }

    private static void CheckFacingGesture_CameraPanPreservesWorldDestination(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(30, 0, 30), 80f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);

            TestGameSession session = new() { Replica = replica };
            Vector3 worldTarget = new(40f, 0f, 50f);
            Vector2 screenPos = camera.UnprojectPosition(worldTarget);

            // Press at worldTarget
            Require(controller.BeginFacingGesture(camera, screenPos, unitViews, session, out _), "BeginFacingGesture failed");

            // Pan camera by 10 meters along X
            camera.Position += new Vector3(10f, 0f, 0f);

            // Drag mouse
            controller.UpdateFacingGesture(camera, screenPos + new Vector2(30, 20), unitViews);

            // Release
            Require(controller.EndFacingGesture(camera, screenPos + new Vector2(30, 20), unitViews, session, out _), "EndFacingGesture failed");

            Require(session.SubmittedMoves.Count == 1, "Expected 1 submitted move");
            NumericsVector2 sentTarget = session.SubmittedMoves[0].Target;
            float distFromOriginal = NumericsVector2.Distance(sentTarget, new NumericsVector2(40f, 50f));
            Require(distFromOriginal <= 0.05f, $"Target must preserve original world coordinates despite camera pan: got {sentTarget}");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckFacingGesture_ControlGroupRecallCancelsGesture(Main main)
    {
        if (main.Session.Replica.OwnUnits.Length < 2) return;
        EntityId unit1 = main.Session.Replica.OwnUnits[0].Id;
        EntityId unit2 = main.Session.Replica.OwnUnits[1].Id;

        // Group 1: unit 1
        main.UnitViews.Selected.Clear();
        main.UnitViews.Selected.Add(unit1);
        main.SelectionController.StoreControlGroup(main.UnitViews, 1);

        // Group 2: unit 2
        main.UnitViews.Selected.Clear();
        main.UnitViews.Selected.Add(unit2);
        main.SelectionController.StoreControlGroup(main.UnitViews, 2);

        // Select Group 1
        main.UnitViews.Selected.Clear();
        main.UnitViews.Selected.Add(unit1);
        main.UnitViews.UpdateSelectionVisuals();

        Vector2 screenPos = main.Camera.UnprojectPosition(new Vector3(30, 0, 30));

        // Start facing gesture for Group 1
        Require(main.PreviewController.BeginFacingGesture(main.Camera, screenPos, main.UnitViews, main.Session, out _), "BeginFacingGesture failed");
        main.PreviewController.UpdateFacingGesture(main.Camera, screenPos + new Vector2(40, 20), main.UnitViews);
        Require(main.PreviewController.IsFacingGestureActive, "Expected facing gesture to be active");

        // Recall Group 2 via keyboard input (Key2)
        using InputEventKey key2 = new() { Keycode = Key.Key2, Pressed = true, Echo = false };
        main._UnhandledInput(key2);

        // Gesture must be cancelled immediately
        Require(!main.PreviewController.IsFacingGestureActive, "Facing gesture must cancel when control group is switched");
        Require(!main.DestinationMarker.IsFacingArrowVisible, "Facing arrow must hide when control group is switched");
        Require(main.UnitViews.Selected.Count == 1 && main.UnitViews.Selected.Contains(unit2), "Group 2 should now be selected");
    }

    private static void CheckFacingGesture_UnitDeletionCancelsWithoutMouseMotion(Main main)
    {
        if (main.Session.Replica.OwnUnits.Length == 0) return;
        EntityId unit1 = main.Session.Replica.OwnUnits[0].Id;

        main.UnitViews.Selected.Clear();
        main.UnitViews.Selected.Add(unit1);
        main.UnitViews.UpdateSelectionVisuals();

        Vector2 screenPos = main.Camera.UnprojectPosition(new Vector3(30, 0, 30));
        Vector2 viewport = main.GetViewport().GetVisibleRect().Size;

        // Start facing gesture and drag to reveal facing arrow
        Require(main.PreviewController.BeginFacingGesture(main.Camera, screenPos, main.UnitViews, main.Session, out _), "BeginFacingGesture failed");
        Vector2 dragPos = screenPos + new Vector2(40, 20);
        main.PreviewController.UpdateFacingGesture(main.Camera, dragPos, main.UnitViews);
        Require(main.PreviewController.IsFacingGestureActive, "Expected facing gesture to be active");
        Require(main.DestinationMarker.IsFacingArrowVisible, "Expected facing arrow to be visible during drag");

        // Simulate unit deletion or selection clear without any mouse motion event
        main.UnitViews.Selected.Clear();

        // The per-frame UpdateHover (invoked every frame in _Process) runs with the stationary mouse position
        main.PreviewController.UpdateHover(
            main.Camera,
            dragPos,
            viewport,
            isPointerOccluded: false,
            hasEnemyContactUnderMouse: false,
            main.UnitViews,
            main.Session.Replica);

        // Gesture must be cancelled immediately without requiring mouse movement
        Require(!main.PreviewController.IsFacingGestureActive, "Facing gesture must cancel when units are removed or deselected even without mouse motion");
        Require(!main.DestinationMarker.IsFacingArrowVisible, "Facing arrow must hide when gesture is cancelled without mouse motion");

        // Also verify multi-unit group: deleting one unit cancels gesture without mouse motion
        if (main.Session.Replica.OwnUnits.Length >= 2)
        {
            EntityId unit2 = main.Session.Replica.OwnUnits[1].Id;
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(unit1);
            main.UnitViews.Selected.Add(unit2);
            main.UnitViews.UpdateSelectionVisuals();

            Require(main.PreviewController.BeginFacingGesture(main.Camera, screenPos, main.UnitViews, main.Session, out _), "Multi-unit BeginFacingGesture failed");
            main.PreviewController.UpdateFacingGesture(main.Camera, dragPos, main.UnitViews);
            Require(main.PreviewController.IsFacingGestureActive, "Expected multi-unit facing gesture to be active");
            Require(main.DestinationMarker.IsFacingArrowVisible, "Expected facing arrow to be visible");

            // Unit 2 dies / is removed from selection
            main.UnitViews.Selected.Remove(unit2);

            main.PreviewController.UpdateHover(
                main.Camera,
                dragPos,
                viewport,
                isPointerOccluded: false,
                hasEnemyContactUnderMouse: false,
                main.UnitViews,
                main.Session.Replica);

            Require(!main.PreviewController.IsFacingGestureActive, "Facing gesture must cancel when a unit is removed from multi-selection without mouse motion");
            Require(!main.DestinationMarker.IsFacingArrowVisible, "Facing arrow must hide after multi-unit deselection without mouse motion");
        }

        main.UnitViews.Selected.Clear();
        main.UnitViews.UpdateSelectionVisuals();
    }

    private static void CheckFacingGesture_CameraRotationInvariance(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);

            TestGameSession session = new() { Replica = replica };

            Vector3 worldTarget = new(30, 0, 30);
            // World direction pointing East-South: delta = (15, 0, 15), angle = Atan2(-15, -15) = -2.35619 rad (-135 deg)
            Vector3 worldDragPoint = new(45, 0, 45);
            float expectedHeading = MathF.Atan2(-15f, -15f);

            // Test 1: Overhead orthogonal camera facing North
            Camera3D cam1 = CreateTestCamera(parent, worldTarget, 80f);
            Vector2 screenStart1 = cam1.UnprojectPosition(worldTarget);
            Vector2 screenEnd1 = cam1.UnprojectPosition(worldDragPoint);

            Require(controller.BeginFacingGesture(cam1, screenStart1, unitViews, session, out _), "Cam1 begin failed");
            controller.UpdateFacingGesture(cam1, screenEnd1);
            Require(controller.IsFacingDetermined, "Cam1 facing determined failed");
            Require(MathF.Abs(CombatSystem.ShorterArcDelta(controller.GestureFacing, expectedHeading)) < 0.02f,
                $"Cam1 heading mismatch: expected {expectedHeading}, got {controller.GestureFacing}");
            controller.CancelFacingGesture();
            cam1.QueueFree();

            // Test 2: Camera rotated by 45 degrees yaw around target
            Camera3D cam2 = new()
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 80f,
                Position = new Vector3(worldTarget.X + 60f, worldTarget.Y + 115f, worldTarget.Z + 60f)
            };
            parent.AddChild(cam2);
            cam2.LookAt(worldTarget, Vector3.Up);

            Vector2 screenStart2 = cam2.UnprojectPosition(worldTarget);
            Vector2 screenEnd2 = cam2.UnprojectPosition(worldDragPoint);

            Require(controller.BeginFacingGesture(cam2, screenStart2, unitViews, session, out _), "Cam2 begin failed");
            controller.UpdateFacingGesture(cam2, screenEnd2);
            Require(controller.IsFacingDetermined, "Cam2 facing determined failed");
            Require(MathF.Abs(CombatSystem.ShorterArcDelta(controller.GestureFacing, expectedHeading)) < 0.02f,
                $"Cam2 heading mismatch with rotated camera: expected {expectedHeading}, got {controller.GestureFacing}");
            controller.CancelFacingGesture();
            cam2.QueueFree();

            // Test 3: Perspective camera looking down at 45 degrees
            Camera3D cam3 = new()
            {
                Projection = Camera3D.ProjectionType.Perspective,
                Fov = 60f,
                Position = new Vector3(worldTarget.X, worldTarget.Y + 40f, worldTarget.Z + 40f)
            };
            parent.AddChild(cam3);
            cam3.LookAt(worldTarget, Vector3.Up);

            Vector2 screenStart3 = cam3.UnprojectPosition(worldTarget);
            Vector2 screenEnd3 = cam3.UnprojectPosition(worldDragPoint);

            Require(controller.BeginFacingGesture(cam3, screenStart3, unitViews, session, out _), "Cam3 begin failed");
            controller.UpdateFacingGesture(cam3, screenEnd3);
            Require(controller.IsFacingDetermined, "Cam3 facing determined failed");
            Require(MathF.Abs(CombatSystem.ShorterArcDelta(controller.GestureFacing, expectedHeading)) < 0.02f,
                $"Cam3 heading mismatch with perspective camera: expected {expectedHeading}, got {controller.GestureFacing}");
            controller.CancelFacingGesture();
            cam3.QueueFree();
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckFacingGesture_ZeroAllocationsDuringDrag(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(30, 0, 30), 80f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);

            TestGameSession session = new() { Replica = replica };
            Vector2 screenPos = camera.UnprojectPosition(new Vector3(30, 0, 30));

            Require(controller.BeginFacingGesture(camera, screenPos, unitViews, session, out _), "BeginFacingGesture failed");

            // Warmup
            Vector2 drag1 = screenPos + new Vector2(30, 10);
            Vector2 drag2 = screenPos + new Vector2(40, 20);
            for (int i = 0; i < 10; i++)
            {
                controller.UpdateFacingGesture(camera, (i % 2 == 0) ? drag1 : drag2);
            }

            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                controller.UpdateFacingGesture(camera, (i % 2 == 0) ? drag1 : drag2);
            }
            long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
            long delta = afterAlloc - beforeAlloc;

            Require(delta == 0, $"UpdateFacingGesture must allocate zero bytes per frame, allocated: {delta} bytes.");
            controller.CancelFacingGesture();
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckReverseMove_PreviewAndDispatch(Main main)
    {
        Node3D parent = new();
        main.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            StaticGeometry geo = new(map, []);

            unitViews = new UnitViewSet(parent, map, catalog);
            Camera3D camera = CreateTestCamera(parent, new Vector3(30, 0, 30), 80f);

            OrderCursorOverlay overlay = new();
            parent.AddChild(overlay);
            OrderDestinationView destinationView = new();
            parent.AddChild(destinationView);

            OrderPreviewController controller = new(geo, map, catalog, overlay, destinationView);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            ApplyWithContacts(replica, bytes, 1, 1, [u1], []);
            unitViews.Sync(replica, 1.0);
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);

            TestGameSession session = new() { Replica = replica };
            Vector2 screenPos = camera.UnprojectPosition(new Vector3(20, 0, 35));

            Require(controller.BeginFacingGesture(camera, screenPos, unitViews, session, out _, MoveMode.Reverse), "Reverse BeginFacingGesture should succeed for unit");
            Require(controller.GestureMode == MoveMode.Reverse, "Gesture mode should be Reverse");

            Require(controller.EndFacingGesture(camera, screenPos, unitViews, session, out _), "EndFacingGesture should dispatch reverse move");
            Require(session.SubmittedMoves.Count == 1, "One move should be submitted");
            Require(session.SubmittedMoves[0].Mode == MoveMode.Reverse, "Submitted move must have Mode == MoveMode.Reverse");
            Require(session.SubmittedMoves[0].Unit == new EntityId(101), "Submitted move unit id mismatch");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckReverseMove_ThroughMainUnhandledInput(Main main)
    {
        if (main.Session.Replica.OwnUnits.Length == 0) return;

        OpsMovementCatalog movementCatalog = new(main.Catalog);
        OwnUnitObservation? targetUnit = null;
        foreach (OwnUnitObservation u in main.Session.Replica.OwnUnits)
        {
            if (movementCatalog.TryGetProfile(u.Definition, out UnitMovementProfile p) && p.ReverseSpeed > 0f)
            {
                targetUnit = u;
                break;
            }
        }
        if (targetUnit is null) return;

        EntityId unitId = targetUnit.Value.Id;
        main.UnitViews.Selected.Clear();
        main.UnitViews.Selected.Add(unitId);
        main.UnitViews.UpdateSelectionVisuals();

        Vector3 unitPos3D = main.UnitViews.Views.TryGetValue(unitId, out UnitView? view)
            ? view.Root.Position
            : new Vector3(targetUnit.Value.Position.X, 0f, targetUnit.Value.Position.Y);
        Vector2 screenPos = main.Camera.UnprojectPosition(unitPos3D + new Vector3(2f, 0f, 2f));

        // 1. Press Key.R into input singleton
        using InputEventKey rDown = new() { Keycode = Key.R, Pressed = true, Echo = false };
        Input.ParseInputEvent(rDown);
        Input.FlushBufferedEvents();

        try
        {
            // 2. Dispatch RMB press through Main._UnhandledInput
            using InputEventMouseButton rmbDown = new()
            {
                ButtonIndex = MouseButton.Right,
                Pressed = true,
                Position = screenPos
            };
            main._UnhandledInput(rmbDown);

            Require(main.PreviewController.IsFacingGestureActive, "Facing gesture must be active after R + RMB");
            Require(main.PreviewController.GestureMode == MoveMode.Reverse, "GestureMode must be Reverse when R is pressed");

            // 3. Dispatch RMB release through Main._UnhandledInput
            using InputEventMouseButton rmbUp = new()
            {
                ButtonIndex = MouseButton.Right,
                Pressed = false,
                Position = screenPos
            };
            main._UnhandledInput(rmbUp);

            Require(!main.PreviewController.IsFacingGestureActive, "Facing gesture must end after RMB release");
        }
        finally
        {
            using InputEventKey rUp = new() { Keycode = Key.R, Pressed = false, Echo = false };
            Input.ParseInputEvent(rUp);
            Input.FlushBufferedEvents();
        }
    }

    private static Camera3D CreateTestCamera(Node3D parent, Vector3 focus, float size)
    {
        Camera3D camera = new()
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = size,
            Far = 3500f,
            Position = new Vector3(focus.X, focus.Y + 115f, focus.Z + 80f)
        };
        parent.AddChild(camera);
        camera.LookAt(focus, Vector3.Up);
        return camera;
    }

    private static void ApplyWithContacts(
        ClientReplica replica,
        byte[] bytes,
        uint sequence,
        int tick,
        OwnUnitObservation[] units,
        ContactObservation[] contacts)
    {
        int size = WireProtocol.WriteSnapshot(bytes, sequence, tick, units, contacts);
        Require(replica.TryApply(bytes.AsSpan(0, size)), "Snapshot apply failed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TestGameSession : IGameSession
    {
        public ClientReplica Replica { get; set; } = new();
        public MissionObservation Mission { get; set; } = new();
        public double RenderTick => 1.0;
        public int Tick => 1;
        public double LastTickMilliseconds => 0.1;
        public long LastTickAllocatedBytes => 0;
        public long BytesReceived => 0;
        public double PendingSeconds => 0;
        public CommandResult? LastCommandResult { get; set; }
        public uint LastSubmittedCommandSequence { get; set; }
        public long RejectedCommands => 0;
        public bool IsConnected => true;
        public int AssignedPlayerId => 1;
        public string StatusDescription => "TestSession";

        public List<(EntityId Unit, NumericsVector2 Target, uint Sequence, float? FinalFacing, MoveMode Mode)> SubmittedMoves { get; } = new();

        public bool Move(EntityId unit, NumericsVector2 target) => Move(unit, target, null, MoveMode.Forward);

        public bool Move(EntityId unit, NumericsVector2 target, float? finalFacing = null) => Move(unit, target, finalFacing, MoveMode.Forward);

        public bool Move(EntityId unit, NumericsVector2 target, float? finalFacing = null, MoveMode mode = MoveMode.Forward)
        {
            LastSubmittedCommandSequence++;
            SubmittedMoves.Add((unit, target, LastSubmittedCommandSequence, finalFacing, mode));
            return true;
        }

        public bool Attack(EntityId unit, ContactId target) => true;
        public bool SetFireStance(EntityId unit, FireStance stance) => true;
        public void Advance(double elapsedSeconds) { }
        public void Dispose() { }
    }
}
