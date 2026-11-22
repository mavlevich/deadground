using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

internal sealed partial class RoleBadgeOverlay : Control
{
    private readonly Dictionary<EntityId, RoleBadge> _unitBadges = new();
    private readonly Dictionary<ContactId, RoleBadge> _contactBadges = new();
    private readonly List<EntityId> _removedUnits = new();
    private readonly List<ContactId> _removedContacts = new();
    private readonly List<Rect2> _cachedPanels = new();
    private readonly List<Rect2> _placedBadges = new();
    private UnitCatalog _catalog = null!;
    private string[] _roleTexts = Array.Empty<string>();
    private int[] _reloadDurations = Array.Empty<int>();
    private ContactClassification[] _classifications = Array.Empty<ContactClassification>();

    public RoleBadgeOverlay(UnitCatalog catalog)
    {
        SetCatalog(catalog);
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    internal void SetCatalog(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Clear();
        _roleTexts = new string[catalog.Units.Length];
        _reloadDurations = new int[catalog.Units.Length];
        _classifications = new ContactClassification[catalog.Units.Length];
        for (int i = 0; i < catalog.Units.Length; i++)
        {
            UnitDefinition def = catalog.Units[i];
            _roleTexts[i] = FormatRoleText(def);
            _classifications[i] = def.Classification?.ToLowerInvariant() switch
            {
                "tank" => ContactClassification.Tank,
                "recon" => ContactClassification.Recon,
                "apc" => ContactClassification.Apc,
                "infantry" => ContactClassification.Infantry,
                "command_vehicle" or "command" => ContactClassification.CommandVehicle,
                _ => ContactClassification.Vehicle
            };
            foreach (WeaponData weapon in catalog.Weapons ?? Array.Empty<WeaponData>())
            {
                if (weapon.Id != def.Weapon) continue;
                _reloadDurations[i] = weapon.ReloadDurationTicks > 0
                    ? weapon.ReloadDurationTicks
                    : (int)Math.Round(weapon.ReloadSeconds / Simulation.TickSeconds);
                break;
            }
        }
    }

    public void UpdateBadges(
        Camera3D camera,
        float cameraSize,
        UnitViewSet unitViews,
        ContactViewSet contactViews,
        ClientReplica replica,
        int serverTick,
        ReadOnlySpan<Rect2> occludingPanels)
    {
        Vector2 viewportSize = GetViewportRect().Size;
        float opacity = Mathf.Clamp((cameraSize - 20f) / 15f, 0.2f, 1.0f);

        _cachedPanels.Clear();
        _placedBadges.Clear();
        for (int i = 0; i < occludingPanels.Length; i++)
        {
            if (occludingPanels[i].Size.X > 0 && occludingPanels[i].Size.Y > 0)
                _cachedPanels.Add(occludingPanels[i]);
        }

        // 1. Update own unit badges
        _removedUnits.Clear();
        foreach (EntityId id in _unitBadges.Keys)
        {
            if (!unitViews.Views.ContainsKey(id))
                _removedUnits.Add(id);
        }
        foreach (EntityId id in _removedUnits)
        {
            RoleBadge badge = _unitBadges[id];
            RemoveChild(badge);
            badge.QueueFree();
            _unitBadges.Remove(id);
        }

        foreach ((EntityId id, UnitView view) in unitViews.Views)
        {
            if (!replica.TryGetOwnUnit(id, out OwnUnitObservation obs))
                continue;

            if (obs.ContainerId != default || !view.Root.Visible)
            {
                if (_unitBadges.TryGetValue(id, out RoleBadge? hiddenBadge)) hiddenBadge.Visible = false;
                continue;
            }

            if (!_unitBadges.TryGetValue(id, out RoleBadge? badge))
            {
                badge = new RoleBadge();
                _unitBadges.Add(id, badge);
                AddChild(badge);
            }

            string roleText = GetOwnUnitRoleText(obs.Definition);
            int groupNumber = unitViews.GetLowestControlGroup(id);
            bool hasDefinition = obs.Definition < _catalog.Units.Length;
            UnitDefinition? definition = hasDefinition ? _catalog.Units[obs.Definition] : null;
            badge.BindUnit(id, roleText, groupNumber, hasDefinition ? _classifications[obs.Definition] : ContactClassification.Unknown);
            WeaponReadinessPresentation readiness = WeaponReadinessPresentation.From(
                definition?.Weapon is not null, obs.Ammunition, obs.ReloadTicksRemaining,
                hasDefinition ? _reloadDurations[obs.Definition] : 0);
            bool selected = unitViews.Selected.Contains(id);
            bool needsAttention = obs.Health < (definition?.MaxHealth ?? 100) || readiness.State is WeaponReadinessState.Reloading or WeaponReadinessState.Empty;
            bool showLabel = !view.IsInfantry || selected || cameraSize >= 55f;
            badge.UpdateOwnReadiness(
                obs.Health / (float)Math.Max(1, definition?.MaxHealth ?? 100),
                readiness,
                selected || (cameraSize <= 60f && needsAttention),
                selected,
                showLabel,
                obs.Stance,
                obs.PassengerCount,
                obs.PassengerCapacity);
            // This overlay owns the screen-space indicators in the main client.
            view.SetWorldHealthBarVisible(false);

            Vector3 worldPos = view.Root.Position + Vector3.Up * (view.IsInfantry ? 2.2f : 3.4f);
            if (camera.IsPositionBehind(worldPos))
            {
                badge.Visible = false;
                continue;
            }

            Vector2 screenPos = camera.UnprojectPosition(worldPos);
            Vector2 badgeSize = badge.GetCombinedMinimumSize();
            float width = Math.Max(28f, badgeSize.X);
            float height = Math.Max(14f, badgeSize.Y);
            screenPos.Y -= height * 0.5f + 4f;
            screenPos = ResolveOverlap(screenPos, width, height);
            Rect2 badgeRect = new(screenPos.X - width * 0.5f, screenPos.Y - height * 0.5f, width, height);
            if (IsOffscreenOrOccluded(badgeRect, viewportSize, occludingPanels))
            {
                badge.Visible = false;
                continue;
            }

            _placedBadges.Add(badgeRect);
            badge.Visible = true;
            badge.UpdateScreenPosition(screenPos, selected ? 1f : opacity);
        }

        // 2. Update enemy contact badges
        _removedContacts.Clear();
        foreach (ContactId id in _contactBadges.Keys)
        {
            if (!contactViews.Views.ContainsKey(id))
                _removedContacts.Add(id);
        }
        foreach (ContactId id in _removedContacts)
        {
            RoleBadge badge = _contactBadges[id];
            RemoveChild(badge);
            badge.QueueFree();
            _contactBadges.Remove(id);
        }

        foreach ((ContactId id, ContactView view) in contactViews.Views)
        {
            if (!_contactBadges.TryGetValue(id, out RoleBadge? badge))
            {
                badge = new RoleBadge();
                _contactBadges.Add(id, badge);
                AddChild(badge);
            }

            float ageSeconds = Math.Max(0, serverTick - view.Observation.LastSeenTick) / (float)Simulation.TickRate;
            string roleText = GetRoleText(view.Observation.Type, view.Observation.Classification);
            badge.BindContact(id, view.Observation.State, roleText, view.Observation.Classification, ageSeconds);

            float heightOffset = view.Observation.State == ContactState.LastKnown
                ? 2.2f
                : 2.8f;

            Vector3 worldPos = view.Root.Position + Vector3.Up * heightOffset;
            if (camera.IsPositionBehind(worldPos))
            {
                badge.Visible = false;
                continue;
            }

            Vector2 screenPos = camera.UnprojectPosition(worldPos);
            Vector2 badgeSize = badge.GetCombinedMinimumSize();
            float width = Math.Max(28f, badgeSize.X);
            float height = Math.Max(14f, badgeSize.Y);
            screenPos.Y -= height * 0.5f + 4f;
            screenPos = ResolveOverlap(screenPos, width, height);
            Rect2 badgeRect = new(screenPos.X - width * 0.5f, screenPos.Y - height * 0.5f, width, height);
            if (IsOffscreenOrOccluded(badgeRect, viewportSize, occludingPanels))
            {
                badge.Visible = false;
                continue;
            }

            _placedBadges.Add(badgeRect);
            badge.Visible = true;
            badge.UpdateScreenPosition(screenPos, opacity);
        }
    }

    public bool TryHitBadge(Vector2 mousePosition, out EntityId? hitUnit, out ContactId? hitContact)
    {
        hitUnit = null;
        hitContact = null;

        for (int i = 0; i < _cachedPanels.Count; i++)
        {
            if (_cachedPanels[i].HasPoint(mousePosition))
                return false;
        }

        for (int i = GetChildCount() - 1; i >= 0; i--)
        {
            if (GetChild(i) is RoleBadge badge && badge.Visible && badge.ContainsScreenPoint(mousePosition))
            {
                if (badge.BoundContactId is not null)
                {
                    hitContact = badge.BoundContactId;
                    return true;
                }
                if (badge.BoundUnitId is not null)
                {
                    hitUnit = badge.BoundUnitId;
                    return true;
                }
            }
        }

        return false;
    }

    internal RoleBadge? TryGetUnitBadge(EntityId id) =>
        _unitBadges.GetValueOrDefault(id);

    public void Clear()
    {
        while (GetChildCount() > 0)
        {
            Node child = GetChild(0);
            RemoveChild(child);
            child.QueueFree();
        }
        _unitBadges.Clear();
        _contactBadges.Clear();
        _removedUnits.Clear();
        _removedContacts.Clear();
        _cachedPanels.Clear();
        _placedBadges.Clear();
    }

    private Vector2 ResolveOverlap(Vector2 center, float width, float height)
    {
        // Nudge clustered badges upward in small steps so dense squads stay readable.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Rect2 candidate = new(center.X - width * 0.5f, center.Y - height * 0.5f, width, height);
            bool overlaps = false;
            for (int i = 0; i < _placedBadges.Count; i++)
            {
                if (_placedBadges[i].Intersects(candidate))
                {
                    overlaps = true;
                    break;
                }
            }
            if (!overlaps) break;
            center.Y -= height + 2f;
        }
        return center;
    }

