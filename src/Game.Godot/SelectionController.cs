using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal enum SelectionReleaseKind
{
    None,
    Cancelled,
    ClickSelect,
    DoubleClickSelect,
    BoxSelect
}

internal enum GroupKeyAction
{
    None,
    Store,
    Add,
    Recall
}

internal delegate bool WorldToScreenProjector(Vector3 worldPoint, out Vector2 screenPoint);

// Pure, testable RTS selection and control group controller. Reuses one scratch list for zero per-frame allocations.
internal sealed class SelectionController
{
    public const float DragThresholdPixels = 6f;
    public const float ClickPickRadiusPixels = 24f;
    public const double DoubleTapIntervalSeconds = 0.35;
    public const int ControlGroupCount = UnitViewSet.ControlGroupCount;

    private readonly List<EntityId> _scratchUnits = new(WireProtocol.MaxUnits);
    private readonly WorldToScreenProjector? _customProjector;
    private bool _pointerDown;
    private bool _isDragging;
    private Vector2 _pressStart;
    private Vector2 _currentPosition;
    private bool _pressShift;
    private bool _pressDoubleClick;
    private int _lastRecalledGroup;
    private double _lastRecallTimeSeconds = -100.0;

    public SelectionController(WorldToScreenProjector? customProjector = null)
    {
        _customProjector = customProjector;
    }

    public bool IsPointerDown => _pointerDown;
    public bool IsDragging => _isDragging;
    public Vector2 PressStart => _pressStart;
    public Vector2 CurrentPosition => _currentPosition;
    public Rect2 CurrentBoxRect => MakeScreenRect(_pressStart, _currentPosition);

    public void Reset()
    {
        _pointerDown = false;
        _isDragging = false;
        _pressStart = default;
        _currentPosition = default;
        _pressShift = false;
        _pressDoubleClick = false;
        _lastRecalledGroup = 0;
        _lastRecallTimeSeconds = -100.0;
        _scratchUnits.Clear();
    }

    public static bool IsDragDistance(Vector2 start, Vector2 end) =>
        start.DistanceSquaredTo(end) > DragThresholdPixels * DragThresholdPixels;

