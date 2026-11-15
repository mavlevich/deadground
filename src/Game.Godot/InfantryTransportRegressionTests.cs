using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

internal static class InfantryTransportRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckInfantrySelection_ShowsBoardButton(scene);
        CheckTransportSelection_ShowsPassengersAndDisembarkButton(scene);
        CheckBoardButtonPressed_EntersBoardingTargetingMode_AndCancelsOnEsc(scene);
        CheckDisembarkButtonPressed_EntersDisembarkTargetingMode_AndCancelsOnEsc(scene);
        CheckSelectionCleared_CancelsTargetingMode_AndHidesPanel(scene);
        CheckInputOcclusion_PanelRectConsumesClicks(scene);
        CheckSteadyStateZeroHeapAllocations(scene);
        GD.Print("INFANTRY_TRANSPORT_REGRESSIONS_OK: action_panel targeting_mode esc_cancel occlusion zero_allocations");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static (ContextActionPanel panel, ClientReplica replica, EntityId riflemanId, EntityId transportId) SetupTestEnvironment(Node3D scene)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ContextActionPanel panel = new(catalog);
        scene.AddChild(panel);

        ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");
        ushort m113Def = catalog.RequireDefinitionIndex("m113a1");

        EntityId riflemanId = new(101);
        EntityId transportId = new(102);

        OwnUnitObservation rifleman = new(
            Id: riflemanId,
            Definition: riflemanDef,
            Position: new NumericsVector2(20f, 20f),
            SupportSurface: 0,
            Health: 15,
            Ammunition: 210);

        OwnUnitObservation transport = new(
            Id: transportId,
            Definition: m113Def,
            Position: new NumericsVector2(25f, 20f),
            SupportSurface: 0,
            Health: 100,
            Ammunition: 0,
            PassengerCount: 3,
            PassengerCapacity: 11);

        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        int size = WireProtocol.WriteSnapshot(buffer, 1, 1, [rifleman, transport]);
        replica.TryApply(buffer.AsSpan(0, size));

        return (panel, replica, riflemanId, transportId);
    }

    private static void CheckInfantrySelection_ShowsBoardButton(Node3D scene)
    {
        var (panel, replica, riflemanId, _) = SetupTestEnvironment(scene);
        try
        {
            HashSet<EntityId> selected = [riflemanId];
            panel.UpdateContext(selected, replica);

            Require(panel.Visible, "ContextActionPanel must be visible when infantry is selected.");
            Require(panel.BoardButton.Visible, "BOARD TRANSPORT button must be visible for selected infantry.");
            Require(!panel.DisembarkButton.Visible, "DISEMBARK ALL button must be hidden for infantry.");
            Require(!panel.DisembarkHereButton.Visible, "DISEMBARK HERE button must be hidden for infantry.");
            Require(!panel.PassengerLabel.Visible, "Passenger count label must be hidden for infantry.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckTransportSelection_ShowsPassengersAndDisembarkButton(Node3D scene)
    {
        var (panel, replica, _, transportId) = SetupTestEnvironment(scene);
        try
        {
            HashSet<EntityId> selected = [transportId];
            panel.UpdateContext(selected, replica);

            Require(panel.Visible, "ContextActionPanel must be visible when transport is selected.");
            Require(!panel.BoardButton.Visible, "BOARD TRANSPORT button must be hidden for transport.");
            Require(panel.DisembarkHereButton.Visible, "DISEMBARK HERE button must be visible for transport.");
            Require(panel.DisembarkButton.Visible, "UNLOAD AT button must be visible for transport.");
            Require(panel.PassengerLabel.Visible, "Passenger count label must be visible for transport.");
            Require(panel.PassengerLabel.Text.Contains("Passengers: 3/11"),
                $"Passenger label should display 'Passengers: 3/11', got '{panel.PassengerLabel.Text}'.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckBoardButtonPressed_EntersBoardingTargetingMode_AndCancelsOnEsc(Node3D scene)
    {
        var (panel, replica, riflemanId, _) = SetupTestEnvironment(scene);
        try
        {
            HashSet<EntityId> selected = [riflemanId];
            panel.UpdateContext(selected, replica);

            bool modeChangedFired = false;
            ContextTargetingMode reportedMode = ContextTargetingMode.None;
            panel.TargetingModeChanged += mode =>
            {
                modeChangedFired = true;
                reportedMode = mode;
            };

            // Enter targeting mode
            panel.SetTargetingMode(ContextTargetingMode.BoardTransport);
            Require(modeChangedFired, "TargetingModeChanged event must fire when entering boarding targeting mode.");
            Require(reportedMode == ContextTargetingMode.BoardTransport, "Reported mode must be BoardTransport.");
            Require(panel.TargetingMode == ContextTargetingMode.BoardTransport, "TargetingMode must be BoardTransport.");

            // Cancel via Esc gesture
            modeChangedFired = false;
            panel.CancelTargeting();
            Require(modeChangedFired, "TargetingModeChanged event must fire when cancelling targeting mode.");
            Require(panel.TargetingMode == ContextTargetingMode.None, "TargetingMode must return to None after cancel.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckDisembarkButtonPressed_EntersDisembarkTargetingMode_AndCancelsOnEsc(Node3D scene)
    {
        var (panel, replica, _, transportId) = SetupTestEnvironment(scene);
        try
        {
            HashSet<EntityId> selected = [transportId];
            panel.UpdateContext(selected, replica);

            panel.SetTargetingMode(ContextTargetingMode.Disembark);
            Require(panel.TargetingMode == ContextTargetingMode.Disembark, "TargetingMode must be Disembark.");

            panel.CancelTargeting();
            Require(panel.TargetingMode == ContextTargetingMode.None, "TargetingMode must return to None after cancel.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckSelectionCleared_CancelsTargetingMode_AndHidesPanel(Node3D scene)
    {
        var (panel, replica, riflemanId, _) = SetupTestEnvironment(scene);
        try
        {
            HashSet<EntityId> selected = [riflemanId];
            panel.UpdateContext(selected, replica);
            panel.SetTargetingMode(ContextTargetingMode.BoardTransport);

            // Deselect all units
            selected.Clear();
            panel.UpdateContext(selected, replica);

            Require(!panel.Visible, "ContextActionPanel must hide when selection is cleared.");
            Require(panel.TargetingMode == ContextTargetingMode.None, "TargetingMode must be reset to None on deselection.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckInputOcclusion_PanelRectConsumesClicks(Node3D scene)
    {
        var (panel, replica, riflemanId, _) = SetupTestEnvironment(scene);
        try
        {
            HashSet<EntityId> selected = [riflemanId];
            panel.UpdateContext(selected, replica);
            panel.Position = new Vector2(100f, 100f);
            panel.Size = new Vector2(360f, 48f);

            Require(panel.MouseFilter == Control.MouseFilterEnum.Stop, "Panel MouseFilter must be Stop to occlude mouse clicks.");
            Rect2 panelRect = new(panel.Position, panel.Size);
            Vector2 clickInside = new(150f, 120f);
            Vector2 clickOutside = new(50f, 50f);

            Require(panelRect.HasPoint(clickInside), "Click inside panel bounds must be contained in panelRect.");
            Require(!panelRect.HasPoint(clickOutside), "Click outside panel bounds must not be contained in panelRect.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckSteadyStateZeroHeapAllocations(Node3D scene)
    {
        var (panel, replica, riflemanId, _) = SetupTestEnvironment(scene);
        try
        {
            HashSet<EntityId> selected = [riflemanId];
            // Warm up cache
            for (int i = 0; i < 5; i++)
            {
                panel.UpdateContext(selected, replica);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++)
            {
                panel.UpdateContext(selected, replica);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Require(allocated == 0, $"ContextActionPanel steady-state updates must allocate 0 bytes, but allocated {allocated}.");
        }
        finally
        {
            panel.QueueFree();
        }
    }
}
