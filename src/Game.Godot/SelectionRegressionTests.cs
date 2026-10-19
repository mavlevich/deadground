using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

internal static class SelectionRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckEngineProjectionMatchesCameraUnproject(scene);
        CheckBoxSelectionAndContacts(scene);
        CheckRectangleMathViewportsAndDirections(scene);
        CheckDragThresholdAndCancellation(scene);
        CheckOrderNotificationClickThrough(scene);
        CheckControlGroupsAndSyncCleanup(scene);
        CheckClassifyGroupKeyModifiers();
        CheckDoubleTapCameraCentering(scene);
        CheckDoubleClickSameTypeOnScreen(scene);
        CheckBadgeLowestGroupAndSelectionCardDisplay(scene);
        CheckNavigationLargeAltTargets();
        CheckControlsHelpLineFormatting();
        CheckHudZeroAllocationsWhenUnchanged(scene);
        CheckRangeRingsAndZeroAllocations(scene);
    }

    private static void CheckEngineProjectionMatchesCameraUnproject(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);
            SelectionController controller = new();

            Vector3[] worldPoints =
            [
                new Vector3(30f, 0f, 35f),
                new Vector3(50f, 1f, 50f),
                new Vector3(68f, 2f, 42f),
                new Vector3(42f, 0.5f, 64f)
            ];

            foreach (Vector3 worldPoint in worldPoints)
            {
                Require(!camera.IsPositionBehind(worldPoint), $"Test world point {worldPoint} should be in front of camera.");
                Require(controller.TryProjectWorldPoint(camera, worldPoint, out Vector2 actual),
                    $"SelectionController.TryProjectWorldPoint failed for {worldPoint}.");
                Vector2 expected = camera.UnprojectPosition(worldPoint);
                Require(actual.DistanceTo(expected) < 0.001f,
                    $"Controller projection {actual} did not match camera.UnprojectPosition {expected} for {worldPoint}.");
            }
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckBoxSelectionAndContacts(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            unitViews = new UnitViewSet(parent, map, catalog);
            contactViews = new ContactViewSet(parent, map);

            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);
            Vector2 viewport = scene.GetViewport().GetVisibleRect().Size;

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation u1 = new(new EntityId(1), 0, new NumericsVector2(40, 45), 0);
            OwnUnitObservation u2 = new(new EntityId(2), 1, new NumericsVector2(55, 50), 0);
            OwnUnitObservation u3 = new(new EntityId(3), 0, new NumericsVector2(90, 90), 0);
            ContactObservation c1 = new(new ContactId(501), ContactState.Current,
                ContactClassification.Tank, 0, new NumericsVector2(48, 48), 5);

            ApplyWithContacts(replica, bytes, 1, 5, [u1, u2, u3], [c1]);
            unitViews.Sync(replica, 5);
            contactViews.Sync(replica, 5);

            SelectionController controller = new();

            Require(controller.TryProjectWorldPoint(camera, unitViews.Views[u1.Id].Root.Position + Vector3.Up, out Vector2 p1),
                "Failed to project unit 1.");
            Require(controller.TryProjectWorldPoint(camera, unitViews.Views[u2.Id].Root.Position + Vector3.Up, out Vector2 p2),
                "Failed to project unit 2.");
            Require(controller.TryProjectWorldPoint(camera, unitViews.Views[u3.Id].Root.Position + Vector3.Up, out Vector2 p3),
                "Failed to project unit 3.");
            Require(controller.TryProjectWorldPoint(camera, contactViews.Views[c1.Id].Root.Position + Vector3.Up * 1.5f, out Vector2 pc),
                "Failed to project contact.");

            // Pre-select contact and unit 3 to verify box selection clears contact selection and replaces unit selection
            contactViews.Selected.Add(c1.Id);
            unitViews.Selected.Add(u3.Id);

            Vector2 boxMin = new(MathF.Min(p1.X, p2.X) - 12f, MathF.Min(p1.Y, p2.Y) - 12f);
            Vector2 boxMax = new(MathF.Max(p1.X, p2.X) + 12f, MathF.Max(p1.Y, p2.Y) + 12f);

            // Ensure contact is inside the box and u3 is outside the box
            Rect2 rect = SelectionController.MakeScreenRect(boxMin, boxMax);
            Require(SelectionController.ContainsPointInclusive(rect, pc), "Contact projected center should lie inside the test box.");
            Require(!SelectionController.ContainsPointInclusive(rect, p3), "Unit 3 should lie outside the test box.");

            Require(controller.BeginPress(boxMin, shiftPressed: false), "Box press should succeed.");
            Require(controller.UpdateDrag(boxMax), "Drag beyond 6 px should enter drag state.");
            SelectionReleaseKind kind = controller.CompleteRelease(
                camera, unitViews, contactViews, null, boxMax, viewport, shiftPressed: false, out _, out _);

            Require(kind == SelectionReleaseKind.BoxSelect, "Release after drag must be BoxSelect.");
            Require(unitViews.Selected.Count == 2 && unitViews.Selected.Contains(u1.Id) && unitViews.Selected.Contains(u2.Id),
                "Units inside box must be selected and unit outside must not.");
            Require(!unitViews.Selected.Contains(u3.Id), "Previous unit outside box must be deselected when Shift is not held.");
            Require(contactViews.Selected.Count == 0, "Contacts must never be box selected and contact selection must be cleared.");

            // Shift + box selection adds u3 without dropping u1 and u2
            Vector2 u3BoxStart = p3 - new Vector2(15f, 15f);
            Vector2 u3BoxEnd = p3 + new Vector2(15f, 15f);
            Require(controller.BeginPress(u3BoxStart, shiftPressed: true), "Shift box press should succeed.");
            Require(controller.UpdateDrag(u3BoxEnd), "Shift box drag should enter drag state.");
            controller.CompleteRelease(camera, unitViews, contactViews, null, u3BoxEnd, viewport, shiftPressed: true, out _, out _);

            Require(unitViews.Selected.Count == 3 &&
                    unitViews.Selected.Contains(u1.Id) &&
                    unitViews.Selected.Contains(u2.Id) &&
                    unitViews.Selected.Contains(u3.Id),
                "Shift box selection must add units to existing selection.");
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckRectangleMathViewportsAndDirections(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            unitViews = new UnitViewSet(parent, map);
            contactViews = new ContactViewSet(parent, map);

            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation leftUnit = new(new EntityId(10), 0, new NumericsVector2(30, 50), 0);
            OwnUnitObservation rightUnit = new(new EntityId(11), 0, new NumericsVector2(70, 50), 0);
            ApplyWithContacts(replica, bytes, 1, 2, [leftUnit, rightUnit], []);
            unitViews.Sync(replica, 2);

            Vector2[] viewports = [new Vector2(1280, 800), new Vector2(1920, 1080)];

            foreach (Vector2 viewport in viewports)
            {
                SelectionController controller = new((Vector3 worldPoint, out Vector2 screenPoint) =>
                    TryProjectOrthoForViewport(camera, worldPoint, viewport, out screenPoint));

                Require(controller.TryProjectWorldPoint(camera, unitViews.Views[leftUnit.Id].Root.Position + Vector3.Up, out Vector2 pLeft),
                    $"Projection failed for left unit at {viewport}.");
                Require(controller.TryProjectWorldPoint(camera, unitViews.Views[rightUnit.Id].Root.Position + Vector3.Up, out Vector2 pRight),
                    $"Projection failed for right unit at {viewport}.");

                Require(pLeft.X < viewport.X * 0.5f && pRight.X > viewport.X * 0.5f,
                    $"Projected X positions did not scale with viewport {viewport}.");

                Vector2 minCorner = new(MathF.Min(pLeft.X, pRight.X) - 10f, MathF.Min(pLeft.Y, pRight.Y) - 10f);
                Vector2 maxCorner = new(MathF.Max(pLeft.X, pRight.X) + 10f, MathF.Max(pLeft.Y, pRight.Y) + 10f);

                // Test all 4 drag directions: TL->BR, BR->TL (start below/right of end), TR->BL, BL->TR
                (Vector2 Start, Vector2 End)[] directions =
                [
                    (minCorner, maxCorner),
                    (maxCorner, minCorner),
                    (new Vector2(maxCorner.X, minCorner.Y), new Vector2(minCorner.X, maxCorner.Y)),
                    (new Vector2(minCorner.X, maxCorner.Y), new Vector2(maxCorner.X, minCorner.Y))
                ];

                foreach ((Vector2 start, Vector2 end) in directions)
                {
                    Rect2 built = SelectionController.MakeScreenRect(start, end);
                    Require(MathF.Abs(built.Position.X - minCorner.X) < 0.01f &&
                            MathF.Abs(built.Position.Y - minCorner.Y) < 0.01f &&
                            MathF.Abs(built.Size.X - (maxCorner.X - minCorner.X)) < 0.01f &&
                            MathF.Abs(built.Size.Y - (maxCorner.Y - minCorner.Y)) < 0.01f,
                        $"Rectangle math failed for direction {start} -> {end} at {viewport}.");

                    IReadOnlyList<EntityId> hits = controller.CollectUnitsInBox(camera, unitViews, start, end);
                    Require(hits.Count == 2,
                        $"Box dragged from {start} to {end} at {viewport} should select 2 units, got {hits.Count}.");
                }
            }
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckDragThresholdAndCancellation(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            unitViews = new UnitViewSet(parent, map);
            contactViews = new ContactViewSet(parent, map);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);
            Vector2 viewport = scene.GetViewport().GetVisibleRect().Size;

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(21), 0, new NumericsVector2(50, 50), 0);
            ApplyWithContacts(replica, bytes, 1, 2, [u1], []);
            unitViews.Sync(replica, 2);

            SelectionController controller = new();
            Require(controller.TryProjectWorldPoint(camera, unitViews.Views[u1.Id].Root.Position + Vector3.Up, out Vector2 u1Screen),
                "Failed to project u1.");

            // 1. Small movement under 6 px threshold is a click, not a box
            Vector2 smallMove = u1Screen + new Vector2(3.5f, 4.0f); // distance = 5.315 <= 6.0
            Require(!SelectionController.IsDragDistance(u1Screen, smallMove), "5.315 px movement must be under 6 px threshold.");
            Require(controller.BeginPress(u1Screen), "Press should start.");
            Require(!controller.UpdateDrag(smallMove), "Movement under threshold must not trigger drag.");
            SelectionReleaseKind clickKind = controller.CompleteRelease(
                camera, unitViews, contactViews, null, smallMove, viewport, shiftPressed: false, out EntityId? clickedUnit, out _);
            Require(clickKind == SelectionReleaseKind.ClickSelect, "Release under drag threshold must be ClickSelect.");
            Require(clickedUnit == u1.Id && unitViews.Selected.Contains(u1.Id), "Sub-threshold click must select the unit under cursor.");

            // 2. Drag starting on a HUD panel must not start a box
            Rect2 hudPanel = new(24, 24, 300, 120);
            Require(!controller.BeginPress(new Vector2(100, 60), shiftPressed: false, doubleClick: false, [hudPanel]),
                "Press on a HUD panel must be rejected.");
            Require(!controller.IsPointerDown && !controller.IsDragging, "Pointer must not be down after HUD panel press.");

            // 3. Releasing outside the window cancels drag cleanly without changing selection
            Vector2 dragStart = new(100, 100);
            Require(controller.BeginPress(dragStart), "Drag start should succeed.");
            Require(controller.UpdateDrag(new Vector2(300, 300)), "Drag should activate.");
            SelectionReleaseKind outsideKind = controller.CompleteRelease(
                camera, unitViews, contactViews, null, new Vector2(-10, 300), viewport, shiftPressed: false, out _, out _);
            Require(outsideKind == SelectionReleaseKind.Cancelled, "Releasing outside viewport must return Cancelled.");
            Require(unitViews.Selected.Contains(u1.Id), "Cancelled drag must preserve prior selection.");

            // 4. Losing focus cancels drag cleanly
            Require(controller.BeginPress(dragStart), "Drag start should succeed.");
            Require(controller.UpdateDrag(new Vector2(250, 250)), "Drag should activate.");
            controller.CancelDrag();
            Require(!controller.IsPointerDown && !controller.IsDragging, "CancelDrag must clear pointer and drag state.");
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckOrderNotificationClickThrough(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            unitViews = new UnitViewSet(parent, map);
            contactViews = new ContactViewSet(parent, map);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);
            Vector2 viewport = scene.GetViewport().GetVisibleRect().Size;

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(55), 0, new NumericsVector2(50, 50), 0);
            ApplyWithContacts(replica, bytes, 1, 2, [u1], []);
            unitViews.Sync(replica, 2);

            SelectionController controller = new();
            Require(controller.TryProjectWorldPoint(camera, unitViews.Views[u1.Id].Root.Position + Vector3.Up, out Vector2 u1Screen),
                "Failed to project unit under notification.");

            OrderNotification notification = new();
            parent.AddChild(notification);
            notification.Position = u1Screen - new Vector2(180f, 18f);
            notification.Size = new Vector2(360f, 36f);
            notification.ShowNotification("Move order submitted / 1 units / 50, 50 m");

            Require(notification.Visible, "OrderNotification must be visible after ShowNotification.");
            Require(notification.MouseFilter == Control.MouseFilterEnum.Ignore,
                "OrderNotification must use MouseFilter.Ignore so it does not swallow clicks.");
            Rect2 toastRect = notification.GetGlobalRect();
            Require(toastRect.HasPoint(u1Screen), "Unit screen point must lie inside the visible OrderNotification rect.");

            // Verify with Main's real panels and input pipeline
            Require(scene is Main, "CheckOrderNotificationClickThrough requires Main scene.");
            Main main = (Main)scene;
            Require(main.OrderNotification is not null, "Main.OrderNotification must be initialized before regression tests.");
            OrderNotification mainToast = main.OrderNotification!;

            mainToast.ShowNotification("Move order submitted");
            main.RefreshOccludingPanels();
            ReadOnlySpan<Rect2> inputPanels = main.InputOccludingPanels;
            ReadOnlySpan<Rect2> badgePanels = main.BadgeOccludingPanels;
            Rect2 mainToastRect = mainToast.GetGlobalRect();
            Require(mainToastRect.Size.X > 0f && mainToastRect.Size.Y > 0f, "Main OrderNotification must have non-zero size.");

            // 1. Input occluding panels MUST NOT contain the toast
            for (int i = 0; i < inputPanels.Length; i++)
            {
                Require(inputPanels[i] != mainToastRect,
                    "Main.InputOccludingPanels must not contain OrderNotification rect.");
            }

            // 2. Badge occluding panels MUST contain the toast so badges hide under it
            bool badgeHasToast = false;
            for (int i = 0; i < badgePanels.Length; i++)
            {
                if (badgePanels[i] == mainToastRect)
                {
                    badgeHasToast = true;
                    break;
                }
            }
            Require(badgeHasToast, "Main.BadgeOccludingPanels must contain OrderNotification rect.");

            // 3. Point occlusion check
            Vector2 toastCenter = mainToastRect.GetCenter();
            Require(!SelectionController.IsPointOccluded(toastCenter, inputPanels),
                "Toast center point must not be occluded in InputOccludingPanels.");

            // 4. Input dispatch through Main._UnhandledInput and Main._Input
            main.CancelActiveDrag();
            InputEventMouseButton pressEvent = new()
            {
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                Position = toastCenter
            };
            main._UnhandledInput(pressEvent);
            Require(main.SelectionController.IsPointerDown,
                "Left click over OrderNotification must not be blocked by input panels.");

            InputEventMouseButton releaseEvent = new()
            {
                ButtonIndex = MouseButton.Left,
                Pressed = false,
                Position = toastCenter
            };
            main._Input(releaseEvent);
            Require(!main.SelectionController.IsPointerDown, "Pointer must be released.");
            main.CancelActiveDrag();

            // 5. Controller press on simulated unit inside toast rect
            Require(controller.BeginPress(u1Screen, shiftPressed: false, doubleClick: false, inputPanels),
                "BeginPress inside visible OrderNotification rect must return true.");
            SelectionReleaseKind kind = controller.CompleteRelease(
                camera, unitViews, contactViews, null, u1Screen, viewport, shiftPressed: false, out EntityId? pickedUnit, out _);
            Require(kind == SelectionReleaseKind.ClickSelect && pickedUnit == u1.Id && unitViews.Selected.Contains(u1.Id),
                "Clicking inside visible OrderNotification rect must select the unit under it.");
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckHudZeroAllocationsWhenUnchanged(Node3D scene)
    {
        Require(scene is Main, "CheckHudZeroAllocationsWhenUnchanged requires Main scene.");
        Main main = (Main)scene;

        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        ushort tankDef = catalog.RequireDefinitionIndex("prototype_tank");
        ushort reconDef = catalog.RequireDefinitionIndex("recon_vehicle");

        OwnUnitObservation u1 = new(new EntityId(901), tankDef, new NumericsVector2(50, 50), 0, Health: 120, Ammunition: 10);
        OwnUnitObservation u2 = new(new EntityId(902), reconDef, new NumericsVector2(52, 50), 0, Health: 60, Ammunition: 5);

        SelectionCard card = new(catalog);
        scene.AddChild(card);
        try
        {
            // 1. SelectionCard isolated zero-allocation test
            for (int i = 0; i < 5; i++)
            {
                card.UpdateSingleOwnUnit(u1, controlGroup: 1);
            }
            long beforeSingle = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
            {
                card.UpdateSingleOwnUnit(u1, controlGroup: 1);
            }
            long allocSingle = GC.GetAllocatedBytesForCurrentThread() - beforeSingle;
            Require(allocSingle == 0,
                $"SelectionCard.UpdateSingleOwnUnit allocated {allocSingle} bytes on unchanged data (expected 0).");

            for (int i = 0; i < 5; i++)
            {
                card.UpdateMultipleOwnUnits(totalCount: 2, tankCount: 1, reconCount: 1, otherCount: 0, controlGroup: 1);
            }
            long beforeMulti = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
            {
                card.UpdateMultipleOwnUnits(totalCount: 2, tankCount: 1, reconCount: 1, otherCount: 0, controlGroup: 1);
            }
            long allocMulti = GC.GetAllocatedBytesForCurrentThread() - beforeMulti;
            Require(allocMulti == 0,
                $"SelectionCard.UpdateMultipleOwnUnits allocated {allocMulti} bytes on unchanged data (expected 0).");

            for (int i = 0; i < 5; i++)
            {
                card.UpdateEmpty();
            }
            long beforeEmpty = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
            {
                card.UpdateEmpty();
            }
            long allocEmpty = GC.GetAllocatedBytesForCurrentThread() - beforeEmpty;
            Require(allocEmpty == 0,
                $"SelectionCard.UpdateEmpty allocated {allocEmpty} bytes on unchanged data (expected 0).");

            // 2. Main.UpdateSelectionPresentation zero-allocation test
            if (main.Session.Replica.OwnUnits.Length > 0)
            {
                main.UnitViews.Selected.Clear();
                EntityId firstId = main.Session.Replica.OwnUnits[0].Id;
                main.UnitViews.Selected.Add(firstId);
                main.SelectionController.StoreControlGroup(main.UnitViews, 1);

                for (int i = 0; i < 5; i++)
                {
                    main.UpdateSelectionPresentation();
                }

                long beforeMain = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 50; i++)
                {
                    main.UpdateSelectionPresentation();
                }
                long allocMain = GC.GetAllocatedBytesForCurrentThread() - beforeMain;
                Require(allocMain == 0,
                    $"Main.UpdateSelectionPresentation allocated {allocMain} bytes on unchanged selection and group (expected 0).");
            }
        }
        finally
        {
            card.QueueFree();
        }
    }

    private static void CheckControlGroupsAndSyncCleanup(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            unitViews = new UnitViewSet(parent, map);
            contactViews = new ContactViewSet(parent, map);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(101), 0, new NumericsVector2(20, 20), 0);
            OwnUnitObservation u2 = new(new EntityId(102), 0, new NumericsVector2(30, 20), 0);
            OwnUnitObservation u3 = new(new EntityId(103), 0, new NumericsVector2(40, 20), 0);
            ApplyWithContacts(replica, bytes, 1, 2, [u1, u2, u3], []);
            unitViews.Sync(replica, 2);

            SelectionController controller = new();

            // Store {u1} in group 1
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);
            controller.StoreControlGroup(unitViews, 1);
            Require(unitViews.GetControlGroup(1).Count == 1 && unitViews.GetControlGroup(1).Contains(u1.Id),
                "Group 1 should contain u1 after StoreControlGroup.");

            // Shift+1 adds {u2} to group 1
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u2.Id);
            controller.AddToControlGroup(unitViews, 1);
            Require(unitViews.GetControlGroup(1).Count == 2 &&
                    unitViews.GetControlGroup(1).Contains(u1.Id) &&
                    unitViews.GetControlGroup(1).Contains(u2.Id),
                "Group 1 should contain u1 and u2 after AddToControlGroup.");

            // Store {u3} in group 2
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u3.Id);
            controller.StoreControlGroup(unitViews, 2);

            // Recall of empty group 5 does nothing (keeps u3 selected)
            Require(!controller.RecallControlGroup(unitViews, contactViews, null, 5, 1.0, out _),
                "Recalling empty group 5 must return false.");
            Require(unitViews.Selected.Count == 1 && unitViews.Selected.Contains(u3.Id),
                "Recalling empty group must not alter current selection.");

            // Recall group 1 replaces selection with {u1, u2}
            Require(controller.RecallControlGroup(unitViews, contactViews, null, 1, 2.0, out _),
                "Recalling group 1 must succeed.");
            Require(unitViews.Selected.Count == 2 &&
                    unitViews.Selected.Contains(u1.Id) &&
                    unitViews.Selected.Contains(u2.Id),
                "Recalling group 1 must replace selection with u1 and u2.");

            // Replace group 1 with {u3}
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u3.Id);
            controller.StoreControlGroup(unitViews, 1);
            Require(unitViews.GetControlGroup(1).Count == 1 && unitViews.GetControlGroup(1).Contains(u3.Id),
                "StoreControlGroup must replace previous group members.");

            // Put u2 in group 3 as well, then remove u3 from snapshot and verify Sync removes u3 from Selected and all groups
            unitViews.Selected.Add(u2.Id);
            controller.AddToControlGroup(unitViews, 3);
            ApplyWithContacts(replica, bytes, 2, 4, [u1, u2], []);
            unitViews.Sync(replica, 4);

            Require(!unitViews.Selected.Contains(u3.Id), "Removed unit u3 must disappear from Selected after Sync.");
            for (int g = 1; g <= SelectionController.ControlGroupCount; g++)
            {
                Require(!unitViews.GetControlGroup(g).Contains(u3.Id),
                    $"Removed unit u3 must disappear from control group {g} after Sync.");
            }
            Require(unitViews.GetControlGroup(3).Contains(u2.Id), "Remaining unit u2 must stay in group 3.");
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckClassifyGroupKeyModifiers()
    {
        // Ctrl+2 stores
        Require(SelectionController.ClassifyGroupKey(Key.Key2, ctrl: true, meta: false, shift: false, alt: false, out int gCtrl) == GroupKeyAction.Store && gCtrl == 2,
            "Ctrl+2 must classify as Store for group 2.");

        // Cmd+2 stores
        Require(SelectionController.ClassifyGroupKey(Key.Key2, ctrl: false, meta: true, shift: false, alt: false, out int gCmd) == GroupKeyAction.Store && gCmd == 2,
            "Cmd+2 must classify as Store for group 2.");

        // Shift+2 adds
        Require(SelectionController.ClassifyGroupKey(Key.Key2, ctrl: false, meta: false, shift: true, alt: false, out int gShift) == GroupKeyAction.Add && gShift == 2,
            "Shift+2 must classify as Add for group 2.");

        // Plain 2 recalls
        Require(SelectionController.ClassifyGroupKey(Key.Key2, ctrl: false, meta: false, shift: false, alt: false, out int gRecall) == GroupKeyAction.Recall && gRecall == 2,
            "Plain 2 must classify as Recall for group 2.");

        // Alt+2 is not a group action
        Require(SelectionController.ClassifyGroupKey(Key.Key2, ctrl: false, meta: false, shift: false, alt: true, out _) == GroupKeyAction.None,
            "Alt+2 must not classify as a control group action.");

        // Ctrl+Shift+2 stores (store wins)
        Require(SelectionController.ClassifyGroupKey(Key.Key2, ctrl: true, meta: false, shift: true, alt: false, out int gCtrlShift) == GroupKeyAction.Store && gCtrlShift == 2,
            "Ctrl+Shift+2 must classify as Store (store wins).");
    }

    private static void CheckDoubleTapCameraCentering(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            unitViews = new UnitViewSet(parent, map);
            contactViews = new ContactViewSet(parent, map);

            Camera3D camera = new() { Projection = Camera3D.ProjectionType.Orthogonal, Size = 90f, Far = 2000f };
            parent.AddChild(camera);
            CameraRig rig = new(camera, new Vector3(64, 0, 64), 90f, map.Width, map.Depth);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(201), 0, new NumericsVector2(20, 30), 0);
            OwnUnitObservation u2 = new(new EntityId(202), 0, new NumericsVector2(40, 50), 0);
            ApplyWithContacts(replica, bytes, 1, 2, [u1, u2], []);
            unitViews.Sync(replica, 2);

            SelectionController controller = new();
            unitViews.Selected.Add(u1.Id);
            unitViews.Selected.Add(u2.Id);
            controller.StoreControlGroup(unitViews, 1);

            // First press at t = 10.0s selects group without moving camera
            Require(controller.RecallControlGroup(unitViews, contactViews, rig, 1, 10.0, out bool centered1),
                "First group recall should succeed.");
            Require(!centered1, "First recall must not center camera.");
            Require(MathF.Abs(rig.Focus.X - 64f) < 0.01f && MathF.Abs(rig.Focus.Z - 64f) < 0.01f,
                "Camera focus should remain at initial position after single press.");

            // Slower second press at t = 10.50s (0.50s > 0.35s) does NOT center camera
            Require(controller.RecallControlGroup(unitViews, contactViews, rig, 1, 10.50, out bool centeredSlow),
                "Slow second group recall should succeed.");
            Require(!centeredSlow, "Second press after 0.35s must not center camera.");
            Require(MathF.Abs(rig.Focus.X - 64f) < 0.01f && MathF.Abs(rig.Focus.Z - 64f) < 0.01f,
                "Camera focus must not move on slow second press.");

            // Fast second press at t = 10.70s (0.20s <= 0.35s) centers camera on group average (30, 40) without changing zoom
            Require(controller.RecallControlGroup(unitViews, contactViews, rig, 1, 10.70, out bool centeredFast),
                "Fast second group recall should succeed.");
            Require(centeredFast, "Double tap within 0.35s must center camera.");
            Require(MathF.Abs(rig.Focus.X - 30f) < 0.01f && MathF.Abs(rig.Focus.Z - 40f) < 0.01f,
                $"Camera focus should center on group average (30, 40), got ({rig.Focus.X}, {rig.Focus.Z}).");
            Require(MathF.Abs(rig.Size - 90f) < 0.01f, "Camera zoom size must not change when centering on group.");
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckDoubleClickSameTypeOnScreen(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            ushort tankDef = catalog.RequireDefinitionIndex("prototype_tank");
            ushort reconDef = catalog.RequireDefinitionIndex("recon_vehicle");

            unitViews = new UnitViewSet(parent, map, catalog);
            contactViews = new ContactViewSet(parent, map);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 30f);
            Vector2 viewport = scene.GetViewport().GetVisibleRect().Size;

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation tankOnScreen1 = new(new EntityId(301), tankDef, new NumericsVector2(45, 50), 0);
            OwnUnitObservation tankOnScreen2 = new(new EntityId(302), tankDef, new NumericsVector2(55, 50), 0);
            OwnUnitObservation tankOffScreen = new(new EntityId(303), tankDef, new NumericsVector2(120, 120), 0);
            OwnUnitObservation reconOnScreen = new(new EntityId(304), reconDef, new NumericsVector2(50, 48), 0);

            ApplyWithContacts(replica, bytes, 1, 2, [tankOnScreen1, tankOnScreen2, tankOffScreen, reconOnScreen], []);
            unitViews.Sync(replica, 2);

            Require(unitViews.Views[tankOnScreen1.Id].UnitId == "prototype_tank", "UnitView.UnitId must be prototype_tank.");
            Require(unitViews.Views[reconOnScreen.Id].UnitId == "recon_vehicle", "UnitView.UnitId must be recon_vehicle.");

            SelectionController controller = new();
            Require(controller.TryProjectWorldPoint(
                camera, unitViews.Views[tankOnScreen1.Id].Root.Position + Vector3.Up, out Vector2 tank1Screen),
                "Failed to project tankOnScreen1.");

            Require(controller.BeginPress(tank1Screen, shiftPressed: false, doubleClick: true), "Double-click press should start.");
            SelectionReleaseKind kind = controller.CompleteRelease(
                camera, unitViews, contactViews, null, tank1Screen, viewport, shiftPressed: false, out _, out _);

            Require(kind == SelectionReleaseKind.DoubleClickSelect, "Double click on own unit must return DoubleClickSelect.");
            Require(unitViews.Selected.Count == 2 &&
                    unitViews.Selected.Contains(tankOnScreen1.Id) &&
                    unitViews.Selected.Contains(tankOnScreen2.Id),
                "Double click must select all on-screen units with the same UnitId.");
            Require(!unitViews.Selected.Contains(tankOffScreen.Id),
                "Double click must not select off-screen units of the same UnitId.");
            Require(!unitViews.Selected.Contains(reconOnScreen.Id),
                "Double click must not select on-screen units of a different UnitId.");

            // Shift + double click on reconOnScreen adds recon units on screen
            Require(controller.TryProjectWorldPoint(
                camera, unitViews.Views[reconOnScreen.Id].Root.Position + Vector3.Up, out Vector2 reconScreen),
                "Failed to project reconOnScreen.");
            Require(controller.BeginPress(reconScreen, shiftPressed: true, doubleClick: true), "Shift double-click press should start.");
            controller.CompleteRelease(camera, unitViews, contactViews, null, reconScreen, viewport, shiftPressed: true, out _, out _);
            Require(unitViews.Selected.Count == 3 && unitViews.Selected.Contains(reconOnScreen.Id),
                "Shift + double click must add same-type units to current selection.");
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckBadgeLowestGroupAndSelectionCardDisplay(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        ContactViewSet? contactViews = null;
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            ushort tankDef = catalog.RequireDefinitionIndex("prototype_tank");
            ushort reconDef = catalog.RequireDefinitionIndex("recon_vehicle");

            unitViews = new UnitViewSet(parent, map, catalog);
            contactViews = new ContactViewSet(parent, map);
            Camera3D camera = CreateTestCamera(parent, new Vector3(50, 0, 50), 100f);

            RoleBadgeOverlay overlay = new(catalog);
            parent.AddChild(overlay);
            SelectionCard card = new(catalog);
            parent.AddChild(card);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation u1 = new(new EntityId(401), tankDef, new NumericsVector2(45, 50), 0, Health: 120);
            OwnUnitObservation u2 = new(new EntityId(402), reconDef, new NumericsVector2(55, 50), 0, Health: 60);
            ApplyWithContacts(replica, bytes, 1, 2, [u1, u2], []);
            unitViews.Sync(replica, 2);

            SelectionController controller = new();

            // Put u1 in group 5 and group 2 -> badge must show lowest group number (2)
            unitViews.Selected.Clear();
            unitViews.Selected.Add(u1.Id);
            controller.StoreControlGroup(unitViews, 5);
            controller.StoreControlGroup(unitViews, 2);

            overlay.UpdateBadges(camera, camera.Size, unitViews, contactViews, replica, 2, []);
            RoleBadge? badge1 = overlay.TryGetUnitBadge(u1.Id);
            RoleBadge? badge2 = overlay.TryGetUnitBadge(u2.Id);
            Require(badge1 is not null && badge2 is not null, "Unit badges were not created.");
            Require(badge1!.BoundControlGroup == 2 && badge1.LabelText == "TANK 2",
                $"Unit in groups 2 and 5 must show lowest group number 'TANK 2', got '{badge1.LabelText}'.");
            Require(badge2!.BoundControlGroup == 0 && badge2.LabelText == "RECON",
                $"Ungrouped unit badge must show 'RECON', got '{badge2.LabelText}'.");

            // Selection equals group 2 -> SelectionCard shows Group 2
            int activeGroup = unitViews.GetSelectedControlGroup();
            Require(activeGroup == 2, $"Expected active group 2, got {activeGroup}.");
            card.UpdateSingleOwnUnit(u1, activeGroup);
            Require(card.TitleText.Contains("Group 2"), $"Single-unit card should display 'Group 2', got '{card.TitleText}'.");
            Require(card.Line3Text.Contains("Fire at will"), $"Single-unit card should display 'Fire at will', got '{card.Line3Text}'.");

            // Store {u1, u2} in group 4 and check multi-unit card
            unitViews.Selected.Add(u2.Id);
            controller.StoreControlGroup(unitViews, 4);
            int multiGroup = unitViews.GetSelectedControlGroup();
            Require(multiGroup == 4, $"Expected active group 4 for multi-selection, got {multiGroup}.");
            card.UpdateMultipleOwnUnits(2, 1, 1, 0, multiGroup);
            Require(card.TitleText.Contains("2 Units Selected") && card.TitleText.Contains("Group 4"),
                $"Multi-unit card should show count and 'Group 4', got '{card.TitleText}'.");
        }
        finally
        {
            unitViews?.Clear();
            contactViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckNavigationLargeAltTargets()
    {
        // Plain numbers 1..6 must NOT trigger navigation-large debug targets; Alt+1..6 MUST trigger them
        for (int i = 1; i <= 6; i++)
        {
            Key key = (Key)((int)Key.Key0 + i);
            Require(!Main.TryGetNavigationLargeDebugTarget(key, altPressed: false, ctrlOrCmdPressed: false, shiftPressed: false, out _),
                $"Plain key {i} must not trigger navigation-large debug route target.");
            Require(!Main.TryGetNavigationLargeDebugTarget(key, altPressed: false, ctrlOrCmdPressed: true, shiftPressed: false, out _),
                $"Ctrl+{i} must not trigger navigation-large debug route target.");
            Require(!Main.TryGetNavigationLargeDebugTarget(key, altPressed: false, ctrlOrCmdPressed: false, shiftPressed: true, out _),
                $"Shift+{i} must not trigger navigation-large debug route target.");
            Require(Main.TryGetNavigationLargeDebugTarget(key, altPressed: true, ctrlOrCmdPressed: false, shiftPressed: false, out NumericsVector2 target) &&
                    target.X > 0f && target.Y > 0f,
                $"Alt+{i} must return a valid navigation-large debug route target.");
        }
    }

    private static void CheckControlsHelpLineFormatting()
    {
        string macHelp = Main.FormatControlsHelpText(isMacOs: true);
        Require(macHelp.Contains("Cmd/Ctrl+1-9 set"), $"macOS help line must contain 'Cmd/Ctrl+1-9 set', got '{macHelp}'.");
        Require(macHelp.Contains("1-9 select (twice: center)"), $"macOS help line must contain '1-9 select (twice: center)', got '{macHelp}'.");
        Require(macHelp.Contains("Shift+1-9 add"), $"macOS help line must contain 'Shift+1-9 add', got '{macHelp}'.");
        Require(!macHelp.Contains('\n'), "Bottom help line must stay on a single line.");
    }

    private static bool TryProjectOrthoForViewport(Camera3D camera, Vector3 worldPoint, Vector2 viewportSize, out Vector2 screenPoint)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
        {
            screenPoint = default;
            return false;
        }

        Transform3D camTransform = camera.GlobalTransform;
        Vector3 local = camTransform.AffineInverse() * worldPoint;
        if (local.Z >= 0f)
        {
            screenPoint = default;
            return false;
        }

        float height = MathF.Max(0.001f, camera.Size);
        float aspect = viewportSize.X / viewportSize.Y;
        float width = height * aspect;
        float u = (local.X / width) + 0.5f;
        float v = 0.5f - (local.Y / height);
        screenPoint = new Vector2(u * viewportSize.X, v * viewportSize.Y);
        return true;
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
        Require(replica.TryApply(bytes.AsSpan(0, size)), "Selection regression snapshot was rejected.");
    }

    private static void CheckRangeRingsAndZeroAllocations(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? unitViews = null;
        try
        {
            UnitCatalog catalog = PrototypeScenario.LoadCatalog();
            HeightMap map = HeightMap.CreatePrototype();
            unitViews = new UnitViewSet(parent, map, catalog);
            ushort tankDef = catalog.RequireDefinitionIndex("prototype_tank");
            ushort apcDef = catalog.RequireDefinitionIndex("apc");

            ClientReplica replica = new();
            byte[] buffer = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation tank = new(
                new EntityId(101),
                tankDef,
                new NumericsVector2(20f, 20f),
                0,
                Health: 100,
                Ammunition: 10,
                Stance: FireStance.FireAtWill);

            OwnUnitObservation unarmedApc = new(
                new EntityId(102),
                apcDef,
                new NumericsVector2(40f, 20f),
                0,
                Health: 100,
                Ammunition: 0,
                Stance: FireStance.HoldFire);

            ApplyWithContacts(replica, buffer, 1, 1, [tank, unarmedApc], []);
            unitViews.Sync(replica, 1.0);

            UnitView tankView = unitViews.Views[tank.Id];
            UnitView apcView = unitViews.Views[unarmedApc.Id];

            Require(tankView.IsArmed, "Prototype tank must be armed.");
            Require(!apcView.IsArmed, "APC must be unarmed.");
            Require(tankView.WeaponRange > 0f, "Tank weapon range must be > 0.");
            Require(tankView.SensorRange > 0f, "Tank sensor range must be > 0.");
            Require(apcView.SensorRange > 0f, "APC sensor range must be > 0.");

            // 1. Deselected: rings are hidden
            tankView.SetSelected(false);
            apcView.SetSelected(false);
            Require(!tankView.WeaponRangeRing.Visible, "Deselected tank weapon ring must be hidden.");
            Require(!tankView.SensorRangeRing.Visible, "Deselected tank sensor ring must be hidden.");
            Require(!apcView.WeaponRangeRing.Visible, "Deselected APC weapon ring must be hidden.");
            Require(!apcView.SensorRangeRing.Visible, "Deselected APC sensor ring must be hidden.");

            // 2. Select armed tank: both weapon range ring and sensor range ring visible
            unitViews.Selected.Clear();
            unitViews.Selected.Add(tank.Id);
            unitViews.UpdateSelectionVisuals();
            Require(tankView.WeaponRangeRing.Visible, "Selected armed tank must show weapon range ring.");
            Require(tankView.SensorRangeRing.Visible, "Selected armed tank must show sensor range ring.");

            // 3. Select unarmed APC: only sensor range ring visible, weapon ring hidden
            unitViews.Selected.Clear();
            unitViews.Selected.Add(unarmedApc.Id);
            unitViews.UpdateSelectionVisuals();
            Require(!apcView.WeaponRangeRing.Visible, "Selected unarmed APC must NOT show weapon range ring.");
            Require(apcView.SensorRangeRing.Visible, "Selected unarmed APC must show sensor range ring.");

            // 4. Deselect: all hidden
            unitViews.Selected.Clear();
            unitViews.UpdateSelectionVisuals();
            Require(!tankView.WeaponRangeRing.Visible, "Deselected tank weapon ring must be hidden.");
            Require(!tankView.SensorRangeRing.Visible, "Deselected tank sensor ring must be hidden.");
            Require(!apcView.WeaponRangeRing.Visible, "Deselected APC weapon ring must be hidden.");
            Require(!apcView.SensorRangeRing.Visible, "Deselected APC sensor ring must be hidden.");

            // 5. Select tank again and verify zero allocations in steady state
            unitViews.Selected.Add(tank.Id);
            unitViews.UpdateSelectionVisuals();
            for (int i = 0; i < 5; i++)
            {
                unitViews.UpdateSelectionVisuals();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
            {
                unitViews.UpdateSelectionVisuals();
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(alloc == 0, $"Range rings in steady state allocated {alloc} bytes (expected 0).");

            // 6. Movement threshold: do not rebuild mesh every frame while moving; drape only when moved >= 3 m
            int rebuildCountBefore = tankView.RingsRebuildCount;
            Vector3 startPos = tankView.Root.Position;

            // Small movement (< 3 m) must NOT rebuild ring meshes
            tankView.UpdatePosition(startPos + new Vector3(1.0f, 0f, 0f));
            Require(tankView.RingsRebuildCount == rebuildCountBefore,
                $"Small movement (< 3m) must not rebuild range rings (was {tankView.RingsRebuildCount}, expected {rebuildCountBefore}).");

            tankView.UpdatePosition(startPos + new Vector3(2.0f, 0f, 0f));
            Require(tankView.RingsRebuildCount == rebuildCountBefore,
                $"Small movement (< 3m) must not rebuild range rings (was {tankView.RingsRebuildCount}, expected {rebuildCountBefore}).");

            // Movement >= 3 m MUST rebuild ring meshes
            tankView.UpdatePosition(startPos + new Vector3(3.5f, 0f, 0f));
            Require(tankView.RingsRebuildCount > rebuildCountBefore,
                "Movement >= 3m must rebuild range rings.");

            // 7. Map rectangle clipping tests (ClipSegmentToMap)
            float mapWidth = 100f;
            float mapDepth = 100f;

            // Inside: segment entirely within bounds
            Require(UnitView.ClipSegmentToMap(10f, 10f, 20f, 20f, mapWidth, mapDepth, out float cx0, out float cz0, out float cx1, out float cz1),
                "Segment inside map bounds must return true.");
            Require(Mathf.IsEqualApprox(cx0, 10f) && Mathf.IsEqualApprox(cz0, 10f) &&
                    Mathf.IsEqualApprox(cx1, 20f) && Mathf.IsEqualApprox(cz1, 20f),
                "Segment inside bounds must not alter endpoints.");

            // Outside: segment entirely outside map bounds
            Require(!UnitView.ClipSegmentToMap(-20f, 10f, -5f, 10f, mapWidth, mapDepth, out _, out _, out _, out _),
                "Segment outside left edge must return false.");
            Require(!UnitView.ClipSegmentToMap(10f, 120f, 20f, 130f, mapWidth, mapDepth, out _, out _, out _, out _),
                "Segment outside bottom edge must return false.");

            // Crossing: segment crossing left boundary
            Require(UnitView.ClipSegmentToMap(-10f, 50f, 10f, 50f, mapWidth, mapDepth, out cx0, out cz0, out cx1, out cz1),
                "Segment crossing left edge must return true.");
            Require(Mathf.IsEqualApprox(cx0, 0f) && Mathf.IsEqualApprox(cz0, 50f) &&
                    Mathf.IsEqualApprox(cx1, 10f) && Mathf.IsEqualApprox(cz1, 50f),
                "Clipped segment must clamp to x=0 at left boundary.");

            // Crossing: segment crossing right boundary
            Require(UnitView.ClipSegmentToMap(90f, 50f, 110f, 50f, mapWidth, mapDepth, out cx0, out cz0, out cx1, out cz1),
                "Segment crossing right edge must return true.");
            Require(Mathf.IsEqualApprox(cx0, 90f) && Mathf.IsEqualApprox(cz0, 50f) &&
                    Mathf.IsEqualApprox(cx1, 100f) && Mathf.IsEqualApprox(cz1, 50f),
                "Clipped segment must clamp to x=100 at right boundary.");

            // Crossing: segment crossing top-left corner
            Require(UnitView.ClipSegmentToMap(-10f, -10f, 10f, 10f, mapWidth, mapDepth, out cx0, out cz0, out cx1, out cz1),
                "Diagonal segment crossing top-left corner must return true.");
            Require(Mathf.IsEqualApprox(cx0, 0f) && Mathf.IsEqualApprox(cz0, 0f) &&
                    Mathf.IsEqualApprox(cx1, 10f) && Mathf.IsEqualApprox(cz1, 10f),
                "Clipped segment must clamp to corner at (0, 0).");

            // 8. Near-edge unit range ring drape
            tankView.UpdatePosition(new Vector3(2f, 0f, 2f));
            Require(tankView.RingsRebuildCount > rebuildCountBefore + 1, "Corner reposition must rebuild range rings.");
        }
        finally
        {
            unitViews?.Clear();
            parent.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
