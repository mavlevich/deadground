using DeadGround.Engine;
using Godot;

namespace DeadGround.Client;

internal static class OrderPreviewProbe
{
    public static async System.Threading.Tasks.Task RunCapture(Main main, string outputDir)
    {
        System.IO.Directory.CreateDirectory(outputDir);
        Camera3D camera = main.Camera;
        Vector2 viewport = main.GetViewport().GetVisibleRect().Size;

        // Select the first own unit
        if (main.Session.Replica.OwnUnits.Length > 0)
        {
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(main.Session.Replica.OwnUnits[0].Id);
            main.UnitViews.UpdateSelectionVisuals();
        }

        Vector2 centerScreen = viewport * 0.5f;

        // 1. Valid move preview
        main.OrderCursorOverlay.SetPreviewState(
            visible: true,
            validity: DestinationValidity.Valid,
            reason: DestinationBlockReason.None,
            validCount: 1,
            totalCount: 1);
        main.OrderCursorOverlay.UpdatePosition(centerScreen, viewport);
        Vector3 groundPos = new(main.CameraRig.Focus.X + 8f, 0.12f, main.CameraRig.Focus.Z + 8f);
        main.DestinationMarker.Show(groundPos, OrderMarkerVisualState.PathFound);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_preview_valid.png"));

        // 2. Water blocked preview
        main.DestinationMarker.Clear();
        main.OrderCursorOverlay.SetPreviewState(
            visible: true,
            validity: DestinationValidity.Blocked,
            reason: DestinationBlockReason.Water,
            validCount: 0,
            totalCount: 1);
        main.OrderCursorOverlay.UpdatePosition(centerScreen, viewport);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_preview_water.png"));

        // 3. Dense forest blocked preview
        main.OrderCursorOverlay.SetPreviewState(
            visible: true,
            validity: DestinationValidity.Blocked,
            reason: DestinationBlockReason.DenseForest,
            validCount: 0,
            totalCount: 1);
        main.OrderCursorOverlay.UpdatePosition(centerScreen, viewport);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_preview_forest.png"));

        // 4. Building blocked preview
        main.OrderCursorOverlay.SetPreviewState(
            visible: true,
            validity: DestinationValidity.Blocked,
            reason: DestinationBlockReason.Building,
            validCount: 0,
            totalCount: 1);
        main.OrderCursorOverlay.UpdatePosition(centerScreen, viewport);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_preview_building.png"));

        // 5. Adjusted target with ghost preview ring
        main.OrderCursorOverlay.SetPreviewState(
            visible: true,
            validity: DestinationValidity.Adjusted,
            reason: DestinationBlockReason.Building,
            validCount: 1,
            totalCount: 1);
        main.OrderCursorOverlay.UpdatePosition(centerScreen, viewport);
        Vector3 ghostPos = new(groundPos.X + 2.5f, 0.12f, groundPos.Z);
        main.DestinationMarker.ShowGhost(ghostPos);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_preview_adjusted.png"));
        main.DestinationMarker.HideGhost();

        // 6. Mixed group selection preview
        main.OrderCursorOverlay.SetPreviewState(
            visible: true,
            validity: DestinationValidity.Valid,
            reason: DestinationBlockReason.None,
            validCount: 2,
            totalCount: 5);
        main.OrderCursorOverlay.UpdatePosition(centerScreen, viewport);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_preview_mixed.png"));

        // 7. Destination marker states: Sent (amber), Processing (cyan), Failed (red), Partial (orange)
        main.OrderCursorOverlay.SetPreviewState(false, DestinationValidity.Valid, DestinationBlockReason.None, 0, 0);

        main.DestinationMarker.Show(groundPos, OrderMarkerVisualState.Sent);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_marker_sent.png"));

        main.DestinationMarker.SetState(OrderMarkerVisualState.Processing);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_marker_processing.png"));

        main.DestinationMarker.SetState(OrderMarkerVisualState.Failed);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_marker_failed.png"));

        main.DestinationMarker.SetState(OrderMarkerVisualState.Partial);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_marker_partial.png"));

        // 8. Facing arrow preview and gesture progression
        main.DestinationMarker.Show(groundPos, OrderMarkerVisualState.Processing);
        main.DestinationMarker.ShowFacingArrow(groundPos, 0.7853982f);
        await WaitFrames(main, 3);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "order_facing_arrow.png"));

        // Capture multi-angle sequence for animation
        float[] angles = [0f, 0.5235988f, 1.0471976f, 1.5707963f, 2.0943951f, 2.6179939f, 3.1415927f];
        for (int i = 0; i < angles.Length; i++)
        {
            main.DestinationMarker.ShowFacingArrow(groundPos, angles[i]);
            await WaitFrames(main, 2);
            CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, $"facing_seq_{i:D2}.png"));
        }
        main.DestinationMarker.HideFacingArrow();

        GD.Print($"ORDER_PREVIEW_CAPTURE_OK: {outputDir}");
        main.GetTree().Quit(0);
    }

    private static async System.Threading.Tasks.Task WaitFrames(Main main, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