    public static Rect2 MakeScreenRect(Vector2 start, Vector2 end)
    {
        float minX = MathF.Min(start.X, end.X);
        float minY = MathF.Min(start.Y, end.Y);
        float maxX = MathF.Max(start.X, end.X);
        float maxY = MathF.Max(start.Y, end.Y);
        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    public static bool ContainsPointInclusive(Rect2 rect, Vector2 point) =>
        point.X >= rect.Position.X && point.X <= rect.Position.X + rect.Size.X &&
        point.Y >= rect.Position.Y && point.Y <= rect.Position.Y + rect.Size.Y;

    public static bool IsPointOccluded(Vector2 point, ReadOnlySpan<Rect2> occludingPanels)
    {
        for (int i = 0; i < occludingPanels.Length; i++)
        {
            Rect2 panel = occludingPanels[i];
            if (panel.Size.X > 0f && panel.Size.Y > 0f && panel.HasPoint(point))
                return true;
        }
        return false;
    }

    public bool TryProjectWorldPoint(Camera3D camera, Vector3 worldPoint, out Vector2 screenPoint)
    {
        if (_customProjector is not null)
            return _customProjector(worldPoint, out screenPoint);

        if (camera.IsPositionBehind(worldPoint))
        {
            screenPoint = default;
            return false;
        }

        screenPoint = camera.UnprojectPosition(worldPoint);
        return true;
    }

    public bool BeginPress(Vector2 screenPos, bool shiftPressed = false, bool doubleClick = false, ReadOnlySpan<Rect2> occludingPanels = default)
    {
        if (IsPointOccluded(screenPos, occludingPanels))
        {
            CancelDrag();
            return false;
        }

        _pointerDown = true;
        _isDragging = false;
        _pressStart = screenPos;
        _currentPosition = screenPos;
        _pressShift = shiftPressed;
        _pressDoubleClick = doubleClick;
        return true;
    }

    public bool UpdateDrag(Vector2 screenPos)
    {
        if (!_pointerDown) return false;
        _currentPosition = screenPos;
        if (!_isDragging && IsDragDistance(_pressStart, screenPos))
            _isDragging = true;
        return _isDragging;
    }

    public void CancelDrag()
    {
        _pointerDown = false;
        _isDragging = false;
        _pressDoubleClick = false;
    }

    public SelectionReleaseKind CompleteRelease(
        Camera3D camera,
        UnitViewSet unitViews,
        ContactViewSet contactViews,
        RoleBadgeOverlay? badgeOverlay,
        Vector2 releasePos,
        Vector2 viewportSize,
        bool shiftPressed,
        out EntityId? clickedUnit,
        out ContactId? clickedContact)
    {
        clickedUnit = null;
        clickedContact = null;

        if (!_pointerDown)
            return SelectionReleaseKind.None;

        bool wasDragging = _isDragging;
        bool wasDoubleClick = _pressDoubleClick;
        Vector2 startPos = _pressStart;
        bool effectiveShift = shiftPressed || _pressShift;
        CancelDrag();

        Rect2 viewportBounds = new(Vector2.Zero, viewportSize);
        if (!ContainsPointInclusive(viewportBounds, releasePos))
            return SelectionReleaseKind.Cancelled;

        if (wasDragging)
        {
            ApplyBoxSelection(camera, unitViews, contactViews, startPos, releasePos, effectiveShift);
            return SelectionReleaseKind.BoxSelect;
        }

        ApplyClickOrDoubleClick(
            camera,
            unitViews,
            contactViews,
            badgeOverlay,
            releasePos,
            viewportSize,
            effectiveShift,
            wasDoubleClick,
            out clickedUnit,
            out clickedContact);

        return wasDoubleClick && clickedUnit is not null
            ? SelectionReleaseKind.DoubleClickSelect
            : SelectionReleaseKind.ClickSelect;
    }

    public IReadOnlyList<EntityId> CollectUnitsInBox(
        Camera3D camera,
        UnitViewSet unitViews,
        Vector2 startScreen,
        Vector2 endScreen)
    {
        _scratchUnits.Clear();
        Rect2 box = MakeScreenRect(startScreen, endScreen);
        foreach ((EntityId id, UnitView view) in unitViews.Views)
        {
            Vector3 center = view.Root.Position + Vector3.Up;
            if (!TryProjectWorldPoint(camera, center, out Vector2 screenPos))
                continue;

            if (ContainsPointInclusive(box, screenPos))
                _scratchUnits.Add(id);
        }
        return _scratchUnits;
    }

    public void ApplyBoxSelection(
        Camera3D camera,
        UnitViewSet unitViews,
        ContactViewSet contactViews,
        Vector2 startScreen,
        Vector2 endScreen,
        bool shiftPressed)
    {
        CollectUnitsInBox(camera, unitViews, startScreen, endScreen);
        contactViews.Selected.Clear();
        if (!shiftPressed)
            unitViews.Selected.Clear();

        for (int i = 0; i < _scratchUnits.Count; i++)
            unitViews.Selected.Add(_scratchUnits[i]);
    }

    public IReadOnlyList<EntityId> CollectSameTypeOnScreen(
        Camera3D camera,
        UnitViewSet unitViews,
        EntityId targetUnitId,
        Vector2 viewportSize)
    {
        _scratchUnits.Clear();
        if (!unitViews.Views.TryGetValue(targetUnitId, out UnitView? targetView) || string.IsNullOrEmpty(targetView.UnitId))
            return _scratchUnits;

        string targetType = targetView.UnitId;
        Rect2 screenBounds = new(Vector2.Zero, viewportSize);
        foreach ((EntityId id, UnitView view) in unitViews.Views)
        {
            if (!string.Equals(view.UnitId, targetType, StringComparison.Ordinal))
                continue;

            Vector3 center = view.Root.Position + Vector3.Up;
            if (!TryProjectWorldPoint(camera, center, out Vector2 screenPos))
                continue;

            if (ContainsPointInclusive(screenBounds, screenPos))
                _scratchUnits.Add(id);
        }
        return _scratchUnits;
    }

    public void ApplyClickOrDoubleClick(
        Camera3D camera,
        UnitViewSet unitViews,
        ContactViewSet contactViews,
        RoleBadgeOverlay? badgeOverlay,
        Vector2 screenPos,
        Vector2 viewportSize,
        bool shiftPressed,
        bool doubleClick,
        out EntityId? nearestUnit,
        out ContactId? nearestContact)
    {
        nearestUnit = null;
        nearestContact = null;

        if (badgeOverlay is not null && badgeOverlay.TryHitBadge(screenPos, out EntityId? badgeUnit, out ContactId? badgeContact))
        {
            if (badgeContact is not null) nearestContact = badgeContact;
            else if (badgeUnit is not null) nearestUnit = badgeUnit;
        }

        if (nearestUnit is null && nearestContact is null)
        {
            float nearestUnitDistance = ClickPickRadiusPixels * ClickPickRadiusPixels;
            foreach ((EntityId id, UnitView view) in unitViews.Views)
            {
                Vector3 center = view.Root.Position + Vector3.Up;
                if (!TryProjectWorldPoint(camera, center, out Vector2 projected))
                    continue;

                float distance = projected.DistanceSquaredTo(screenPos);
                if (distance < nearestUnitDistance)
                {
                    nearestUnitDistance = distance;
                    nearestUnit = id;
                }
            }

            float nearestContactDistance = ClickPickRadiusPixels * ClickPickRadiusPixels;
            foreach ((ContactId id, ContactView view) in contactViews.Views)
            {
                Vector3 center = view.Root.Position + Vector3.Up * 1.5f;
                if (!TryProjectWorldPoint(camera, center, out Vector2 projected))
                    continue;

                float distance = projected.DistanceSquaredTo(screenPos);
                if (distance < nearestContactDistance)
                {
                    nearestContactDistance = distance;
                    nearestContact = id;
                }
            }

            if (nearestContact is not null && nearestUnit is not null && nearestContactDistance > nearestUnitDistance)
                nearestContact = null;
            else if (nearestContact is not null && nearestUnit is not null)
                nearestUnit = null;
        }

        if (nearestContact is not null)
        {
            unitViews.Selected.Clear();
            if (!shiftPressed) contactViews.Selected.Clear();
            contactViews.Selected.Add(nearestContact.Value);
        }
        else if (nearestUnit is not null)
        {
            contactViews.Selected.Clear();
            if (!shiftPressed) unitViews.Selected.Clear();

            if (doubleClick && unitViews.Views.TryGetValue(nearestUnit.Value, out UnitView? clickedView) && !string.IsNullOrEmpty(clickedView.UnitId))
            {
                CollectSameTypeOnScreen(camera, unitViews, nearestUnit.Value, viewportSize);
                unitViews.Selected.Add(nearestUnit.Value);
                for (int i = 0; i < _scratchUnits.Count; i++)
                    unitViews.Selected.Add(_scratchUnits[i]);
            }
            else
            {
                unitViews.Selected.Add(nearestUnit.Value);
            }
        }
        else
        {
            if (!shiftPressed)
            {
                unitViews.Selected.Clear();
                contactViews.Selected.Clear();
            }
        }
    }

    public static int TryGetControlGroupNumber(Key keycode) => keycode switch
    {
        Key.Key1 => 1,
        Key.Key2 => 2,
        Key.Key3 => 3,
        Key.Key4 => 4,
        Key.Key5 => 5,
        Key.Key6 => 6,
        Key.Key7 => 7,
        Key.Key8 => 8,
        Key.Key9 => 9,
        _ => 0
    };

    public static GroupKeyAction ClassifyGroupKey(
        Key keycode,
        bool ctrl,
        bool meta,
        bool shift,
        bool alt,
        out int groupNumber)
    {
        groupNumber = TryGetControlGroupNumber(keycode);
        if (groupNumber == 0 || alt)
            return GroupKeyAction.None;

        if (ctrl || meta)
            return GroupKeyAction.Store;

        if (shift)
            return GroupKeyAction.Add;

        return GroupKeyAction.Recall;
    }

    public static GroupKeyAction ClassifyGroupKey(Key keycode, bool ctrl, bool meta, bool shift, bool alt) =>
        ClassifyGroupKey(keycode, ctrl, meta, shift, alt, out _);

    public void StoreControlGroup(UnitViewSet unitViews, int groupNumber)
    {
        if (groupNumber < 1 || groupNumber > ControlGroupCount)
            return;

        HashSet<EntityId> group = unitViews.GetControlGroup(groupNumber);
        group.Clear();
        foreach (EntityId id in unitViews.Selected)
        {
            if (unitViews.Views.ContainsKey(id))
                group.Add(id);
        }
    }

    public void AddToControlGroup(UnitViewSet unitViews, int groupNumber)
    {
        if (groupNumber < 1 || groupNumber > ControlGroupCount)
            return;

        HashSet<EntityId> group = unitViews.GetControlGroup(groupNumber);
        foreach (EntityId id in unitViews.Selected)
        {
            if (unitViews.Views.ContainsKey(id))
                group.Add(id);
        }
    }

    public bool RecallControlGroup(
        UnitViewSet unitViews,
        ContactViewSet contactViews,
        CameraRig? cameraRig,
        int groupNumber,
        double currentTimeSeconds,
        out bool centeredCamera)
    {
        centeredCamera = false;
        if (groupNumber < 1 || groupNumber > ControlGroupCount)
            return false;

        HashSet<EntityId> group = unitViews.GetControlGroup(groupNumber);
        if (group.Count == 0)
            return false;

        _scratchUnits.Clear();
        Vector3 sumPosition = Vector3.Zero;
        foreach (EntityId id in group)
        {
            if (unitViews.Views.TryGetValue(id, out UnitView? view))
            {
                _scratchUnits.Add(id);
                sumPosition += view.Root.Position;
            }
        }

        if (_scratchUnits.Count == 0)
            return false;

        contactViews.Selected.Clear();
        unitViews.Selected.Clear();
        for (int i = 0; i < _scratchUnits.Count; i++)
            unitViews.Selected.Add(_scratchUnits[i]);

        double elapsed = currentTimeSeconds - _lastRecallTimeSeconds;
        bool isDoubleTap = _lastRecalledGroup == groupNumber && elapsed >= 0.0 && elapsed <= DoubleTapIntervalSeconds;
        _lastRecalledGroup = groupNumber;
        _lastRecallTimeSeconds = currentTimeSeconds;

        if (isDoubleTap && cameraRig is not null)
        {
            Vector3 averagePosition = sumPosition / _scratchUnits.Count;
            cameraRig.CenterOn(averagePosition);
            centeredCamera = true;
        }

        return true;
    }
}
