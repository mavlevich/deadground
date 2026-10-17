using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

internal sealed partial class RoleBadgeOverlay : Control
{
    private readonly Dictionary<EntityId, RoleBadge> _unitBadges = new();
    private readonly Dictionary<ContactId, RoleBadge> _contactBadges = new();
    private readonly List<EntityId> _removedUnits = new();
    private readonly List<ContactId> _removedContacts = new();
    private readonly List<Rect2> _cachedPanels = new();
    private readonly UnitCatalog _catalog;

    public RoleBadgeOverlay(UnitCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
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

            if (!_unitBadges.TryGetValue(id, out RoleBadge? badge))
            {
                badge = new RoleBadge();
                _unitBadges.Add(id, badge);
                AddChild(badge);
            }

            string roleText = GetOwnUnitRoleText(obs.Definition);
            int groupNumber = unitViews.GetLowestControlGroup(id);
            badge.BindUnit(id, roleText, groupNumber);

            Vector3 worldPos = view.Root.Position + Vector3.Up * 2.8f;
            if (camera.IsPositionBehind(worldPos))
            {
                badge.Visible = false;
                continue;
            }

            Vector2 screenPos = camera.UnprojectPosition(worldPos);
            Vector2 badgeSize = badge.GetCombinedMinimumSize();
            float width = Math.Max(40f, badgeSize.X);
            float height = Math.Max(18f, badgeSize.Y);
            Rect2 badgeRect = new(screenPos.X - width * 0.5f, screenPos.Y - height * 0.5f, width, height);
            if (IsOffscreenOrOccluded(badgeRect, viewportSize, occludingPanels))
            {
                badge.Visible = false;
                continue;
            }

            badge.Visible = true;
            badge.UpdateScreenPosition(screenPos, opacity);
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
            badge.BindContact(id, view.Observation.State, view.Observation.Level, view.Observation.Classification, ageSeconds);

            float heightOffset = view.Observation.State == ContactState.LastKnown
                ? 2.2f
                : (view.Observation.Level == DetectionLevel.Identified ? 2.8f : 3.0f);

            Vector3 worldPos = view.Root.Position + Vector3.Up * heightOffset;
            if (camera.IsPositionBehind(worldPos))
            {
                badge.Visible = false;
                continue;
            }

            Vector2 screenPos = camera.UnprojectPosition(worldPos);
            Vector2 badgeSize = badge.GetCombinedMinimumSize();
            float width = Math.Max(40f, badgeSize.X);
            float height = Math.Max(18f, badgeSize.Y);
            Rect2 badgeRect = new(screenPos.X - width * 0.5f, screenPos.Y - height * 0.5f, width, height);
            if (IsOffscreenOrOccluded(badgeRect, viewportSize, occludingPanels))
            {
                badge.Visible = false;
                continue;
            }

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
    }

    private string GetOwnUnitRoleText(ushort definition)
    {
        if (definition < _catalog.Units.Length)
        {
            UnitDefinition def = _catalog.Units[definition];
            if (!string.IsNullOrEmpty(def.Classification))
            {
                return def.Classification.ToLowerInvariant() switch
                {
                    "tank" => "TANK",
                    "recon" => "RECON",
                    "apc" => "APC",
                    "command_vehicle" => "CMD",
                    _ => def.Classification.ToUpperInvariant()
                };
            }
            if (def.Id.Contains("tank", StringComparison.OrdinalIgnoreCase)) return "TANK";
            if (def.Id.Contains("recon", StringComparison.OrdinalIgnoreCase)) return "RECON";
            if (def.Id.Contains("command", StringComparison.OrdinalIgnoreCase)) return "CMD";
        }
        return "UNIT";
    }

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
