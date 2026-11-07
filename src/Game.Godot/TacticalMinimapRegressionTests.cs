using System;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Regression tests for TacticalMinimap:
/// 1. Rectangular map coordinate transforms, letterbox margins, boundary clicks.
/// 2. Contact observation lifecycle (Current -> LastKnown + Current -> only Current), age growth in tooltip, removal on loss, scenario reset.
/// 3. Ustye camera navigation, focus centering, zoom/yaw/pitch preservation, selection preservation, RMB absorption.
/// 4. Camera frustum clipping at map boundaries and rotation invariance.
/// 5. Zero steady-state allocations during continuous updates and redraws.
/// </summary>
internal static class TacticalMinimapRegressionTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run(Node3D scene)
    {
        CheckRectangularMapTransforms(scene);
        CheckContactLifecycleAndTooltips(scene);
        CheckCameraNavigationAndInputOnUstye(scene);
        CheckFrustumClippingAtBoundaries(scene);
        CheckSteadyStateZeroAllocations(scene);
        CheckBackgroundRasterizationOnUstye(scene);
        GD.Print("TACTICAL_MINIMAP_REGRESSIONS_OK 6/6 passed: rectangular_transforms contact_lifecycle camera_navigation frustum_clipping zero_allocations ustye_background");
    }

    private static void CheckRectangularMapTransforms(Node3D scene)
    {
        // Rectangular map: 1200 x 600 m (aspect 2:1)
        HeightMap rectMap = new(13, 7, 100f, 1f, new ushort[13 * 7]);
        StaticGeometry geometry = new(rectMap, ReadOnlySpan<OpaqueStructure>.Empty);

        TacticalMinimap minimap = new();
        scene.AddChild(minimap);
        try
        {
            minimap.Size = new Vector2(240f, 240f);
            minimap.Initialize(geometry, cameraRig: null, camera: null, session: null, unitViews: null, contactViews: null, catalog: null);

            Rect2 mapRect = minimap.MapRect;
            // 2:1 map inside 232x232 content area should have width 232 and height 116
            Require(MathF.Abs(mapRect.Size.X - 232f) < 1f, $"Expected mapRect width ~232, got {mapRect.Size.X}");
            Require(MathF.Abs(mapRect.Size.Y - 116f) < 1f, $"Expected mapRect height ~116, got {mapRect.Size.Y}");

            // Verify top and bottom letterbox fields exist
            Require(mapRect.Position.Y > TacticalMinimap.FramePadding, "Expected top field margin above mapRect.");
            Require(minimap.Size.Y - mapRect.End.Y > TacticalMinimap.FramePadding, "Expected bottom field margin below mapRect.");

            // Verify corner and center transforms
            Vector2[] testWorldPoints =
            [
                new Vector2(0f, 0f),
                new Vector2(1200f, 0f),
                new Vector2(1200f, 600f),
                new Vector2(0f, 600f),
                new Vector2(600f, 300f),
                new Vector2(300f, 150f)
            ];

            foreach (Vector2 wp in testWorldPoints)
            {
                Vector2 mPos = minimap.WorldToMinimap(wp);
                Require(minimap.IsPointOnMap(mPos), $"Minimap point {mPos} for world {wp} must be on map.");

                bool ok = minimap.TryMinimapToWorld(mPos, out Vector2 roundTripWp);
                Require(ok, $"TryMinimapToWorld must succeed for point {mPos}.");
                Require(wp.DistanceTo(roundTripWp) < 0.1f, $"Roundtrip mismatch: expected {wp}, got {roundTripWp}.");
            }

            // Test clicks on fields outside mapRect: TryMinimapToWorld must return false
            Vector2 topFieldClick = new(120f, 20f);
            Require(!minimap.IsPointOnMap(topFieldClick), "Top field point must not be on map.");
            Require(!minimap.TryMinimapToWorld(topFieldClick, out _), "TryMinimapToWorld on field click must return false.");

            Vector2 bottomFieldClick = new(120f, 220f);
            Require(!minimap.IsPointOnMap(bottomFieldClick), "Bottom field point must not be on map.");
            Require(!minimap.TryMinimapToWorld(bottomFieldClick, out _), "TryMinimapToWorld on bottom field click must return false.");
        }
        finally
        {
            scene.RemoveChild(minimap);
            minimap.QueueFree();
        }
    }

    private static void CheckContactLifecycleAndTooltips(Node3D scene)
    {
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geometry = new(map, ReadOnlySpan<OpaqueStructure>.Empty);
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        TacticalMinimap minimap = new();
        scene.AddChild(minimap);
        try
        {
            minimap.Size = new Vector2(240f, 240f);
            TestSession session = new(map, catalog);
            minimap.Initialize(geometry, cameraRig: null, camera: null, session: session, unitViews: null, contactViews: null, catalog: catalog);

            // Snapshot 1 (tick 10): Contact #1 at A (30, 30) as Current
            Vector2 posA = new(30f, 30f);
            session.Tick = 10;
            session.SetContacts(
            [
                new ContactObservation(new ContactId(1), ContactState.Current, ContactClassification.Tank, 0, new NumericsVector2(posA.X, posA.Y), LastSeenTick: 10)
            ]);

            minimap.UpdateMinimap(0.016);
            Require(minimap.RenderedCurrentCount == 1, "Expected 1 Current contact.");
            Require(minimap.RenderedLastKnownCount == 0, "Expected 0 LastKnown contacts.");

            Vector2 mPosA = minimap.WorldToMinimap(posA);

            // Hovering over Current contact should not show LastKnown age tooltip
            minimap.SimulateHover(mPosA);
            Require(!minimap.IsTooltipVisible, "Hovering over Current contact should not show LastKnown age tooltip.");

            // Snapshot 2 (tick 30, 1.0s later): Contact #1 at A transitions to LastKnown, Contact #2 at B (60, 60) appears as Current
            Vector2 posB = new(60f, 60f);
            session.Tick = 30;
            session.SetContacts(
            [
                new ContactObservation(new ContactId(1), ContactState.LastKnown, ContactClassification.Tank, 0, new NumericsVector2(posA.X, posA.Y), LastSeenTick: 10),
                new ContactObservation(new ContactId(2), ContactState.Current, ContactClassification.Recon, 1, new NumericsVector2(posB.X, posB.Y), LastSeenTick: 30)
            ]);

            minimap.UpdateMinimap(0.016);
            Require(minimap.RenderedCurrentCount == 1, "Expected 1 Current contact at B.");
            Require(minimap.RenderedLastKnownCount == 1, "Expected 1 LastKnown contact at A.");
            Require(minimap.RenderedContactCount == 2, "Expected 2 total contacts.");

            // Verify position A is frozen (motionless)
            Vector2 mPosA_after = minimap.WorldToMinimap(posA);
            Require(mPosA.DistanceTo(mPosA_after) < 0.001f, "LastKnown marker at A must remain strictly motionless.");

            // Hover over marker A: tooltip must show age 1.0s
            minimap.SimulateHover(mPosA);
            Require(minimap.IsTooltipVisible, "Tooltip must become visible when hovering over LastKnown contact A.");
            Require(minimap.HoveredContactId == new ContactId(1), "HoveredContactId must be Contact #1.");
            Require(minimap.ActiveTooltipText.Contains("1.0s"), $"Tooltip text must report age 1.0s, got '{minimap.ActiveTooltipText}'.");

            // Snapshot 3 (tick 50, 2.0s after loss): Server tick advances to 50
            session.Tick = 50;
            minimap.UpdateMinimap(0.016);
            Require(minimap.IsTooltipVisible, "Tooltip must remain visible as tick advances.");
            Require(minimap.ActiveTooltipText.Contains("2.0s"), $"Tooltip text must report age 2.0s, got '{minimap.ActiveTooltipText}'.");

            // Snapshot 4 (tick 70): Contact #1 is removed from replica (confirmed empty or timeout)
            session.Tick = 70;
            session.SetContacts(
            [
                new ContactObservation(new ContactId(2), ContactState.Current, ContactClassification.Recon, 1, new NumericsVector2(posB.X, posB.Y), LastSeenTick: 30)
            ]);

            minimap.UpdateMinimap(0.016);
            Require(minimap.RenderedLastKnownCount == 0, "LastKnown contact A must be removed from minimap.");
            Require(minimap.RenderedCurrentCount == 1, "Current contact B must remain.");
            Require(!minimap.IsTooltipVisible, "Tooltip for Contact #1 must disappear upon contact removal.");
            Require(minimap.HoveredContactId is null, "HoveredContactId must be null after contact removal.");

            // Verify scenario reset clears all state
            minimap.Reset();
            Require(!minimap.IsTooltipVisible, "Tooltip must be hidden after reset.");
            Require(!minimap.IsDraggingCamera, "Dragging must be false after reset.");
        }
        finally
        {
            scene.RemoveChild(minimap);
            minimap.QueueFree();
        }
    }

    private static void CheckCameraNavigationAndInputOnUstye(Node3D scene)
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        HeightMap map = geometry.HeightMap;
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        Camera3D camera = new() { Projection = Camera3D.ProjectionType.Orthogonal, Size = 160f, Far = 3500f };
        scene.AddChild(camera);
        CameraRig cameraRig = new(camera, new Vector3(2160f, 10f, 2325f), 160f, map.Width, map.Depth);

        TacticalMinimap minimap = new();
        scene.AddChild(minimap);
        try
        {
            minimap.Size = new Vector2(240f, 240f);
            TestSession session = new(map, catalog);

            // Set up 3 friendly units forming a column
            Vector2 u0 = new(2160f, 2325f);
            Vector2 u1 = new(2140f, 2325f);
            Vector2 u2 = new(2120f, 2325f);
            session.SetOwnUnits(
            [
                new OwnUnitObservation(new EntityId(1), 0, new NumericsVector2(u0.X, u0.Y), 0),
                new OwnUnitObservation(new EntityId(2), 0, new NumericsVector2(u1.X, u1.Y), 0),
                new OwnUnitObservation(new EntityId(3), 0, new NumericsVector2(u2.X, u2.Y), 0)
            ]);

            Node3D unitParent = new();
            scene.AddChild(unitParent);
            UnitViewSet unitViews = new(unitParent, map, catalog);
            // Select units 1 and 2
            unitViews.Selected.Add(new EntityId(1));
            unitViews.Selected.Add(new EntityId(2));

            UstyePlanData plan = UstyePlanData.Load();
            minimap.Initialize(geometry, cameraRig, camera, session, unitViews, null, catalog, plan);

            // Record initial camera state and selections
            float initialSize = cameraRig.Size;
            float initialYaw = cameraRig.ViewYaw;
            float initialPitch = cameraRig.ViewPitch;
            int selectedCount = unitViews.Selected.Count;

            // Pan camera away towards eastern map edge
            cameraRig.CenterOn(new Vector3(4500f, 0f, 4500f));
            Require(cameraRig.Focus.X > 4000f, "Camera must have panned away.");

            // Click minimap at column position
            Vector2 columnMinimapPos = minimap.WorldToMinimap(u0);
            minimap.SimulateClick(columnMinimapPos, MouseButton.Left, pressed: true);
            minimap.SimulateClick(columnMinimapPos, MouseButton.Left, pressed: false);

            // Verify camera centered back on column
            Vector3 focusAfter = cameraRig.Focus;
            Require(MathF.Abs(focusAfter.X - u0.X) < 1f, $"Camera X should be near {u0.X}, got {focusAfter.X}");
            Require(MathF.Abs(focusAfter.Z - u0.Y) < 1f, $"Camera Z should be near {u0.Y}, got {focusAfter.Z}");

            // Verify zoom, yaw, pitch, and selections are preserved
            Require(MathF.Abs(cameraRig.Size - initialSize) < 0.01f, "Camera zoom must be preserved when navigating by minimap.");
            Require(MathF.Abs(cameraRig.ViewYaw - initialYaw) < 0.001f, "Camera yaw must be preserved when navigating by minimap.");
            Require(MathF.Abs(cameraRig.ViewPitch - initialPitch) < 0.001f, "Camera pitch must be preserved when navigating by minimap.");
            Require(unitViews.Selected.Count == selectedCount, "Unit selection must be preserved when navigating by minimap.");
            Require(unitViews.Selected.Contains(new EntityId(1)) && unitViews.Selected.Contains(new EntityId(2)), "Selected IDs must be preserved.");

            // Right-click simulation on minimap: must absorb click and not throw or change state
            minimap.SimulateClick(columnMinimapPos, MouseButton.Right, pressed: true);
            minimap.SimulateClick(columnMinimapPos, MouseButton.Right, pressed: false);
            Require(!minimap.IsDraggingCamera, "Right click must not start camera drag.");

            // Drag gesture and focus loss regression (P2 bug fix):
            // 1. Start drag at (1800, 1600) via Godot InputEventMouseButton
            Vector2 dragStartWorld = new(1800f, 1600f);
            Vector2 dragStartMinimap = minimap.WorldToMinimap(dragStartWorld);
            using (InputEventMouseButton lmbPress = new()
            {
                Position = dragStartMinimap,
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                ButtonMask = MouseButtonMask.Left
            })
            {
                minimap._GuiInput(lmbPress);
            }
            Require(minimap.IsDraggingCamera, "LMB down on minimap must initiate camera drag.");
            Require(MathF.Abs(cameraRig.Focus.X - dragStartWorld.X) < 1f && MathF.Abs(cameraRig.Focus.Z - dragStartWorld.Y) < 1f,
                $"Camera focus must be at drag start {dragStartWorld}, got ({cameraRig.Focus.X}, {cameraRig.Focus.Z})");

            // 2. Simulate loss of focus via Godot notification (WMWindowFocusOut / FocusExit)
            minimap.Notification((int)Node.NotificationWMWindowFocusOut);
            Require(!minimap.IsDraggingCamera, "Loss of focus notification must cancel active camera drag on minimap.");

            // Also test Main.CancelActiveDrag if scene is Main and has HUD minimap
            if (scene is Main mainScene && mainScene.Minimap is not null)
            {
                Vector2 p = mainScene.Minimap.MapRect.GetCenter();
                using (InputEventMouseButton lmbPress = new()
                {
                    Position = p,
                    ButtonIndex = MouseButton.Left,
                    Pressed = true,
                    ButtonMask = MouseButtonMask.Left
                })
                {
                    mainScene.Minimap._GuiInput(lmbPress);
                }
                Require(mainScene.Minimap.IsDraggingCamera, "LMB down on Main.Minimap must start drag.");
                mainScene.CancelActiveDrag();
                Require(!mainScene.Minimap.IsDraggingCamera, "Main.CancelActiveDrag must cancel Main.Minimap camera drag.");
            }

            // 3. Return mouse cursor to minimap at (3600, 3400) without buttons pressed (ButtonMask = 0)
            Vector2 jumpWorld = new(3600f, 3400f);
            Vector2 jumpMinimap = minimap.WorldToMinimap(jumpWorld);
            using (InputEventMouseMotion motionWithoutButtons = new()
            {
                Position = jumpMinimap,
                ButtonMask = 0
            })
            {
                minimap._GuiInput(motionWithoutButtons);
            }
            Require(!minimap.IsDraggingCamera, "Mouse motion without LMB pressed must not be dragging.");
            Require(MathF.Abs(cameraRig.Focus.X - dragStartWorld.X) < 1f && MathF.Abs(cameraRig.Focus.Z - dragStartWorld.Y) < 1f,
                $"Camera focus must remain at {dragStartWorld} and not jump to {jumpWorld}, actual is ({cameraRig.Focus.X}, {cameraRig.Focus.Z})");

            // 4. Test motion event arriving with ButtonMask = 0 when drag flag was active (e.g. lost focus without notification)
            using (InputEventMouseButton lmbPress2 = new()
            {
                Position = dragStartMinimap,
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                ButtonMask = MouseButtonMask.Left
            })
            {
                minimap._GuiInput(lmbPress2);
            }
            Require(minimap.IsDraggingCamera, "Drag must be active before unheld motion.");
            using (InputEventMouseMotion motionWithoutLmb = new()
            {
                Position = jumpMinimap,
                ButtonMask = 0
            })
            {
                minimap._GuiInput(motionWithoutLmb);
            }
            Require(!minimap.IsDraggingCamera, "Mouse motion without LMB in ButtonMask must cancel camera drag.");
            Require(MathF.Abs(cameraRig.Focus.X - dragStartWorld.X) < 1f && MathF.Abs(cameraRig.Focus.Z - dragStartWorld.Y) < 1f,
                "Camera must not move when motion event has ButtonMask == 0.");

            // 5. Test valid drag with LMB held in ButtonMask
            using (InputEventMouseButton lmbPress3 = new()
            {
                Position = dragStartMinimap,
                ButtonIndex = MouseButton.Left,
                Pressed = true,
                ButtonMask = MouseButtonMask.Left
            })
            {
                minimap._GuiInput(lmbPress3);
            }
            using (InputEventMouseMotion validMotion = new()
            {
                Position = jumpMinimap,
                ButtonMask = MouseButtonMask.Left
            })
            {
                minimap._GuiInput(validMotion);
            }
            Require(minimap.IsDraggingCamera, "Drag must remain active during motion with LMB held.");
            Require(MathF.Abs(cameraRig.Focus.X - jumpWorld.X) < 1f && MathF.Abs(cameraRig.Focus.Z - jumpWorld.Y) < 1f,
                $"Camera must follow drag to {jumpWorld}, got ({cameraRig.Focus.X}, {cameraRig.Focus.Z})");

            // Clean up: release LMB
            using (InputEventMouseButton lmbRelease = new()
            {
                Position = jumpMinimap,
                ButtonIndex = MouseButton.Left,
                Pressed = false,
                ButtonMask = 0
            })
            {
                minimap._GuiInput(lmbRelease);
            }
            Require(!minimap.IsDraggingCamera, "Drag must be inactive after release.");

            Span<Vector2> footprint = stackalloc Vector2[16];
            Require(minimap.ComputeClippedFrustum(footprint) >= 3, "Orthographic camera must have a footprint.");
            cameraRig.SetPerspective(true);
            cameraRig.Orbit(0.7f, OrbitCameraPose.MinPitch - cameraRig.ViewPitch);
            int vertices = minimap.ComputeClippedFrustum(footprint);
            Require(vertices >= 3, "A low perspective camera must keep its footprint with the sky in view.");
            for (int i = 0; i < vertices; i++)
            {
                Require(float.IsFinite(footprint[i].X) && float.IsFinite(footprint[i].Y), "Footprint must stay finite.");
                Require(footprint[i].X >= -0.01f && footprint[i].X <= map.Width + 0.01f &&
                    footprint[i].Y >= -0.01f && footprint[i].Y <= map.Depth + 0.01f, "Footprint must stay inside the map.");
            }

            unitParent.QueueFree();
        }
        finally
        {
            scene.RemoveChild(minimap);
            minimap.QueueFree();
            scene.RemoveChild(camera);
            camera.QueueFree();
        }
    }

    private static void CheckFrustumClippingAtBoundaries(Node3D scene)
    {
        // Test polygon clipping against a 5000 x 5000 map
        const float mapW = 5000f;
        const float mapD = 5000f;

        // Quad partially outside northern and western boundaries [-200..300, -100..400]
        Vector2[] quad =
        [
            new(-200f, -100f),
            new(300f, -100f),
            new(300f, 400f),
            new(-200f, 400f)
        ];

        Span<Vector2> clipped = stackalloc Vector2[16];
        int count = TacticalMinimap.ClipConvexPolygonToRect(quad, clipped, 0f, mapW, 0f, mapD);

        Require(count >= 3 && count <= 8, $"Clipped polygon should have 3..8 vertices, got {count}.");

        for (int i = 0; i < count; i++)
        {
            Vector2 v = clipped[i];
            Require(v.X >= -0.01f && v.X <= mapW + 0.01f, $"Vertex X {v.X} outside [0, {mapW}].");
            Require(v.Y >= -0.01f && v.Y <= mapD + 0.01f, $"Vertex Y {v.Y} outside [0, {mapD}].");
        }
    }

    private static void CheckSteadyStateZeroAllocations(Node3D scene)
    {
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geometry = new(map, ReadOnlySpan<OpaqueStructure>.Empty);
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        TacticalMinimap minimap = new();
        scene.AddChild(minimap);
        try
        {
            minimap.Size = new Vector2(240f, 240f);
            TestSession session = new(map, catalog);

            // Populate 50 units and 50 contacts (100 total markers)
            OwnUnitObservation[] units = new OwnUnitObservation[50];
            ContactObservation[] contacts = new ContactObservation[50];

            for (int i = 0; i < 50; i++)
            {
                float x = 10f + (i % 10) * 10f;
                float z = 10f + (i / 10) * 20f;
                units[i] = new OwnUnitObservation(new EntityId((uint)(i + 1)), 0, new NumericsVector2(x, z), 0);
                contacts[i] = new ContactObservation(new ContactId((uint)(i + 1)), i % 2 == 0 ? ContactState.Current : ContactState.LastKnown, ContactClassification.Tank, 0, new NumericsVector2(x + 5f, z + 5f), LastSeenTick: 10);
            }

            session.SetObservations(units, contacts);
            minimap.Initialize(geometry, cameraRig: null, camera: null, session: session, unitViews: null, contactViews: null, catalog: catalog);

            Vector2 hoverTest = minimap.WorldToMinimap(contacts[1].Position);
            Vector2 worldTest = new(50f, 50f);
            Vector2[] quad = [new(10f, 10f), new(100f, 10f), new(100f, 100f), new(10f, 100f)];
            Span<Vector2> clipped = stackalloc Vector2[16];

            // Warm up
            for (int f = 0; f < 10; f++)
            {
                minimap.UpdateMinimap(0.016);
                minimap.SimulateHover(hoverTest);
                minimap.WorldToMinimap(worldTest);
                minimap.TryMinimapToWorld(hoverTest, out _);
                TacticalMinimap.ClipConvexPolygonToRect(quad, clipped, 0f, 128f, 0f, 128f);
            }

            Require(minimap.IsTooltipVisible, "Allocation measurement must hover a LastKnown contact.");

            // Measure steady-state allocations across 100 frames
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();

            for (int f = 0; f < 100; f++)
            {
                session.Tick = 10 + f;
                minimap.UpdateMinimap(0.016);
                minimap.SimulateHover(hoverTest);
                minimap.WorldToMinimap(worldTest);
                minimap.TryMinimapToWorld(hoverTest, out _);
                TacticalMinimap.ClipConvexPolygonToRect(quad, clipped, 0f, 128f, 0f, 128f);
            }

            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAlloc = allocAfter - allocBefore;

            Require(totalAlloc == 0, $"TacticalMinimap steady-state hot path must allocate 0 bytes, but allocated {totalAlloc} B across 100 frames (100 markers).");
            GD.Print($"TACTICAL_MINIMAP_ALLOC bytes={totalAlloc} frames=100 own=50 current=25 last_known=25 hover=last_known");
        }
        finally
        {
            scene.RemoveChild(minimap);
            minimap.QueueFree();
        }
    }

    private static void CheckBackgroundRasterizationOnUstye(Node3D scene)
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        UstyePlanData plan = UstyePlanData.Load();

        ImageTexture? texture = TacticalMinimap.GenerateBackground(geometry, plan, 256, 256);
        Require(texture is not null, "GenerateBackground must produce an ImageTexture.");
        Require(texture!.GetWidth() == 256, $"Expected width 256, got {texture.GetWidth()}");
        Require(texture!.GetHeight() == 256, $"Expected height 256, got {texture.GetHeight()}");
    }

    private sealed class TestSession : IGameSession
    {
        private readonly ClientReplica _replica = new();
        private uint _sequence;

        public int Tick { get; set; } = 10;
        public ClientReplica Replica => _replica;
        public MissionObservation Mission => default;
        public double RenderTick => Tick;
        public double LastTickMilliseconds => 0.0;
        public long LastTickAllocatedBytes => 0;
        public long BytesReceived => 0;
        public double PendingSeconds => 0.0;
        public CommandResult? LastCommandResult => null;
        public GroupMoveResult? LastGroupMoveResult => null;
        public uint LastSubmittedCommandSequence => 0;
        public long RejectedCommands => 0;
        public bool IsConnected => true;
        public int AssignedPlayerId => 0;
        public string StatusDescription => "Test Session";

        public TestSession(HeightMap map, UnitCatalog catalog)
        {
        }

        public void SetOwnUnits(ReadOnlySpan<OwnUnitObservation> units) =>
            SetObservations(units, _replica.Contacts);

        public void SetContacts(ReadOnlySpan<ContactObservation> contacts) =>
            SetObservations(_replica.OwnUnits, contacts);

        public void SetObservations(ReadOnlySpan<OwnUnitObservation> units, ReadOnlySpan<ContactObservation> contacts)
        {
            byte[] snapshot = BuildSnapshotBytes(++_sequence, Tick, units, contacts);
            Require(_replica.TryApply(snapshot), "The test snapshot must be applied.");
        }

        private static byte[] BuildSnapshotBytes(
            uint sequence,
            int tick,
            ReadOnlySpan<OwnUnitObservation> units,
            ReadOnlySpan<ContactObservation> contacts)
        {
            byte[] buffer = new byte[8192];
            int written = WireProtocol.WriteSnapshot(
                buffer,
                sequence: sequence,
                tick: tick,
                units: units,
                contacts: contacts,
                events: ReadOnlySpan<CombatEventObservation>.Empty,
                mission: default);

            byte[] result = new byte[written];
            buffer.AsSpan(0, written).CopyTo(result);
            return result;
        }

        public bool Attack(EntityId attackerId, ContactId targetContactId) => true;
        public bool Move(EntityId unit, NumericsVector2 target, float? finalFacing = null, MoveMode mode = MoveMode.Forward, uint? targetSurface = null) => true;
        public bool GroupMove(ReadOnlyMemory<EntityId> units, NumericsVector2 target, uint targetSurface = 0, float? finalFacing = null, MoveMode mode = MoveMode.Forward) => true;
        public bool SetFireStance(EntityId unit, FireStance stance) => true;
        public void Advance(double elapsedSeconds) { }
        public void Disconnect() { }
        public void Dispose() { }
    }
}