    // Units show their real type ("T-62"); prototype units show their role.
    internal string GetRoleText(ushort definition, ContactClassification fallbackClassification = ContactClassification.Unknown)
    {
        if (definition < _catalog.Units.Length)
        {
            return _roleTexts[definition];
        }
        return fallbackClassification switch
        {
            ContactClassification.Tank => "TANK",
            ContactClassification.Recon => "RECON",
            ContactClassification.Apc => "APC",
            ContactClassification.CommandVehicle => "CMD",
            ContactClassification.Vehicle => "VEH",
            ContactClassification.Infantry => "INF",
            _ => "UNIT"
        };
    }

    private static string FormatRoleText(UnitDefinition def)
    {
        if (def.Name is not null)
            return def.Name.ToUpperInvariant();
        if (!string.IsNullOrEmpty(def.Classification))
        {
            return def.Classification.ToLowerInvariant() switch
            {
                "tank" => "TANK",
                "recon" => "RECON",
                "apc" => "APC",
                "command_vehicle" => "CMD",
                "infantry" => "INF",
                _ => def.Classification.ToUpperInvariant()
            };
        }
        if (def.Id.Contains("tank", StringComparison.OrdinalIgnoreCase)) return "TANK";
        if (def.Id.Contains("recon", StringComparison.OrdinalIgnoreCase)) return "RECON";
        if (def.Id.Contains("command", StringComparison.OrdinalIgnoreCase)) return "CMD";
        return "UNIT";
    }

    internal string GetOwnUnitRoleText(ushort definition) => GetRoleText(definition);

    internal static bool IsOffscreenOrOccluded(Rect2 badgeRect, Vector2 viewportSize, ReadOnlySpan<Rect2> occludingPanels)
    {
        Vector2 center = badgeRect.GetCenter();
        if (center.X < 10 || center.X > viewportSize.X - 10 ||
            center.Y < 10 || center.Y > viewportSize.Y - 10)
        {
            return true;
        }

        Rect2 hitRect = badgeRect.Grow(4f);
        for (int i = 0; i < occludingPanels.Length; i++)
        {
            if (occludingPanels[i].Size.X > 0 && occludingPanels[i].Size.Y > 0 && occludingPanels[i].Intersects(hitRect))
                return true;
        }

        return false;
    }
}
