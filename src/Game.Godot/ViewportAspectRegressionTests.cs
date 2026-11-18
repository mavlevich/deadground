using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

/// <summary>
/// Regression tests verifying multi-aspect support (4:3, 16:10, 16:9, 21:9) and 32:9 pillarbox cap:
/// 1. Project stretch configuration + real root Window aspect/visible-rect behavior.
/// 2. HUD panel bounds and non-overlap across wide and tall viewports.
/// 3. TacticalMinimap camera frustum widening away from map boundaries.
/// 4. Window-to-canvas input transforms, unit selection, badge visibility, and ground picking at far edges
///    across Orthographic/Perspective modes, Scaling3DScale, and resize/DPI changes.
/// </summary>
internal static partial class ViewportAspectRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckProjectStretchSettingsAndRootWindow(scene);
        CheckHudPanelsLayoutAcrossAspects(scene);
        CheckMinimapFrustumAcrossAspects(scene);
        CheckEdgeInputPickingAndProjectionAcrossAspects(scene);
        CheckAspectPreservingResizeAndDensityUpdates(scene);

        GD.Print("VIEWPORT_ASPECT_REGRESSIONS_OK 5/5 passed: root_window_cap hud_layout minimap_frustum edge_input_picking resize_density");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckProjectStretchSettingsAndRootWindow(Node3D scene)
    {
        string stretchMode = ProjectSettings.GetSetting("display/window/stretch/mode").AsString();
        string stretchAspect = ProjectSettings.GetSetting("display/window/stretch/aspect").AsString();
        Require(stretchMode == "canvas_items", $"Expected project stretch mode 'canvas_items', got '{stretchMode}'.");
        Require(stretchAspect == "expand", $"Expected project stretch aspect 'expand', got '{stretchAspect}'.");

        Window root = scene.GetWindow();
        Require(root is not null, "Scene must have a root Window.");

        Vector2I origSize = root!.Size;
        Vector2I origContentSize = root.ContentScaleSize;
        Window.ContentScaleAspectEnum origAspect = root.ContentScaleAspect;
        float origScale = root.Scaling3DScale;
        Viewport.Scaling3DModeEnum origMode = root.Scaling3DMode;
        Viewport.Msaa origMsaa = root.Msaa3D;
        try
        {
            // Verify ResolveWindowPixelSize uses capped active 21:9 pixels (3442x1440 -> 0.862 scale, 2x MSAA) for 32:9 windows
            DisplaySettings autoSettings = new(RenderScale: RenderScalePreset.Auto, AntiAliasing: AntiAliasingPreset.Auto);
            root.Size = new Vector2I(5120, 1440);
            Vector2I activePx = SettingsPanel.ResolveWindowPixelSize(root, new Vector2I(5120, 1440));
            Require(activePx.X < 5120 && activePx.Y == 1440 && activePx.X / (float)activePx.Y <= ViewportAspectLayout.MaxAspectRatio + 0.01f,
                $"ResolveWindowPixelSize at 5120x1440 must cap active pixels at 21:9, got {activePx}.");

            (float cappedScale, int cappedMsaa) = SettingsPanel.ApplyWindowRenderPolicy(root, root, autoSettings);
            Require(root.ContentScaleAspect == Window.ContentScaleAspectEnum.Keep &&
                    MathF.Abs(cappedScale - 0.862f) < 0.01f &&
                    cappedMsaa == 2,
                $"Capped 5120x1440 Auto expected Keep, ~0.862 scale, 2x MSAA, got {root.ContentScaleAspect}, {cappedScale:F3}, {cappedMsaa}x.");

            (int W, int H, bool ExpectKeep, float MinAspect, float MaxAspect)[] cases =
            [
                (1440, 900, false, 1.59f, 1.61f),
                (1920, 1080, false, 1.77f, 1.79f),
                (3440, 1440, false, 2.38f, 2.40f),
                (5120, 1440, true, 2.38f, 2.40f),
                (1440, 1080, false, 1.32f, 1.35f)
            ];

            foreach ((int w, int h, bool expectKeep, float minAspect, float maxAspect) in cases)
            {
                root.Size = new Vector2I(w, h);
                ViewportStretchSpec spec = SettingsPanel.ApplyWindowStretchCap(root, new Vector2I(w, h));
                Require(spec.UseAspectCapKeep == expectKeep,
                    $"For {w}x{h}, expected UseAspectCapKeep={expectKeep}, got {spec.UseAspectCapKeep}.");

                Window.ContentScaleAspectEnum expectedAspectEnum = expectKeep
                    ? Window.ContentScaleAspectEnum.Keep
                    : Window.ContentScaleAspectEnum.Expand;
                Require(root.ContentScaleAspect == expectedAspectEnum,
                    $"For {w}x{h}, root.ContentScaleAspect={root.ContentScaleAspect}, expected {expectedAspectEnum}.");

                Rect2 visible = root.GetVisibleRect();
                Transform2D screenXform = root.GetScreenTransform();
                float actualAspect = visible.Size.X / MathF.Max(1f, visible.Size.Y);

                Require(actualAspect >= minAspect && actualAspect <= maxAspect,
                    $"For {w}x{h}, expected visible rect aspect in [{minAspect}, {maxAspect}], got {actualAspect:F3} (visible={visible.Size}).");
                Require(actualAspect <= ViewportAspectLayout.MaxAspectRatio + 0.02f,
                    $"Visible rect aspect {actualAspect:F3} exceeded MaxAspectRatio {ViewportAspectLayout.MaxAspectRatio}.");
                if (expectKeep)
                {
                    Require(screenXform.Origin.X > 800f && Mathf.IsZeroApprox(screenXform.Origin.Y),
                        $"Expected centered pillarbox origin X > 800 for {w}x{h}, got {screenXform.Origin}.");
                }
                else
                {
                    Require(MathF.Abs(screenXform.Origin.X) < 1f && MathF.Abs(screenXform.Origin.Y) < 1f,
                        $"Expected zero pillarbox/letterbox bars for {w}x{h}, got origin {screenXform.Origin}.");
                }
            }
        }
        finally
        {
            root.Scaling3DScale = origScale;
            root.Scaling3DMode = origMode;
            root.Msaa3D = origMsaa;
            root.ContentScaleAspect = origAspect;
            root.ContentScaleSize = origContentSize;
            root.Size = origSize;
        }
    }

    private static void CheckHudPanelsLayoutAcrossAspects(Node3D scene)
    {
        Main main = scene as Main ?? throw new InvalidOperationException("HUD aspect regression requires Main scene.");
        Window root = main.GetWindow();
        Vector2I origRootSize = root.Size;
        Vector2I origContentSize = root.ContentScaleSize;
        Window.ContentScaleAspectEnum origAspect = root.ContentScaleAspect;
        bool origHudVisible = main.HudCanvas.Visible;
        string origStatsText = main.StatsLabel.Text;
        MissionPanel? origMissionPanel = main.MissionPanel;
        bool createdTempMission = origMissionPanel is null;

        if (createdTempMission)
        {
            main.ConfigureMissionPanel("ustye-raid");
        }

        main.HudCanvas.Visible = true;
        main.StatsLabel.Text = "20 Hz SIM / 10 Hz STATE / TICK 00010 / OWN: 3 / CONTACTS: 0\n3,986 ms per tick / 0 B tick allocations / 2,1 KiB/s";
        main.TopHud.ResetSize();

        try
        {
            // Verify real multi-line TopHud + MissionPanel briefing clearance via RefreshOccludingPanels across all aspect ratios
            (int W, int H)[] sizes =
            [
                (1440, 900),   // 16:10
                (1920, 1080),  // 16:9
                (3440, 1440),  // 21:9
                (5120, 1440),  // 32:9 (capped to 21:9)
                (1440, 1080)   // 4:3 taller window
            ];

            foreach ((int w, int h) in sizes)
            {
                root.Size = new Vector2I(w, h);
                SettingsPanel.ApplyWindowStretchCap(root, root.Size);
                Rect2 vis = root.GetVisibleRect();
                float visW = vis.Size.X;
                float visH = vis.Size.Y;

                // Explicitly ensure TopHud uses real multi-line minimum size and RefreshOccludingPanels updates briefing Y offset
                main.TopHud.ResetSize();
                main.BottomHud.SetSize(new Vector2(visW - 48f, 64f));
                main.BottomHud.Position = new Vector2(24f, visH - 88f);
                main.SelectionCard.Position = new Vector2(24f, visH - 196f);
                main.SelectionCard.SetSize(new Vector2(380f, 100f));
                main.Minimap.Position = new Vector2(visW - 24f - TacticalMinimap.DefaultWidgetSize, visH - 96f - TacticalMinimap.DefaultWidgetSize);
                main.Minimap.SetSize(new Vector2(TacticalMinimap.DefaultWidgetSize, TacticalMinimap.DefaultWidgetSize));

                main.RefreshOccludingPanels();

                ReadOnlySpan<Rect2> occluding = main.InputOccludingPanels;
                Rect2 topRect = occluding[0];
                Rect2 bottomRect = occluding[1];
                Rect2 cardRect = occluding[2];
                Rect2 missionRect = occluding[4];
                Rect2 minimapRect = occluding[5];

                Require(topRect.Size.Y >= 135f,
                    $"Real TopHud with 4 lines must expand to >= 135px, got {topRect.Size.Y:F1}px.");
                Require(missionRect.Size.X > 100f && missionRect.Size.Y > 80f,
                    $"Real MissionPanel briefing card must have positive layout size, got {missionRect}.");
                Require(!topRect.Intersects(missionRect) && missionRect.Position.Y >= topRect.End.Y + 9f,
                    $"TopHud ({topRect}) must clear MissionPanel briefing ({missionRect}) at {w}x{h}.");

                Rect2 screenRect = new(Vector2.Zero, vis.Size);
                Rect2[] realPanels = [topRect, missionRect, bottomRect, cardRect, minimapRect];
                for (int i = 0; i < realPanels.Length; i++)
                {
                    Require(screenRect.Encloses(realPanels[i]),
                        $"Real HUD panel {i} ({realPanels[i]}) escaped visible screen {screenRect} at {w}x{h}.");
                    for (int j = i + 1; j < realPanels.Length; j++)
                    {
                        Require(!realPanels[i].Intersects(realPanels[j]),
                            $"Real HUD panels {i} ({realPanels[i]}) and {j} ({realPanels[j]}) overlapped at {w}x{h}.");
                    }
                }

                Require(MathF.Abs(bottomRect.Position.Y - cardRect.End.Y - 8f) < 1f,
                    $"SelectionCard must stay 8px above BottomHud at {w}x{h}: bottom={bottomRect}, card={cardRect}.");
                Require(MathF.Abs(bottomRect.Position.Y - minimapRect.End.Y - 8f) < 1f,
                    $"Minimap must stay 8px above BottomHud at {w}x{h}: bottom={bottomRect}, minimap={minimapRect}.");
                Require(MathF.Abs(minimapRect.End.X - (visW - 24f)) < 1f,
                    $"Minimap must hug right edge at {w}x{h}: visW={visW}, minimap={minimapRect}.");
            }
        }
        finally
        {
            if (createdTempMission)
            {
                main.ClearMissionPanel();
            }
            main.StatsLabel.Text = origStatsText;
            main.TopHud.ResetSize();
            main.HudCanvas.Visible = origHudVisible;
            root.ContentScaleAspect = origAspect;
            root.ContentScaleSize = origContentSize;
            root.Size = origRootSize;
            main.OnWindowMetricsChanged();
            main.RefreshOccludingPanels();
        }
    }

    private static void CheckMinimapFrustumAcrossAspects(Node3D scene)
    {
        HeightMap map = BattleScenario.CreateMap();
        StaticGeometry geometry = new(map, ReadOnlySpan<OpaqueStructure>.Empty);
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");

        // Test away from map edges at fixed map center (1000, 0, 1000) and identical camera size/pitch
        Vector3 centerFocus = new(map.Width * 0.5f, 0f, map.Depth * 0.5f);

        float width1610 = MeasureMinimapFrustumWidth(scene, geometry, catalog, 1440, 900, centerFocus, out float height1610);
        float width169 = MeasureMinimapFrustumWidth(scene, geometry, catalog, 1600, 900, centerFocus, out float height169);
        float width219 = MeasureMinimapFrustumWidth(scene, geometry, catalog, 2150, 900, centerFocus, out float height219);

        float aspectRatioIncrease = width219 / width1610;
        float expectedIncrease = (2150f / 900f) / (1440f / 900f);
        Require(MathF.Abs(aspectRatioIncrease - expectedIncrease) < 0.05f,
            $"Minimap frustum width at 21:9 should scale by ~{expectedIncrease:F3}x, got {aspectRatioIncrease:F3}x ({width1610:F2} -> {width219:F2}).");
        Require(width169 > width1610 * 1.08f,
            $"Minimap frustum at 16:9 ({width169:F2}) must be wider than 16:10 ({width1610:F2}).");
        Require(MathF.Abs(height219 - height1610) < 0.5f && MathF.Abs(height169 - height1610) < 0.5f,
            $"Minimap frustum vertical height must stay constant under expand: {height1610:F2}, {height169:F2}, {height219:F2}.");
    }

    private static float MeasureMinimapFrustumWidth(
        Node3D scene,
        StaticGeometry geometry,
        UnitCatalog catalog,
        int vpWidth,
        int vpHeight,
        Vector3 focus,
        out float frustumHeight)
    {
        SubViewport sv = new() { Size = new Vector2I(vpWidth, vpHeight), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        scene.AddChild(sv);
        try
        {
            Camera3D cam = new() { Projection = Camera3D.ProjectionType.Orthogonal, Size = 160f, Current = true };
            sv.AddChild(cam);
            CameraRig rig = new(cam, focus, 160f, geometry.HeightMap.Width, geometry.HeightMap.Depth);
            TacticalMinimap minimap = new() { Size = new Vector2(TacticalMinimap.DefaultWidgetSize, TacticalMinimap.DefaultWidgetSize) };
            sv.AddChild(minimap);
            minimap.Initialize(geometry, rig, cam, session: null, unitViews: null, contactViews: null, catalog: catalog);

            Span<Vector2> corners = stackalloc Vector2[8];
            int count = minimap.ComputeClippedFrustum(corners);
            Require(count >= 3, $"Expected frustum polygon on minimap, got {count} vertices.");
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                if (corners[i].X < minX) minX = corners[i].X;
                if (corners[i].X > maxX) maxX = corners[i].X;
                if (corners[i].Y < minZ) minZ = corners[i].Y;
                if (corners[i].Y > maxZ) maxZ = corners[i].Y;
            }
            frustumHeight = maxZ - minZ;
            return maxX - minX;
        }
        finally
        {
            scene.RemoveChild(sv);
            sv.QueueFree();
        }
    }

    private static void CheckEdgeInputPickingAndProjectionAcrossAspects(Node3D scene)
    {
        HeightMap map = BattleScenario.CreateMap();
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");

        // Exercise real Root Window stretch transforms (canvas_items + expand + 21:9 cap + Scaling3DScale)
        Window root = scene.GetWindow();
        Vector2I origRootSize = root.Size;
        Vector2I origContentSize = root.ContentScaleSize;
        Window.ContentScaleAspectEnum origAspect = root.ContentScaleAspect;
        float origScale = root.Scaling3DScale;

        try
        {
            // Verify window <-> viewport transform roundtrip on wide 21:9 and capped 32:9 window
            (int WinW, int WinH, float Scale3D)[] rootConfigs =
            [
                (3440, 1440, 0.86f),
                (5120, 1440, 0.50f)
            ];

            foreach ((int winW, int winH, float scale3D) in rootConfigs)
            {
                root.Size = new Vector2I(winW, winH);
                ViewportStretchSpec spec = SettingsPanel.ApplyWindowStretchCap(root, new Vector2I(winW, winH));
                root.Scaling3DScale = scale3D;

                Transform2D screenXform = root.GetScreenTransform();
                Vector2 visibleSize = root.GetVisibleRect().Size;

                // Simulated raw OS mouse clicks near left & right active viewport boundaries
                Vector2 leftLogicalTarget = new(42f, visibleSize.Y * 0.5f);
                Vector2 rightLogicalTarget = new(visibleSize.X - 42f, visibleSize.Y * 0.5f);

                Vector2 leftWindowPixel = screenXform * leftLogicalTarget;
                Vector2 rightWindowPixel = screenXform * rightLogicalTarget;
                if (spec.UseAspectCapKeep)
                {
                    Require(leftWindowPixel.X > spec.CenteredOffsetX,
                        $"32:9 left click pixel {leftWindowPixel.X:F1} must fall inside pillarbox origin > {spec.CenteredOffsetX:F1}.");
                    Require(rightWindowPixel.X < winW - spec.CenteredOffsetX,
                        $"32:9 right click pixel {rightWindowPixel.X:F1} must stay inside pillarbox bound < {winW - spec.CenteredOffsetX:F1}.");
                }

                Vector2 mappedBackLeft = screenXform.AffineInverse() * leftWindowPixel;
                Vector2 mappedBackRight = screenXform.AffineInverse() * rightWindowPixel;
                Require(mappedBackLeft.DistanceTo(leftLogicalTarget) < 0.01f &&
                        mappedBackRight.DistanceTo(rightLogicalTarget) < 0.01f,
                    "Window screen-to-canvas transform roundtrip drifted.");

                // Push raw OS-window-space mouse events through root Window.PushInput and confirm Stretch transform maps them accurately
                List<Vector2> rootCaptured = [];
                EdgeInputSpy rootSpy = new(rootCaptured);
                scene.AddChild(rootSpy);
                try
                {
                    using InputEventMouseButton rootLeft = new()
                    {
                        ButtonIndex = MouseButton.Left,
                        Pressed = true,
                        Position = leftWindowPixel,
                        GlobalPosition = leftWindowPixel
                    };
                    root.PushInput(rootLeft, inLocalCoords: false);

                    using InputEventMouseButton rootRight = new()
                    {
                        ButtonIndex = MouseButton.Right,
                        Pressed = true,
                        Position = rightWindowPixel,
                        GlobalPosition = rightWindowPixel
                    };
                    root.PushInput(rootRight, inLocalCoords: false);

                    Require(rootCaptured.Count == 2 &&
                            rootCaptured[0].DistanceTo(leftLogicalTarget) < 1.5f &&
                            rootCaptured[1].DistanceTo(rightLogicalTarget) < 1.5f,
                        $"Root Window PushInput shifted scaled edge coords for {winW}x{winH}: left {rootCaptured.ElementAtOrDefault(0)} vs {leftLogicalTarget}, right {rootCaptured.ElementAtOrDefault(1)} vs {rightLogicalTarget}.");
                }
                finally
                {
                    scene.RemoveChild(rootSpy);
                    rootSpy.QueueFree();
                }
            }

            // Exercise edge unit selection, ground picking, and badge projection in both Orthographic and Perspective
            Vector2I[] aspectSizes = [new Vector2I(1440, 900), new Vector2I(1920, 1080), new Vector2I(2150, 900)];
            Camera3D.ProjectionType[] projections = [Camera3D.ProjectionType.Orthogonal, Camera3D.ProjectionType.Perspective];

            foreach (Vector2I vpSize in aspectSizes)
            {
                foreach (Camera3D.ProjectionType proj in projections)
                {
                    SubViewport sv = new()
                    {
                        Size = vpSize,
                        Scaling3DScale = 0.50f,
                        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
                    };
                    scene.AddChild(sv);
                    Node3D worldRoot = new();
                    sv.AddChild(worldRoot);
                    UnitViewSet? unitViews = null;
                    ContactViewSet? contactViews = null;
                    try
                    {
                        Camera3D cam = new() { Current = true };
                        worldRoot.AddChild(cam);
                        Vector3 centerFocus = new(map.Width * 0.5f, map.GetHeight(map.Width * 0.5f, map.Depth * 0.5f), map.Depth * 0.5f);
                        CameraRig rig = new(cam, centerFocus, 160f, map.Width, map.Depth);
                        rig.SetPerspective(proj == Camera3D.ProjectionType.Perspective);

                        // Ray-ground intersection at far left (x=45) and far right (x=vpSize.X-45) screen edges
                        Vector2 leftPick = new(45f, vpSize.Y * 0.5f);
                        Vector2 rightPick = new(vpSize.X - 45f, vpSize.Y * 0.5f);
                        Vector3 leftWorld = RaycastHorizontalPlane(cam, leftPick, centerFocus.Y);
                        Vector3 rightWorld = RaycastHorizontalPlane(cam, rightPick, centerFocus.Y);
                        Require(leftWorld.X < rightWorld.X - 100f,
                            $"Expected left edge ray ({leftWorld.X:F1}) < right edge ray ({rightWorld.X:F1}) on {vpSize} ({proj}).");

                        // Verify UnprojectPosition round-trips ground picks back to exact screen edge coordinates
                        Vector2 unprojLeft = cam.UnprojectPosition(leftWorld);
                        Vector2 unprojRight = cam.UnprojectPosition(rightWorld);
                        Require(unprojLeft.DistanceTo(leftPick) < 0.5f && unprojRight.DistanceTo(rightPick) < 0.5f,
                            $"Ray/Unproject mismatch at {vpSize} ({proj}): left {unprojLeft} vs {leftPick}, right {unprojRight} vs {rightPick}.");

                        // Verify Godot PushInput delivers transformed mouse events at far edges
                        List<Vector2> capturedPositions = [];
                        EdgeInputSpy spy = new(capturedPositions);
                        sv.AddChild(spy);
                        using InputEventMouseButton leftClick = new()
                        {
                            ButtonIndex = MouseButton.Left,
                            Pressed = true,
                            Position = leftPick,
                            GlobalPosition = leftPick
                        };
                        sv.PushInput(leftClick, inLocalCoords: false);
                        using InputEventMouseButton rightClick = new()
                        {
                            ButtonIndex = MouseButton.Right,
                            Pressed = true,
                            Position = rightPick,
                            GlobalPosition = rightPick
                        };
                        sv.PushInput(rightClick, inLocalCoords: false);
                        Require(capturedPositions.Count == 2 &&
                                capturedPositions[0].DistanceTo(leftPick) < 0.1f &&
                                capturedPositions[1].DistanceTo(rightPick) < 0.1f,
                            $"PushInput lost/shifted edge coordinates on {vpSize} ({proj}).");

                        // Place units at far left and far right visible edges and test box selection + badges
                        unitViews = new UnitViewSet(worldRoot, map, catalog);
                        contactViews = new ContactViewSet(worldRoot, map);
                        ClientReplica replica = new();
                        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
                        OwnUnitObservation uLeft = new(new EntityId(10), 0, new NumericsVector2(leftWorld.X, leftWorld.Z), 0);
                        OwnUnitObservation uRight = new(new EntityId(11), 0, new NumericsVector2(rightWorld.X, rightWorld.Z), 0);
                        int snapLen = WireProtocol.WriteSnapshot(bytes, 1, 5, [uLeft, uRight], []);
                        Require(replica.TryApply(bytes.AsSpan(0, snapLen)), "Edge selection snapshot rejected.");
                        unitViews.Sync(replica, 5);

                        RoleBadgeOverlay badges = new(catalog);
                        sv.AddChild(badges);
                        badges.UpdateBadges(cam, cam.Size, unitViews, contactViews, replica, 5, ReadOnlySpan<Rect2>.Empty);
                        Require(!RoleBadgeOverlay.IsOffscreenOrOccluded(new Rect2(unprojLeft - new Vector2(20, 10), new Vector2(40, 20)), vpSize, ReadOnlySpan<Rect2>.Empty),
                            $"Left edge badge at {unprojLeft} must stay on screen for {vpSize} ({proj}).");
                        Require(!RoleBadgeOverlay.IsOffscreenOrOccluded(new Rect2(unprojRight - new Vector2(20, 10), new Vector2(40, 20)), vpSize, ReadOnlySpan<Rect2>.Empty),
                            $"Right edge badge at {unprojRight} must stay on screen for {vpSize} ({proj}).");

                        // Box-select unit at the far right expanded edge (> 1440 on 21:9) using SelectionController
                        SelectionController sel = new();
                        Vector2 rightUnitScreen = cam.UnprojectPosition(unitViews.Views[uRight.Id].Root.Position + Vector3.Up);
                        sel.BeginPress(rightUnitScreen - new Vector2(30, 30), shiftPressed: false, doubleClick: false, ReadOnlySpan<Rect2>.Empty);
                        sel.UpdateDrag(rightUnitScreen + new Vector2(30, 30));
                        SelectionReleaseKind release = sel.CompleteRelease(
                            cam, unitViews, contactViews, badges, rightUnitScreen + new Vector2(30, 30), vpSize, false, out _, out _);
                        Require(release == SelectionReleaseKind.BoxSelect && unitViews.Selected.Contains(uRight.Id),
                            $"Box selection at right edge {rightUnitScreen} failed for {vpSize} ({proj}).");
                    }
                    finally
                    {
                        unitViews?.Clear();
                        contactViews?.Clear();
                        scene.RemoveChild(sv);
                        sv.QueueFree();
                    }
                }
            }
        }
        finally
        {
            root.Scaling3DScale = origScale;
            root.ContentScaleAspect = origAspect;
            root.ContentScaleSize = origContentSize;
            root.Size = origRootSize;
        }
    }

    private static Vector3 RaycastHorizontalPlane(Camera3D cam, Vector2 screenPos, float planeY)
    {
        Vector3 origin = cam.ProjectRayOrigin(screenPos);
        Vector3 dir = cam.ProjectRayNormal(screenPos);
        Require(MathF.Abs(dir.Y) > 1e-4f, "Camera ray did not intersect horizontal plane.");
        float t = (planeY - origin.Y) / dir.Y;
        Require(t > 0f, $"Camera ray hit plane behind origin (t={t}).");
        return origin + dir * t;
    }

    private static void CheckAspectPreservingResizeAndDensityUpdates(Node3D scene)
    {
        Main main = scene as Main ?? throw new InvalidOperationException("Resize/density regression requires Main scene.");
        Window root = main.GetWindow();
        Vector2I origRootSize = root.Size;
        Vector2I origContentSize = root.ContentScaleSize;
        Window.ContentScaleAspectEnum origAspect = root.ContentScaleAspect;
        float origScale = root.Scaling3DScale;
        Viewport.Scaling3DModeEnum origMode = root.Scaling3DMode;
        Viewport.Msaa origMsaa = root.Msaa3D;
        DisplaySettings origSavedSettings = main.SettingsPanel.SavedConfig;
        DisplaySettings origEffectiveSettings = main.SettingsPanel.CurrentSettings;
        try
        {
            // Switch Main SettingsPanel to Auto/Auto so real SizeChanged and DpiChanged signal handlers drive updates
            DisplaySettings autoSettings = origEffectiveSettings with
            {
                RenderScale = RenderScalePreset.Auto,
                AntiAliasing = AntiAliasingPreset.Auto
            };
            main.SettingsPanel.Initialize(origSavedSettings, autoSettings, false, false, false, false, false, false);

            // Simulate 1x display (1440x900) through real SizeChanged signal only
            root.Size = new Vector2I(1440, 900);
            root.EmitSignal(Viewport.SignalName.SizeChanged);
            float scale1x = root.Scaling3DScale;
            float cachedHeight1x = main.CachedTerrainViewportHeightPx;

            // Simulate moving window to 2x Retina display (2880x1800) keeping 16:10 via SizeChanged + DpiChanged signals
            root.Size = new Vector2I(2880, 1800);
            root.EmitSignal(Viewport.SignalName.SizeChanged);
            float scaleAfterResize = root.Scaling3DScale;
            float cachedHeightAfterResize = main.CachedTerrainViewportHeightPx;

            // Reset scale temporarily to prove DpiChanged handler alone triggers full recalculation
            root.Scaling3DScale = 1.0f;
            root.EmitSignal(Window.SignalName.DpiChanged);
            float scaleAfterDpi = root.Scaling3DScale;
            float cachedHeightAfterDpi = main.CachedTerrainViewportHeightPx;

            // Logical visible height remains 900 while Auto render scale drops (~0.843) and Main's cached pixel height rises
            Require(MathF.Abs(root.GetVisibleRect().Size.Y - 900f) < 1f,
                $"Logical visible height must remain 900 on 16:10 density jump, got {root.GetVisibleRect().Size.Y}.");
            Require(Mathf.IsEqualApprox(scale1x, 1.0f) && scaleAfterResize < 0.88f && scaleAfterDpi < 0.88f,
                $"Main SizeChanged/DpiChanged handlers failed to update Auto render scale: {scale1x:F3} -> resize {scaleAfterResize:F3} -> dpi {scaleAfterDpi:F3}.");
            Require(cachedHeightAfterResize > cachedHeight1x * 1.4f && Mathf.IsEqualApprox(cachedHeightAfterDpi, cachedHeightAfterResize),
                $"Main CachedTerrainViewportHeightPx failed to track density updates: {cachedHeight1x:F1} -> {cachedHeightAfterResize:F1} (dpi {cachedHeightAfterDpi:F1}).");
        }
        finally
        {
            main.SettingsPanel.Initialize(origSavedSettings, origEffectiveSettings, false, false, false, false, false, false);
            root.Scaling3DScale = origScale;
            root.Scaling3DMode = origMode;
            root.Msaa3D = origMsaa;
            root.ContentScaleAspect = origAspect;
            root.ContentScaleSize = origContentSize;
            root.Size = origRootSize;
            main.OnWindowMetricsChanged();
        }
    }

    private sealed partial class EdgeInputSpy(List<Vector2> positions) : Node
    {
        public override void _Input(InputEvent @event)
        {
            if (@event is InputEventMouseButton { Pressed: true } mb)
            {
                positions.Add(mb.Position);
            }
        }
    }
}
