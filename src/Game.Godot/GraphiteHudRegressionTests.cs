using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

// These checks require Godot and are deliberately not run by a plain dotnet test.
internal static class GraphiteHudRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckOwnReadinessAndContactPrivacy(scene);
        CheckEmbarkedBadgesAndScreenPlacement(scene);
        CheckCardTransitions(scene);
        CheckCommandAvailability(scene);
        CheckLastKnownTargetCannotSubmitAttack((Main)scene);
        CheckTargetCancellation((Main)scene);
        CheckModalCenteringAcrossViewports(scene);
        CheckCompactHud((Main)scene);
        CheckBriefingDisclosure(scene);
        CheckCursorBounds(scene);
        CheckLiveMenuCatalogTransition((Main)scene);
        CheckBugFixesAndPolish((Main)scene);
        GD.Print("GRAPHITE_HUD_REGRESSIONS_OK readiness contact_privacy card_transitions command_capabilities targeting_cancellation polish_fixes");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckModalCenteringAcrossViewports(Node3D scene)
    {
        foreach (Vector2I size in new[] { new Vector2I(1440, 900), new Vector2I(1920, 1080), new Vector2I(3440, 1440) })
        {
            SubViewport viewport = new() { Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
            scene.AddChild(viewport);
            try
            {
                SessionMenu session = new();
                viewport.AddChild(session);
                session.ShowMainMenuView();
                RequireCentered(session.GetActiveCardRect(), size, "start menu");
                session.ShowMultiplayerView();
                RequireCentered(session.GetActiveCardRect(), size, "multiplayer menu");
                session.ShowConnectionLostModal("The connection was interrupted. Return to the menu to join another session.");
                RequireCentered(session.GetActiveCardRect(), size, "disconnect menu");
                session.HideMenu();

                PauseMenu pause = new();
                viewport.AddChild(pause);
                pause.Open();
                RequireCentered(pause.GetCardRect(), size, "pause menu");
                pause.Close();

                SettingsPanel settings = new();
                viewport.AddChild(settings);
                settings.Open();
                RequireCentered(settings.GetContainerRect(), size, "settings menu");
                settings.Close();

                ControlsHelpPanel help = new();
                viewport.AddChild(help);
                help.Open();
                RequireCentered(help.GetCardRect(), size, "controls help");
                help.Close();

                Node3D world = new();
                viewport.AddChild(world);
                MissionPanel result = new(world, HeightMap.CreatePrototype());
                viewport.AddChild(result);
                result.SetScenario("battle");
                result.SetLocalMode(true);
                result.UpdateMission(new MissionObservation(3, MissionStatus.Victory, MissionEndReason.AllArmedUnitsDestroyed, 0, 100, default, default, 0, 0), 100);
                RequireCentered(result.GetResultRect(), size, "result menu");
            }
            finally { viewport.Free(); }
        }
    }

    private static void RequireCentered(Rect2 rect, Vector2I viewportSize, string label)
    {
        Vector2 center = new(viewportSize.X * 0.5f, viewportSize.Y * 0.5f);
        Require(rect.Size.X > 0 && rect.Size.Y > 0 && rect.GetCenter().DistanceTo(center) <= 1f,
            $"The {label} must use its completed content size when centered in {viewportSize}, got {rect}.");
        Require(new Rect2(Vector2.Zero, viewportSize).Encloses(rect), $"The {label} must stay within {viewportSize}, got {rect}.");
    }

    private static void CheckCompactHud(Main main)
    {
        bool savedDebug = main.DebugHudVisible;
        bool savedRanges = main.RangeLimitsVisible;
        HashSet<EntityId> savedUnits = [.. main.UnitViews.Selected];
        HashSet<ContactId> savedContacts = [.. main.ContactViews.Selected];
        try
        {
            main.SetDebugHudVisible(false);
            float compactHeader = main.TopHud.GetCombinedMinimumSize().Y;
            Require(!main.StatsLabel.Visible, "Diagnostics must not occupy the normal battle header.");
            using InputEventKey diagnostics = new() { Keycode = Key.F9, Pressed = true };
            main._UnhandledInput(diagnostics);
            Require(main.DebugHudVisible && main.TopHud.GetCombinedMinimumSize().Y > compactHeader + 20f, "F9 must reveal diagnostics without shrinking text.");
            main._UnhandledInput(diagnostics);
            Require(!main.DebugHudVisible, "F9 must hide diagnostics again.");

            main.UnitViews.Selected.Clear();
            main.ContactViews.Selected.Clear();
            main.UpdateSelectionPresentation();
            main.RefreshOccludingPanels();
            Require(!main.SelectionCard.Visible && !main.CommandPanel.Visible, "Empty selection must not leave blank tactical panels.");
            Require(main.InputOccludingPanels[2].Size == Vector2.Zero && main.InputOccludingPanels[7].Size == Vector2.Zero, "Hidden tactical panels must release their input regions.");
            Vector2 viewport = main.GetViewport().GetVisibleRect().Size;
            Require(!SelectionController.IsPointOccluded(new Vector2(viewport.X * 0.5f, viewport.Y - 32f), main.InputOccludingPanels), "Removed footer must release the bottom center of the battlefield.");

            EntityId id = main.Session.Replica.OwnUnits[0].Id;
            main.UnitViews.Selected.Add(id);
            main.UnitViews.UpdateSelectionVisuals();
            main.UpdateSelectionPresentation();
            Require(main.CommandPanel.GetCombinedMinimumSize().X <= HudLayout.CommandWidth, "Compact command buttons must fit their fixed width without smaller fonts.");
            bool beforeRangeKey = main.RangeLimitsVisible;
            using InputEventKey rangeKey = new() { Keycode = Key.F7, Pressed = true };
            main._UnhandledInput(rangeKey);
            Require(main.RangeLimitsVisible != beforeRangeKey && main.UnitViews.RangeIndicatorsVisible == main.RangeLimitsVisible, "F7 must toggle the real range presentation.");
            main._UnhandledInput(rangeKey);

            main.BeginHudTargeting(HudTargetingMode.Move);
            uint sequence = main.Session.LastSubmittedCommandSequence;
            main.OpenControlsHelp();
            Require(main.HelpPanel.IsOpen && main.CommandTargetingMode == HudTargetingMode.None, "Opening help must cancel targeting.");
            using InputEventMouseButton click = new() { ButtonIndex = MouseButton.Right, Pressed = true, Position = viewport * 0.5f };
            main._UnhandledInput(click);
            Require(main.Session.LastSubmittedCommandSequence == sequence, "Help must consume gameplay orders.");
            using InputEventKey escape = new() { Keycode = Key.Escape, Pressed = true };
            main._UnhandledInput(escape);
            Require(!main.HelpPanel.IsOpen && main.UnitViews.Selected.Contains(id), "Escape must close help without clearing selection.");
        }
        finally
        {
            main.HelpPanel.Close();
            main.CancelActiveDrag();
            main.SetDebugHudVisible(savedDebug);
            if (main.RangeLimitsVisible != savedRanges) main.ToggleRangeLimits();
            main.UnitViews.Selected.Clear();
            foreach (EntityId id in savedUnits) main.UnitViews.Selected.Add(id);
            main.ContactViews.Selected.Clear();
            foreach (ContactId id in savedContacts) main.ContactViews.Selected.Add(id);
            main.UnitViews.UpdateSelectionVisuals();
            main.UpdateSelectionPresentation();
        }
    }

    private static void CheckBriefingDisclosure(Node3D scene)
    {
        MissionPanel panel = new(scene, HeightMap.CreatePrototype());
        scene.AddChild(panel);
        try
        {
            panel.SetBriefingExpanded(false);
            float compactHeight = panel.GetBriefingRect().Size.Y;
            panel.BriefingToggleButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(panel.IsBriefingExpanded && panel.GetBriefingRect().Size.Y > compactHeight + 30f, "Objective button must reveal the full briefing.");
            panel.BriefingToggleButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(!panel.IsBriefingExpanded && panel.GetBriefingRect().Size.Y <= compactHeight + 1f, "Collapsing briefing must actually release its old area.");
        }
        finally { panel.Cleanup(); panel.Free(); }
    }

    private static void CheckCursorBounds(Node3D scene)
    {
        OrderCursorOverlay overlay = new();
        scene.AddChild(overlay);
        try
        {
            overlay.SetCustomTooltip(true, DestinationValidity.Valid, "A very long cursor tooltip message that expands the panel width");
            Vector2 viewport = new(1440, 900);
            overlay.UpdatePosition(new Vector2(200, 200), viewport);
            float wideWidth = overlay.PanelRect.Size.X;
            overlay.SetCustomTooltip(true, DestinationValidity.Valid, "Move");
            overlay.UpdatePosition(new Vector2(5000, 5000), viewport);
            Require(overlay.PanelRect.Size.X < wideWidth - 40f, $"Cursor tooltip must shrink when text shortens, got {overlay.PanelRect.Size.X} vs {wideWidth}.");
            Require(new Rect2(Vector2.Zero, viewport).Encloses(overlay.PanelRect), "Cursor tooltip must stay inside the viewport even when the pointer is outside the window.");
        }
        finally { overlay.Free(); }
    }

    private static void CheckLiveMenuCatalogTransition(Main main)
    {
        if (main.Session is not LocalSession) return;
        IGameSession savedSession = main.Session;
        string savedScenario = main.Scenario;
        HashSet<EntityId> savedSelection = [.. main.UnitViews.Selected];
        try
        {
            main.SetScenarioForTest("prototype");
            main.LeaveCurrentMatch();
            main.StartLocalBattle();
            main.Session.Advance(Simulation.TickSeconds * 2);
            main.UpdateViews();
            OwnUnitObservation tank = main.Session.Replica.OwnUnits[0];
            UnitDefinition def = main.Catalog.Units[tank.Definition];
            Require(def.Weapon is not null && tank.Ammunition > 0, "Battle roster fixture must start with an armed tank.");
            main.UnitViews.Selected.Add(tank.Id);
            main.UpdateSelectionPresentation();
            Require(main.SelectionCard.TitleText == SelectionCard.FormatUnitName(def), "Starting a battle from a prototype must rebind unit identity.");
            Require(!main.CommandPanel.AttackButton.Disabled && main.SelectionCard.Readiness.State != WeaponReadinessState.Unarmed, "Battle commands and readiness must use the new catalog.");
            Require(main.RoleBadgeOverlay.GetOwnUnitRoleText(tank.Definition) == def.Name!.ToUpperInvariant(), "Role name cache must use the new catalog.");

            ushort infantryDefinition = main.Catalog.RequireDefinitionIndex("us_rifleman");
            ClientReplica infantryReplica = new();
            OwnUnitObservation infantry = new(new EntityId(901), infantryDefinition, default, 0, Health: 15);
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            int size = WireProtocol.WriteSnapshot(bytes, 1, 1, [infantry]);
            Require(infantryReplica.TryApply(bytes.AsSpan(0, size)), "Infantry HUD fixture snapshot must apply.");
            main.ContextActionPanel.UpdateContext([infantry.Id], infantryReplica);
            Require(main.ContextActionPanel.BoardButton.Visible, "Transport actions must also use the new catalog classification.");
            CheckContextPointerOcclusion(main, infantryReplica, infantry, bytes);
        }
        finally
        {
            main.Session.Dispose();
            main.SetScenarioForTest(savedScenario);
            main.Session.Dispose();
            main.Session = savedSession;
            main.UnitViews.Selected.Clear();
            foreach (EntityId id in savedSelection) main.UnitViews.Selected.Add(id);
            main.UpdateViews();
            main.UnitViews.UpdateSelectionVisuals();
            main.UpdateSelectionPresentation();
            main.OrderNotification.Clear();
        }
    }

    private static void CheckContextPointerOcclusion(Main main, ClientReplica replica, OwnUnitObservation infantry, byte[] bytes)
    {
        IGameSession savedSession = main.Session;
        Vector2 savedMouse = main.GetViewport().GetMousePosition();
        try
        {
            main.Session = new RecordingSession(replica);
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(infantry.Id);
            main.UpdateSelectionPresentation();
            main.ContextActionPanel.SetTargetingMode(ContextTargetingMode.BoardTransport);
            using InputEventMouseMotion outside = new() { Position = new Vector2(5000, 5000), GlobalPosition = new Vector2(5000, 5000) };
            main.GetWindow().PushInput(outside, inLocalCoords: true);
            main._Process(0);
            Require(main.ContextActionPanel.TargetingMode == ContextTargetingMode.BoardTransport && !main.OrderCursorOverlay.IsPanelVisible,
                "Boarding must not show a stuck tooltip when the pointer leaves the viewport.");

            OwnUnitObservation transport = new(new EntityId(902), main.Catalog.RequireDefinitionIndex("m113a1"), default, 0, PassengerCount: 3, PassengerCapacity: 11);
            int size = WireProtocol.WriteSnapshot(bytes, 2, 2, [transport]);
            Require(replica.TryApply(bytes.AsSpan(0, size)), "Transport cursor fixture snapshot must apply.");
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(transport.Id);
            main.UpdateSelectionPresentation();
            main.ContextActionPanel.SetTargetingMode(ContextTargetingMode.Disembark);
            main._Process(0);
            Require(main.ContextActionPanel.TargetingMode == ContextTargetingMode.Disembark && !main.OrderCursorOverlay.IsPanelVisible,
                "Disembark targeting must also hide its tooltip outside the viewport.");
        }
        finally
        {
            main.ContextActionPanel.CancelTargeting();
            main.Session = savedSession;
            using InputEventMouseMotion restore = new() { Position = savedMouse, GlobalPosition = savedMouse };
            main.GetWindow().PushInput(restore, inLocalCoords: true);
        }
    }

    private static void CheckOwnReadinessAndContactPrivacy(Node3D scene)
    {
        RoleBadge badge = new();
        scene.AddChild(badge);
        try
        {
            badge.BindUnit(new EntityId(41), "T-62", classification: ContactClassification.Tank);
            badge.UpdateOwnReadiness(0.6f, WeaponReadinessPresentation.From(true, 4, 30, 60), true);
            Require(badge.ReadinessVisible && badge.Readiness.Progress == 0.5f && badge.ReadinessText == "RELOADING", "Own badge must show server reload progress below health.");
            badge.UpdateOwnReadiness(0.6f, WeaponReadinessPresentation.From(true, 0, 0, 60), true);
            Require(badge.ReadinessText == "NO AMMO", "Empty weapon must not display READY.");
            badge.UpdateOwnReadiness(0.6f, WeaponReadinessPresentation.From(true, 4, 0, 60), false);
            Require(!badge.ReadinessVisible, "Distant unselected badges must hide detailed bars.");
            badge.UpdateOwnReadiness(0.6f, WeaponReadinessPresentation.From(true, 4, 0, 60), true);
            badge.BindContact(new ContactId(7), ContactState.LastKnown, "T-62", ContactClassification.Tank, 3f);
            Require(!badge.ReadinessVisible && badge.LabelText == "T-62 3s", "Contact binding must hide private readiness and retain last-seen age.");
        }
        finally { badge.Free(); }
    }

    private static void CheckCardTransitions(Node3D scene)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        SelectionCard card = new(catalog);
        scene.AddChild(card);
        try
        {
            OwnUnitObservation own = new(new EntityId(1), catalog.RequireDefinitionIndex("t62"), default, 0, Ammunition: 0);
            card.UpdateSingleOwnUnit(own);
            Require(card.ReadinessVisible && card.Readiness.State == WeaponReadinessState.Empty && card.ReadinessText == "NO AMMO", "Card must distinguish empty from ready.");
            VBoxContainer details = card.GetChild<VBoxContainer>(0);
            for (int i = 0; i < details.GetChildCount(); i++)
                if (details.GetChild(i) is Label { TooltipText.Length: > 0 } label)
                    Require(label.MouseFilter != Control.MouseFilterEnum.Ignore, "Compact card details must remain reachable through tooltips.");
            card.UpdateMultipleOwnUnits(2, 2, 0, 0);
            Require(!card.ReadinessVisible, "Group card must not present one invented aggregate reload.");
            card.UpdateSingleOwnUnit(own with { Ammunition = 10, ReloadTicksRemaining = 10 });
            card.UpdateContact(new ContactObservation(new ContactId(2), ContactState.LastKnown, ContactClassification.Tank, own.Definition, default, 1), 21);
            Require(!card.ReadinessVisible, "Enemy card must never reuse own-unit health/reload bars.");
            card.UpdateEmpty();
            Require(!card.ReadinessVisible, "Empty card must hide readiness.");
        }
        finally { card.Free(); }
    }

    private static void CheckEmbarkedBadgesAndScreenPlacement(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        HeightMap map = HeightMap.CreatePrototype();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        UnitViewSet units = new(parent, map, catalog);
        ContactViewSet contacts = new(parent, map);
        RoleBadgeOverlay overlay = new(catalog);
        parent.AddChild(overlay);
        Camera3D camera = new() { Projection = Camera3D.ProjectionType.Orthogonal, Size = 80, Position = new Vector3(50, 80, 85) };
        parent.AddChild(camera);
        camera.LookAt(new Vector3(50, 0, 50), Vector3.Up);
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation own = new(new EntityId(41), 0, new System.Numerics.Vector2(50, 50), 0, Ammunition: 5);
        try
        {
            int size = WireProtocol.WriteSnapshot(buffer, 1, 1, [own]);
            Require(replica.TryApply(buffer.AsSpan(0, size)), "Badge placement snapshot must apply.");
            units.Sync(replica, 1);
            units.Selected.Add(own.Id);
            overlay.UpdateBadges(camera, 80, units, contacts, replica, 1, []);
            RoleBadge badge = overlay.TryGetUnitBadge(own.Id) ?? throw new InvalidOperationException("Own badge must exist.");
            Vector2 anchor = camera.UnprojectPosition(units.Views[own.Id].Root.Position + Vector3.Up * 3.4f);
            Require(badge.Visible && badge.GetGlobalRect().End.Y <= anchor.Y - 3f, "Full health/readiness badge must sit above the model anchor.");

            size = WireProtocol.WriteSnapshot(buffer, 2, 2, [own with { ContainerId = new EntityId(42) }]);
            Require(replica.TryApply(buffer.AsSpan(0, size)), "Embarked snapshot must apply.");
            units.Sync(replica, 2);
            overlay.UpdateBadges(camera, 80, units, contacts, replica, 2, []);
            Require(!badge.Visible, "Embarked unit must not draw a badge over its transport.");

            size = WireProtocol.WriteSnapshot(buffer, 3, 3, [own]);
            Require(replica.TryApply(buffer.AsSpan(0, size)), "Disembarked snapshot must apply.");
            units.Sync(replica, 3);
            overlay.UpdateBadges(camera, 80, units, contacts, replica, 3, []);
            Require(badge.Visible, "Disembarked unit must regain its badge.");
        }
        finally
        {
            units.Clear();
            contacts.Clear();
            parent.Free();
        }
    }

    private static void CheckCommandAvailability(Node3D scene)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        // Missing reverse speed uses the same resolved fallback as navigation.
        catalog = catalog with { Units = [catalog.Units[catalog.RequireDefinitionIndex("t62")] with { ReverseSpeed = 0 }] };
        UnitCommandPanel panel = new(catalog);
        scene.AddChild(panel);
        try
        {
            ClientReplica replica = new();
            HashSet<EntityId> selected = [new EntityId(1)];
            OwnUnitObservation own = new(new EntityId(1), catalog.RequireDefinitionIndex("t62"), default, 0, Ammunition: 5);
            byte[] buffer = new byte[WireProtocol.MaxMessageSize];
            int size = WireProtocol.WriteSnapshot(buffer, 1, 1, [own]);
            Require(replica.TryApply(buffer.AsSpan(0, size)), "Command fixture snapshot must apply.");
            panel.UpdateSelection(selected, replica, false);
            Require(!panel.MoveButton.Disabled && !panel.ReverseButton.Disabled && !panel.AttackButton.Disabled, "An operational tank must expose existing commands.");
            size = WireProtocol.WriteSnapshot(buffer, 2, 2, [own with { Ammunition = 0 }]);
            replica.TryApply(buffer.AsSpan(0, size));
            panel.UpdateSelection(selected, replica, false);
            Require(panel.AttackButton.Disabled && !panel.MoveButton.Disabled, "No ammo disables attack, not movement.");
            panel.UpdateSelection(selected, replica, true);
            Require(panel.MoveButton.Disabled && panel.ReverseButton.Disabled && panel.AttackButton.Disabled, "Finished mission must disable command buttons.");
            selected.Clear();
            panel.UpdateSelection(selected, replica, false);
            Require(panel.MoveButton.Disabled, "No selection must disable commands.");
        }
        finally { panel.Free(); }
    }

    private static void CheckTargetCancellation(Main main)
    {
        HashSet<EntityId> saved = [.. main.UnitViews.Selected];
        try
        {
            Require(main.Session.Replica.OwnUnits.Length > 0, "Main cancellation fixture requires an own unit.");
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(main.Session.Replica.OwnUnits[0].Id);
            main.UpdateSelectionPresentation();
            main.CommandPanel.MoveButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(main.CommandTargetingMode == HudTargetingMode.Move, "Move button must start terrain targeting.");
            using InputEventKey escape = new() { Keycode = Key.Escape, Pressed = true };
            main._UnhandledInput(escape);
            Require(main.CommandTargetingMode == HudTargetingMode.None && main.UnitViews.Selected.Count == 1, "Escape must cancel target mode without clearing selection.");
            main.BeginHudTargeting(HudTargetingMode.Reverse);
            main.Notification((int)Main.NotificationApplicationFocusOut);
            Require(main.CommandTargetingMode == HudTargetingMode.None, "Focus loss must cancel HUD targeting.");
            main.BeginHudTargeting(HudTargetingMode.Move);
            main.UnitViews.Selected.Clear();
            main.UpdateSelectionPresentation();
            Require(main.CommandTargetingMode == HudTargetingMode.None, "Selection change must cancel targeting without a mouse event.");
        }
        finally
        {
            main.CancelActiveDrag();
            main.Notification((int)Main.NotificationApplicationFocusIn);
            main.UnitViews.Selected.Clear();
            foreach (EntityId id in saved) main.UnitViews.Selected.Add(id);
            main.UpdateSelectionPresentation();
        }
    }

    private static void CheckLastKnownTargetCannotSubmitAttack(Main main)
    {
        IGameSession savedSession = main.Session;
        HashSet<EntityId> savedSelection = [.. main.UnitViews.Selected];
        ClientReplica replica = new();
        RecordingSession session = new(replica);
        ushort definition = 0;
        while (definition < main.Catalog.Units.Length && main.Catalog.Units[definition].Weapon is null) definition++;
        Require(definition < main.Catalog.Units.Length, "Attack fixture requires an armed definition.");
        OwnUnitObservation own = new(new EntityId(901), definition, default, 0, Ammunition: 5);
        ContactObservation contact = new(new ContactId(902), ContactState.LastKnown, ContactClassification.Tank, definition, default, 1);
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        try
        {
            int size = WireProtocol.WriteSnapshot(buffer, 1, 1, [own], [contact]);
            Require(replica.TryApply(buffer.AsSpan(0, size)), "Last-known attack fixture must apply.");
            main.Session = session;
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(own.Id);
            main.BeginHudTargeting(HudTargetingMode.Attack);
            main.ExecuteHudAttackTarget(contact.Id);
            Require(session.AttackCount == 0 && main.CommandTargetingMode == HudTargetingMode.Attack, "Ghost target must not submit an impossible attack or consume targeting.");
            size = WireProtocol.WriteSnapshot(buffer, 2, 2, [own], [contact with { State = ContactState.Current }]);
            Require(replica.TryApply(buffer.AsSpan(0, size)), "Current attack fixture must apply.");
            session.AcceptAttack = false;
            main.ExecuteHudAttackTarget(contact.Id);
            Require(session.AttackCount == 1 && main.CommandTargetingMode == HudTargetingMode.Attack, "Rejected submission must leave targeting available for retry.");
            session.AcceptAttack = true;
            main.ExecuteHudAttackTarget(contact.Id);
            Require(session.AttackCount == 2 && main.CommandTargetingMode == HudTargetingMode.None, "Observed target must submit attack and consume targeting.");
        }
        finally
        {
            main.CancelActiveDrag();
            main.Session = savedSession;
            main.UnitViews.Selected.Clear();
            foreach (EntityId id in savedSelection) main.UnitViews.Selected.Add(id);
            main.UpdateSelectionPresentation();
            main.OrderNotification.Clear();
        }
    }

    private static void CheckBugFixesAndPolish(Main main)
    {
        // 1. Complete theme coverage for interactive states and popups
        Theme theme = HudTheme.Shared;
        Require(theme.HasStylebox("hover_pressed", "Button"), "Button must define hover_pressed stylebox.");
        Require(theme.HasStylebox("hover_pressed", "CompactButton"), "CompactButton must define hover_pressed stylebox.");
        Require(theme.HasStylebox("read_only", "LineEdit"), "LineEdit must define read_only stylebox.");
        Require(theme.HasStylebox("panel", "PopupMenu"), "PopupMenu must be themed.");
        Require(theme.HasStylebox("normal", "CheckButton"), "CheckButton must be themed.");
        Require(theme.HasStylebox("panel", "TooltipPanel"), "TooltipPanel must be themed.");

        // 2. Utility toolbar and context buttons must not steal keyboard focus from camera arrows
        HBoxContainer toolbar = main.UtilityHud.GetChild<HBoxContainer>(0);
        for (int i = 0; i < toolbar.GetChildCount(); i++)
        {
            if (toolbar.GetChild(i) is Button btn)
                Require(btn.FocusMode == Control.FocusModeEnum.None, $"Toolbar button '{btn.Text}' must not steal keyboard focus.");
        }
        Require(main.ContextActionPanel.BoardButton.FocusMode == Control.FocusModeEnum.None, "Board button must not steal focus.");
        Require(main.ContextActionPanel.DisembarkHereButton.FocusMode == Control.FocusModeEnum.None, "Unload here button must not steal focus.");
        Require(main.ContextActionPanel.DisembarkButton.FocusMode == Control.FocusModeEnum.None, "Unload at button must not steal focus.");
        Require(main.CommandPanel.StopButton.FocusMode == Control.FocusModeEnum.None, "Stop button must not steal focus.");

        // 3. Cursor overlay renders above the minimap
        Require(main.OrderCursorOverlay.ZIndex > main.Minimap.ZIndex || main.OrderCursorOverlay.GetIndex() > main.Minimap.GetIndex(),
            "OrderCursorOverlay must render above TacticalMinimap.");

        // 4. Infantry and APC group breakdown (never 'Other') + roster chips
        UnitCatalog coldWar = ColdWarContent.LoadCatalog();
        SelectionCard groupCard = new(coldWar);
        main.AddChild(groupCard);
        try
        {
            ClientReplica replica = new();
            ushort rifleDef = coldWar.RequireDefinitionIndex("us_rifleman");
            ushort apcDef = coldWar.RequireDefinitionIndex("m113a1");
            OwnUnitObservation[] squad =
            [
                new(new EntityId(701), rifleDef, default, 0, Health: 15, Ammunition: 20),
                new(new EntityId(702), rifleDef, default, 0, Health: 8, Ammunition: 0),
                new(new EntityId(703), apcDef, default, 0, Health: 50, Ammunition: 100)
            ];
            byte[] buf = new byte[WireProtocol.MaxMessageSize];
            int bytes = WireProtocol.WriteSnapshot(buf, 1, 1, squad);
            Require(replica.TryApply(buf.AsSpan(0, bytes)), "Group composition snapshot must apply.");
            HashSet<EntityId> sel = [new EntityId(701), new EntityId(702), new EntityId(703)];
            groupCard.UpdateMultipleOwnUnits(sel, replica, controlGroup: 2);
            Require(groupCard.Line1Text.Contains("2 Infantry") && groupCard.Line1Text.Contains("1 APC") && !groupCard.Line1Text.Contains("Other"),
                $"Group card must classify Infantry and APC explicitly, got '{groupCard.Line1Text}'.");
            Require(groupCard.VisibleRosterChipCount == 3, $"Group card must show 3 clickable roster chips, got {groupCard.VisibleRosterChipCount}.");
            Require(groupCard.SpecsText.Contains("damaged") && groupCard.SpecsText.Contains("NO AMMO"),
                $"Group card must surface damaged and empty-ammo alerts, got '{groupCard.SpecsText}'.");
        }
        finally { groupCard.Free(); }

        // 5. Command button toggle highlight + toggle-cancel + CancelActiveDrag clears ContextActionPanel + F1 help
        HashSet<EntityId> savedUnits = [.. main.UnitViews.Selected];
        try
        {
            EntityId id = main.Session.Replica.OwnUnits[0].Id;
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(id);
            main.UpdateSelectionPresentation();

            main.CommandPanel.MoveButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(main.CommandTargetingMode == HudTargetingMode.Move && main.CommandPanel.MoveButton.ButtonPressed,
                "Pressing Move must enter targeting mode and highlight the Move button.");
            main.CommandPanel.MoveButton.EmitSignal(BaseButton.SignalName.Pressed);
            Require(main.CommandTargetingMode == HudTargetingMode.None && !main.CommandPanel.MoveButton.ButtonPressed,
                "Pressing an active targeting button again must cancel targeting.");

            main.ContextActionPanel.SetTargetingMode(ContextTargetingMode.BoardTransport);
            main.CancelActiveDrag();
            Require(main.ContextActionPanel.TargetingMode == ContextTargetingMode.None,
                "CancelActiveDrag must cancel context transport targeting as well.");

            using InputEventKey f1 = new() { Keycode = Key.F1, Pressed = true };
            main._UnhandledInput(f1);
            Require(main.HelpPanel.IsOpen, "F1 must open ControlsHelpPanel.");
            main._UnhandledInput(f1);
            Require(!main.HelpPanel.IsOpen, "F1 while Help is open must close ControlsHelpPanel.");

            // 6. STOP [E] button and E hotkey dispatch StopCommand through session and cancel targeting
            IGameSession savedForStop = main.Session;
            RecordingSession stopRec = new(main.Session.Replica);
            try
            {
                main.Session = stopRec;
                main.UpdateSelectionPresentation();
                Require(!main.CommandPanel.StopButton.Disabled, "STOP [E] button must be enabled when an own unit is selected.");

                main.CommandPanel.MoveButton.EmitSignal(BaseButton.SignalName.Pressed);
                Require(main.CommandTargetingMode == HudTargetingMode.Move, "Precondition: Move targeting active before Stop.");
                main.CommandPanel.StopButton.EmitSignal(BaseButton.SignalName.Pressed);
                Require(stopRec.StopCount == 1, $"STOP button must issue 1 stop command, got {stopRec.StopCount}.");
                Require(main.CommandTargetingMode == HudTargetingMode.None, "STOP button must cancel active targeting mode.");

                using InputEventKey stopKey = new() { Keycode = Key.E, Pressed = true };
                main._UnhandledInput(stopKey);
                Require(stopRec.StopCount == 2, $"Pressing E must issue a stop command, got {stopRec.StopCount}.");
            }
            finally
            {
                main.Session = savedForStop;
            }
        }
        finally
        {
            main.HelpPanel.Close();
            main.CancelActiveDrag();
            main.UnitViews.Selected.Clear();
            foreach (EntityId id in savedUnits) main.UnitViews.Selected.Add(id);
            main.UpdateSelectionPresentation();
        }
    }

    private sealed class RecordingSession(ClientReplica replica) : IGameSession
    {
        public int AttackCount { get; private set; }
        public bool AcceptAttack { get; set; } = true;
        public ClientReplica Replica => replica;
        public MissionObservation Mission => default;
        public double RenderTick => 2;
        public int Tick => 2;
        public double LastTickMilliseconds => 0;
        public long LastTickAllocatedBytes => 0;
        public long BytesReceived => 0;
        public double PendingSeconds => 0;
        public CommandResult? LastCommandResult => null;
        public GroupMoveResult? LastGroupMoveResult => null;
        public DisembarkResult? LastDisembarkResult => null;
        public uint LastSubmittedCommandSequence => 0;
        public long RejectedCommands => 0;
        public bool IsConnected => true;
        public int AssignedPlayerId => 1;
        public string StatusDescription => "HUD fixture";
        public bool Move(EntityId unit, System.Numerics.Vector2 target, float? finalFacing = null, MoveMode mode = MoveMode.Forward, uint? targetSurface = null) => false;
        public bool GroupMove(ReadOnlyMemory<EntityId> units, System.Numerics.Vector2 target, uint targetSurface = 0, float? finalFacing = null, MoveMode mode = MoveMode.Forward) => false;
        public bool Attack(EntityId unit, ContactId target) { AttackCount++; return AcceptAttack; }
        public bool SetFireStance(EntityId unit, FireStance stance) => false;
        public bool BoardTransport(EntityId unit, EntityId transport) => false;
        public bool Disembark(EntityId transport, System.Numerics.Vector2 target, uint targetSurface = 0) => false;
        public bool DeploySmoke(EntityId unit, bool hasTarget = false, System.Numerics.Vector2 target = default, uint targetSurface = 0) => false;
        public bool OrderHelicopter(EntityId unit, HelicopterOrderKind kind, AltitudeBand band = AltitudeBand.High, bool hasTarget = false, System.Numerics.Vector2 target = default, EntityId cargoUnit = default) => false;
        public bool FireMission(EntityId unit, System.Numerics.Vector2 target, bool cancel = false) => false;
        public int StopCount { get; private set; }
        public bool Stop(EntityId unit) { StopCount++; return true; }
        public void Advance(double elapsedSeconds) { }
        public void Dispose() { }
    }
}
