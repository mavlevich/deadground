using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

/// <summary>
/// Headless client regression tests for CH-47D helicopter presentation, altitude elevation,
/// rotor blur disc switching, ramp articulation, enemy helicopter contacts, and zero-allocation
/// ContextActionPanel helicopter controls.
/// </summary>
internal static class HelicopterFlightRegressionTests
{
    public static void Run(Node3D scene)
    {
        CheckCh47dUnitViewRigAndAnimation(scene);
        CheckCh47dCargoVisualsAndZoneCircle(scene);
        CheckHelicopterSoundAndZeroAllocController(scene);
        CheckCh47dWreckHidesRotorBlurDiscs(scene);
        CheckEnemyHelicopterContactView(scene);
        CheckContextActionPanelHelicopterControls(scene);
        CheckBoardThenTakeOffInPlaceViaContextPanel(scene);
        GD.Print("HELICOPTER_FLIGHT_REGRESSIONS_OK 7/7 passed: ch47d_rig_animation ch47d_cargo_visuals_zone_circle helicopter_sound_zero_alloc ch47d_wreck_no_blur enemy_helicopter_contact context_panel_zero_alloc board_then_takeoff_in_place");
    }

    private static void RequireNoShadowCasting(Node node, string label)
    {
        if (node is GeometryInstance3D geom)
        {
            Require(geom.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                $"{label} mesh '{geom.Name}' must have CastShadow=Off.");
        }
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            RequireNoShadowCasting(node.GetChild(i), label);
        }
    }

    private static void RequireNoYellowRotorTipMaterial(Node node, string label)
    {
        if (node is MeshInstance3D mi && mi.Mesh is Mesh mesh)
        {
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                Material? active = mi.GetActiveMaterial(s);
                if (active is BaseMaterial3D bm)
                {
                    Require(!(bm.AlbedoColor.R > 0.65f && bm.AlbedoColor.G > 0.50f && bm.AlbedoColor.B < 0.30f),
                        $"{label} surface {s} must not retain bright yellow tip material.");
                }
            }
        }
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            RequireNoYellowRotorTipMaterial(node.GetChild(i), label);
        }
    }

    private static void CheckCh47dUnitViewRigAndAnimation(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitView view = new("ch47d", faction: "us", classification: "helicopter");
            parent.AddChild(view.Root);

            Require(view.HasVehicleModel, "CH-47D must instantiate its authored GLB visual.");
            Require(view.IsHelicopter, "CH-47D UnitView must detect helicopter capability.");
            Require(view.LodCount == 3, $"CH-47D expected 3 LODs, got {view.LodCount}.");

            Node3D? lod0 = view.GetLod(0);
            Require(lod0 is not null, "CH-47D LOD0 must be loaded.");
            Node3D? frontRotor = lod0!.FindChild("FrontRotor", recursive: true, owned: false) as Node3D;
            Node3D? rearRotor = lod0.FindChild("RearRotor", recursive: true, owned: false) as Node3D;
            Require(frontRotor is not null, "Missing FrontRotor node.");
            Require(rearRotor is not null, "Missing RearRotor node.");
            RequireNoYellowRotorTipMaterial(frontRotor!, "FrontRotor");
            RequireNoYellowRotorTipMaterial(rearRotor!, "RearRotor");
            Node3D? frontBlur = lod0.FindChild("FrontRotorBlur", recursive: true, owned: false) as Node3D;
            Node3D? rearBlur = lod0.FindChild("RearRotorBlur", recursive: true, owned: false) as Node3D;
            Require(frontBlur is not null, "Missing FrontRotorBlur node.");
            Require(rearBlur is not null, "Missing RearRotorBlur node.");
            RequireNoShadowCasting(frontBlur!, "FrontRotorBlur");
            RequireNoShadowCasting(rearBlur!, "RearRotorBlur");
            Require(lod0.FindChild("RearRamp", recursive: true, owned: false) is Node3D, "Missing RearRamp node.");

            // Landed initial state: solid rotors visible, blur discs hidden, rear ramp lowered
            Require(!view.AreRotorBlursVisible, "Landed CH-47D should hide rotor blur discs initially.");
            Require(!view.IsAltitudeTetherVisible, "Landed CH-47D should hide altitude tether.");
            Require(view.RearRampAngleDeg > 35f, $"Landed CH-47D ramp should be lowered, got {view.RearRampAngleDeg:F1} deg.");

            // Transition to airborne flight at 55m AGL with nose-down pitch and roll bank
            view.UpdatePosition(new Vector3(200f, 2f, 200f));
            view.SetHullHeading(0.5f);
            view.UpdateFlightState(55.0f, -0.15f, 0.18f, FlightPhase.Airborne);
            for (int f = 0; f < 120; f++)
            {
                view.UpdateEffects(0.016);
            }

            Require(MathF.Abs(view.Model.Position.Y - 55.0f) < 0.05f,
                $"Airborne CH-47D Model.Position.Y expected ~55.0, got {view.Model.Position.Y:F2}.");
            Require(view.IsAltitudeTetherVisible, "Airborne CH-47D must display vertical altitude tether.");
            Require(view.IsGroundFootprintVisible, "Airborne CH-47D must display ground projection footprint ring.");
            Require(view.AreRotorBlursVisible, "Airborne CH-47D at full RPM must display rotor blur discs.");
            Require(view.RearRampAngleDeg < 2.0f, $"Airborne CH-47D ramp must retract to 0 deg, got {view.RearRampAngleDeg:F1} deg.");
            Require(!view.IsDownwashEmitting, "High-altitude (55m) CH-47D should not emit terrain downwash dust.");

            // Steady-state UpdateEffects allocation check
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                view.UpdateEffects(0.016);
            }
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Require(allocDelta == 0, $"CH-47D steady-state UpdateEffects must allocate 0 bytes, got {allocDelta}.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckCh47dCargoVisualsAndZoneCircle(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitView heli = new("ch47d", faction: "us", classification: "helicopter");
            UnitView m113 = new("m113a1", faction: "us", classification: "apc");
            parent.AddChild(heli.Root);
            parent.AddChild(m113.Root);

            // Fast-rope hover at 18m AGL: downwash dust active, twin ropes visible, rear ramp opens
            heli.UpdatePosition(new Vector3(250f, 2f, 690f));
            heli.UpdateFlightState(18.0f, 0f, 0f, FlightPhase.Airborne, FlightStateFlags.FastRopeActive, hasSlingLoad: false);
            for (int f = 0; f < 90; f++)
                heli.UpdateEffects(0.016);

            Require(heli.AreFastRopesVisible, "Fast-rope active CH-47D must display fast ropes.");
            Require(heli.IsDownwashEmitting, "Low-altitude (18m) hover CH-47D must emit rotor downwash dust.");
            Require(heli.RearRampAngleDeg > 35f, $"Fast-rope CH-47D ramp must lower to ~42 deg, got {heli.RearRampAngleDeg:F1} deg.");

            // External sling harness active + slung M113A1 elevated cleanly
            heli.UpdateFlightState(35.0f, 0f, 0f, FlightPhase.Airborne, FlightStateFlags.None, hasSlingLoad: true);
            m113.UpdateSlungState(25.0f, isSlung: true);
            m113.UpdateEffects(0.016);

            Require(!heli.AreFastRopesVisible, "Fast ropes must hide when fast-rope flag clears.");
            Require(heli.IsSlingHarnessVisible, "CH-47D with sling load must display 4-leg sling harness.");
            Require(m113.IsSlung && MathF.Abs(m113.Model.Position.Y - 25.0f) < 0.05f,
                $"Slung M113A1 must elevate to 25.0m, got {m113.Model.Position.Y:F2}.");

            // Terrain-draped LZ / drop-zone preview circle (green valid vs red blocked)
            OrderDestinationView destView = new();
            parent.AddChild(destView);
            HeightMap map = HelicopterPlaygroundScenario.CreateHeightMap();
            destView.ShowZoneCircle(map, 110f, 690f, 10.0f, valid: true);
            Require(destView.IsZoneCircleVisible && destView.IsZoneCircleValid, "Zone preview circle must show valid green ring.");
            destView.ShowZoneCircle(map, 400f, 512f, 10.0f, valid: false);
            Require(destView.IsZoneCircleVisible && !destView.IsZoneCircleValid, "Zone preview circle must switch to red blocked ring.");
            destView.HideZoneCircle();
            Require(!destView.IsZoneCircleVisible, "HideZoneCircle must hide preview ring.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckHelicopterSoundAndZeroAllocController(Node3D scene)
    {
        float[] loop = HelicopterSound.RenderRotorLoop(47);
        Require(loop.Length > 40000, $"Expected >40k rotor loop samples, got {loop.Length}.");
        float peak = 0f;
        for (int i = 0; i < loop.Length; i++)
        {
            Require(float.IsFinite(loop[i]), "Rotor audio sample must be finite.");
            float a = MathF.Abs(loop[i]);
            if (a > peak) peak = a;
        }
        Require(peak > 0.5f && peak <= 1.0f, $"Rotor audio loop peak out of range: {peak:F3}.");

        // Disabled headless controller must allocate 0 bytes per frame
        Node3D holder = new();
        scene.AddChild(holder);
        try
        {
            HelicopterAudioController audio = new(holder, enabled: false);
            ClientReplica replica = new();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                audio.Update(replica, Vector3.Zero, 180f, 0.016);
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(delta == 0, $"HelicopterAudioController steady-state update must allocate 0 bytes, got {delta}.");
        }
        finally
        {
            scene.RemoveChild(holder);
            holder.QueueFree();
        }
    }

    private static void CheckCh47dWreckHidesRotorBlurDiscs(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            WreckView wreck = new("ch47d", faction: "us", initialCameraSize: 80f);
            parent.AddChild(wreck.Root);
            Require(wreck.HasVehicleModel, "CH-47D wreck must instantiate its authored GLB visual.");

            Node3D? frontBlur = wreck.Model.FindChild("FrontRotorBlur", recursive: true, owned: false) as Node3D;
            Node3D? rearBlur = wreck.Model.FindChild("RearRotorBlur", recursive: true, owned: false) as Node3D;
            Require(frontBlur is not null && !frontBlur.Visible, "CH-47D wreck must hide FrontRotorBlur.");
            Require(rearBlur is not null && !rearBlur.Visible, "CH-47D wreck must hide RearRotorBlur.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckEnemyHelicopterContactView(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitCatalog catalog = ColdWarContent.LoadCatalog();
            ushort ch47Def = catalog.RequireDefinitionIndex("ch47d");
            ContactView contactView = new(() => "us", def => def < catalog.Units.Length ? catalog.Units[def].Id : null);
            parent.AddChild(contactView.Root);

            ContactObservation obs = new(
                new ContactId(55),
                ContactState.Current,
                ContactClassification.Helicopter,
                ch47Def,
                new NumericsVector2(420f, 510f),
                LastSeenTick: 20,
                TurretYaw: 0f,
                HullHeading: 1.0f,
                SupportSurface: 0,
                Altitude: 28.0f,
                FlightPhase: FlightPhase.Airborne);

            contactView.Update(obs, obs.Position, groundY: 2.0f, isSelected: false, obs.TurretYaw, obs.HullHeading, obs.Altitude);

            Require(contactView.HasEnemyModel, "Enemy helicopter contact must instantiate a visible model.");
            Require(contactView.CurrentModelClassification == ContactClassification.Helicopter,
                "Contact classification must match Helicopter.");
            Require(MathF.Abs(contactView.EnemyModel!.Position.Y - 28.0f) < 0.05f,
                $"Enemy helicopter model elevation expected 28.0, got {contactView.EnemyModel.Position.Y:F2}.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckContextActionPanelHelicopterControls(Node3D scene)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ushort ch47Def = catalog.RequireDefinitionIndex("ch47d");
        ContextActionPanel panel = new(catalog);
        scene.AddChild(panel);
        try
        {
            ClientReplica replica = new();
            OwnUnitObservation heliLanded = new(
                new EntityId(1),
                ch47Def,
                new NumericsVector2(160f, 160f),
                0,
                Health: 160,
                Ammunition: 1200,
                PassengerCount: 2,
                PassengerCapacity: 24,
                Altitude: 0f,
                FlightPhase: FlightPhase.Ground,
                AltitudeBand: AltitudeBand.High,
                HasFlightCapability: true);

            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            int len = WireProtocol.WriteSnapshot(snapBuf, 1, 10, [heliLanded]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len)), "Helicopter snapshot must apply.");

            HashSet<EntityId> selected = [heliLanded.Id];
            panel.UpdateContext(selected, replica);

            Require(panel.Visible, "ContextActionPanel must be visible when CH-47D is selected.");
            Require(panel.FlightToggleButton.Visible, "FlightToggleButton must be visible for helicopter.");
            Require(panel.LandAtButton.Visible, "LandAtButton must be visible for helicopter.");
            Require(panel.AltitudeBandButton.Visible, "AltitudeBandButton must be visible for helicopter.");
            Require(panel.FastRopeButton.Visible, "FastRopeButton must be visible for CH-47D with passengers.");
            Require(panel.SlingButton.Visible, "SlingButton must be visible for CH-47D with sling capacity.");
            Require(panel.FlightToggleButton.Text.Contains("TAKE OFF", StringComparison.Ordinal),
                $"Expected TAKE OFF text while landed, got '{panel.FlightToggleButton.Text}'.");
            Require(panel.AltitudeBandButton.Text.Contains("HIGH", StringComparison.Ordinal),
                $"Expected HIGH altitude text, got '{panel.AltitudeBandButton.Text}'.");
            Require(panel.FastRopeButton.Text.Contains("FAST-ROPE", StringComparison.Ordinal),
                $"Expected FAST-ROPE text, got '{panel.FastRopeButton.Text}'.");
            Require(panel.SlingButton.Text.Contains("SLING PICKUP", StringComparison.Ordinal),
                $"Expected SLING PICKUP text when unloaded, got '{panel.SlingButton.Text}'.");

            // Steady-state zero-allocation check
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                panel.UpdateContext(selected, replica);
            }
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Require(allocDelta == 0, $"ContextActionPanel helicopter steady-state must allocate 0 bytes, got {allocDelta}.");
        }
        finally
        {
            scene.RemoveChild(panel);
            panel.QueueFree();
        }
    }

    private static void CheckBoardThenTakeOffInPlaceViaContextPanel(Node3D scene)
    {
        StaticGeometry geometry = HelicopterPlaygroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = HelicopterPlaygroundScenario.CreateSimulation(geometry, catalog, matchSeed: 2024UL);
        using LocalSession session = new(sim, player: 1);

        Node3D parent = new();
        scene.AddChild(parent);
        ContextActionPanel panel = new(catalog);
        scene.AddChild(panel);
        try
        {
            UnitViewSet views = new(parent, geometry.HeightMap, catalog, () => "us", geometry);
            for (int i = 0; i < 10; i++)
                session.Advance(1d / Simulation.TickRate);
            views.Sync(session.Replica, session.RenderTick);

            EntityId alphaId = default;
            EntityId escort1 = default;
            EntityId escort2 = default;
            foreach (OwnUnitObservation obs in session.Replica.OwnUnits)
            {
                if (obs.HasFlightCapability && alphaId == default)
                    alphaId = obs.Id;
                else if (!obs.HasFlightCapability && obs.ContainerId == default)
                {
                    if (escort1 == default) escort1 = obs.Id;
                    else if (escort2 == default) escort2 = obs.Id;
                }
            }

            Require(alphaId != default && escort1 != default && escort2 != default, "Expected Chinook Alpha and 2 escort riflemen.");
            Require(session.BoardTransport(escort1, alphaId), "Escort 1 boarding order must succeed.");
            Require(session.BoardTransport(escort2, alphaId), "Escort 2 boarding order must succeed.");

            for (int i = 0; i < 200; i++)
                session.Advance(1d / Simulation.TickRate);
            views.Sync(session.Replica, session.RenderTick);

            Require(session.Replica.TryGetOwnUnit(alphaId, out OwnUnitObservation boardedObs), "Chinook Alpha must be observable.");
            Require(boardedObs.PassengerCount == 4, $"Expected 4 passengers after boarding, got {boardedObs.PassengerCount}.");
            Require(boardedObs.FlightPhase == FlightPhase.Ground, "Chinook Alpha must still be on ground before takeoff.");

            views.Selected.Clear();
            views.Selected.Add(alphaId);
            panel.UpdateContext(views.Selected, session.Replica);
            Require(panel.FlightToggleButton.Visible && !panel.FlightToggleButton.Disabled, "TakeOff button must be visible and enabled.");
            Require(panel.FlightToggleButton.Text.Contains("TAKE OFF", StringComparison.Ordinal),
                $"Expected TAKE OFF button label, got '{panel.FlightToggleButton.Text}'.");

            panel.HelicopterToggleFlightRequested += () =>
            {
                if (session.Replica.TryGetOwnUnit(alphaId, out OwnUnitObservation cur) && cur.HasFlightCapability)
                {
                    HelicopterOrderKind kind = cur.FlightPhase != FlightPhase.Ground ? HelicopterOrderKind.Land : HelicopterOrderKind.TakeOff;
                    session.OrderHelicopter(alphaId, kind, cur.AltitudeBand);
                }
            };

            // Trigger TAKE OFF button with NO move command
            panel.FlightToggleButton.EmitSignal(BaseButton.SignalName.Pressed);

            for (int i = 0; i < 180; i++)
            {
                session.Advance(1d / Simulation.TickRate);
                views.Sync(session.Replica, session.RenderTick);
            }

            Require(session.Replica.TryGetOwnUnit(alphaId, out OwnUnitObservation airborneObs), "Chinook Alpha must remain alive.");
            Require(airborneObs.FlightPhase == FlightPhase.Airborne, $"Expected Airborne after pressing TAKE OFF, got {airborneObs.FlightPhase}.");
            Require(airborneObs.Altitude >= 54.0f, $"Expected altitude >= 54m, got {airborneObs.Altitude:F2}m.");
            Require(airborneObs.Health == 160, $"Northern staging takeoff must be safe from enemy fire, got HP={airborneObs.Health}.");

            UnitView alphaView = views.Views[alphaId];
            Require(alphaView.Model.Position.Y >= 53.0f, $"Expected UnitView Model.Position.Y >= 53m, got {alphaView.Model.Position.Y:F2}m.");
            Require(alphaView.IsAltitudeTetherVisible, "Airborne CH-47D UnitView must show altitude tether.");

            panel.UpdateContext(views.Selected, session.Replica);
            Require(panel.FlightToggleButton.Text.Contains("LAND HERE", StringComparison.Ordinal),
                $"Expected LAND HERE label once airborne, got '{panel.FlightToggleButton.Text}'.");
        }
        finally
        {
            scene.RemoveChild(panel);
            panel.QueueFree();
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
