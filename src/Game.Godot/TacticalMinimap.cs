using System;
using System.Collections.Generic;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Tactical minimap rendered in the visual style of a Cold War military topographic map.
/// Displays terrain relief, water features, roads, crossings, and urban blocks.
/// Shows own units, hostile live contacts (Current), and frozen contact markers (LastKnown).
/// Hosts camera frustum visualization and left-click camera focus navigation.
/// Absorbs widget clicks to prevent accidental orders or unit selection changes.
/// </summary>
public partial class TacticalMinimap : Control
{
    public const float DefaultWidgetSize = 240f;
    public const float FramePadding = 4f;

    private Texture2D? _backgroundTexture;
    private StaticGeometry? _geometry;
    private HeightMap? _map;
    private UstyePlanData? _plan;
    private CameraRig? _cameraRig;
    private Camera3D? _camera;
    private IGameSession? _session;
    private UnitViewSet? _unitViews;
    private ContactViewSet? _contactViews;
    private UnitCatalog? _catalog;

    private float _mapWidth;
    private float _mapDepth;
    private Rect2 _mapRect;

    private Vector2 _hoverPos = new(-1000f, -1000f);
    private bool _isDraggingCamera;
    private ContactId? _hoveredContactId;

    private readonly PanelContainer _tooltipPanel;
    private readonly Label _tooltipName;
    private readonly Label _tooltipAge;
    private string[] _ageLabels = [];
    private string? _displayedName;
    private int _displayedAge = -1;

    // Pre-allocated buffers to guarantee zero per-frame steady-state allocations
    private readonly Vector2[] _diamondPoints = new Vector2[4];
    private readonly Vector2[][] _polyArrays = new Vector2[17][];

    public Rect2 MapRect => _mapRect;
    public bool IsTooltipVisible => _tooltipPanel.Visible;
    internal string ActiveTooltipText => $"{_tooltipName.Text} {_tooltipAge.Text}";
    public ContactId? HoveredContactId => _hoveredContactId;
    public bool IsDraggingCamera => _isDraggingCamera;
    public Texture2D? BackgroundTexture => _backgroundTexture;

    public int RenderedOwnUnitCount { get; private set; }
    public int RenderedContactCount { get; private set; }
    public int RenderedLastKnownCount { get; private set; }
    public int RenderedCurrentCount { get; private set; }

    public TacticalMinimap()
    {
        CustomMinimumSize = new Vector2(DefaultWidgetSize, DefaultWidgetSize);
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;

        for (int i = 3; i < _polyArrays.Length; i++)
        {
            _polyArrays[i] = new Vector2[i];
        }

        _tooltipPanel = new PanelContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 10
        };

        StyleBoxFlat tipStyle = new()
        {
            BgColor = new Color(0.11f, 0.13f, 0.10f, 0.95f),
            BorderColor = new Color(0.56f, 0.60f, 0.49f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
        _tooltipPanel.AddThemeStyleboxOverride("panel", tipStyle);

        HBoxContainer tooltipContent = new() { MouseFilter = MouseFilterEnum.Ignore };
        _tooltipName = CreateTooltipLabel();
        _tooltipAge = CreateTooltipLabel();
        tooltipContent.AddChild(_tooltipName);
        tooltipContent.AddChild(_tooltipAge);
        _tooltipPanel.AddChild(tooltipContent);
        AddChild(_tooltipPanel);
        MouseExited += ClearHover;

        UpdateMapRect();
    }

    private static Label CreateTooltipLabel()
    {
        Label label = new() { MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", new Color(0.92f, 0.88f, 0.79f));
        return label;
    }

    private void ClearHover()
    {
        _hoverPos = new Vector2(-1000f, -1000f);
        _hoveredContactId = null;
        _tooltipPanel.Visible = false;
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationResized)
        {
            UpdateMapRect();
            QueueRedraw();
        }
        else if (what is (int)NotificationApplicationFocusOut
            or (int)NotificationWMWindowFocusOut
            or (int)NotificationFocusExit)
        {
            CancelDrag();
        }
    }

    internal void CancelDrag()
    {
        if (_isDraggingCamera)
        {
            _isDraggingCamera = false;
            QueueRedraw();
        }
    }

    internal void Initialize(
        StaticGeometry scenarioGeometry,
        CameraRig? cameraRig,
        Camera3D? camera,
        IGameSession? session,
        UnitViewSet? unitViews,
        ContactViewSet? contactViews,
        UnitCatalog? catalog,
        UstyePlanData? plan = null)
    {
        _geometry = scenarioGeometry;
        _map = scenarioGeometry.HeightMap;
        _plan = plan;
        _cameraRig = cameraRig;
        _camera = camera;
        _session = session;
        _unitViews = unitViews;
        _contactViews = contactViews;
        _catalog = catalog;
        int cachedAgeTicks = Math.Clamp(catalog?.Detection?.MemoryDurationTicks ?? DetectionTuning.DefaultMemoryDurationTicks, 0, 60 * Simulation.TickRate);
        _ageLabels = new string[cachedAgeTicks + 1];
        for (int tick = 0; tick < _ageLabels.Length; tick++)
        {
            _ageLabels[tick] = FormatAge(tick);
        }
        _displayedAge = -1;
        _displayedName = null;

        _mapWidth = _map.Width;
        _mapDepth = _map.Depth;

        UpdateMapRect();
        _backgroundTexture = GenerateBackground(scenarioGeometry, _plan, 512, 512);
        QueueRedraw();
    }

    public void Reset()
    {
        _isDraggingCamera = false;
        _hoverPos = new Vector2(-1000f, -1000f);
        _hoveredContactId = null;
        if (_tooltipPanel is not null)
        {
            _tooltipPanel.Visible = false;
        }
        UpdateCounts();
        QueueRedraw();
    }

    public void UpdateMinimap(double delta)
    {
        UpdateCounts();
        UpdateTooltip();
        QueueRedraw();
    }

    private void UpdateCounts()
    {
        if (_session?.Replica is null)
        {
            RenderedOwnUnitCount = 0;
            RenderedContactCount = 0;
            RenderedCurrentCount = 0;
            RenderedLastKnownCount = 0;
            return;
        }

        RenderedOwnUnitCount = _session.Replica.OwnUnits.Length;
        ReadOnlySpan<ContactObservation> contacts = _session.Replica.Contacts;
        int current = 0;
        int lastKnown = 0;
        for (int i = 0; i < contacts.Length; i++)
        {
            if (contacts[i].State == ContactState.Current) current++;
            else if (contacts[i].State == ContactState.LastKnown) lastKnown++;
        }
        RenderedCurrentCount = current;
        RenderedLastKnownCount = lastKnown;
        RenderedContactCount = current + lastKnown;
    }

    public void UpdateMapRect()
    {
        float w = Size.X > 0f ? Size.X : DefaultWidgetSize;
        float h = Size.Y > 0f ? Size.Y : DefaultWidgetSize;
        Rect2 contentRect = new(FramePadding, FramePadding, MathF.Max(1f, w - FramePadding * 2f), MathF.Max(1f, h - FramePadding * 2f));

        if (_mapWidth <= 0f || _mapDepth <= 0f)
        {
            _mapRect = contentRect;
            return;
        }

        float mapAspect = _mapWidth / _mapDepth;
        float widgetAspect = contentRect.Size.X / contentRect.Size.Y;

        if (mapAspect >= widgetAspect)
        {
            float drawWidth = contentRect.Size.X;
            float drawHeight = drawWidth / mapAspect;
            float x = contentRect.Position.X;
            float y = contentRect.Position.Y + (contentRect.Size.Y - drawHeight) * 0.5f;
            _mapRect = new Rect2(x, y, drawWidth, drawHeight);
        }
        else
        {
            float drawHeight = contentRect.Size.Y;
            float drawWidth = drawHeight * mapAspect;
            float x = contentRect.Position.X + (contentRect.Size.X - drawWidth) * 0.5f;
            float y = contentRect.Position.Y;
            _mapRect = new Rect2(x, y, drawWidth, drawHeight);
        }
    }

    public Vector2 WorldToMinimap(Vector2 worldPos)
    {
        if (_mapWidth <= 0f || _mapDepth <= 0f) return _mapRect.Position;
        float normX = worldPos.X / _mapWidth;
        float normZ = worldPos.Y / _mapDepth;
        return new Vector2(
            _mapRect.Position.X + normX * _mapRect.Size.X,
            _mapRect.Position.Y + normZ * _mapRect.Size.Y);
    }

    public Vector2 WorldToMinimap(NumericsVector2 worldPos) =>
        WorldToMinimap(new Vector2(worldPos.X, worldPos.Y));

    public bool TryMinimapToWorld(Vector2 minimapPos, out Vector2 worldPos)
    {
        if (!IsPointOnMap(minimapPos) || _mapWidth <= 0f || _mapDepth <= 0f)
        {
            worldPos = Vector2.Zero;
            return false;
        }

        float normX = (minimapPos.X - _mapRect.Position.X) / _mapRect.Size.X;
        float normZ = (minimapPos.Y - _mapRect.Position.Y) / _mapRect.Size.Y;
        normX = Mathf.Clamp(normX, 0f, 1f);
        normZ = Mathf.Clamp(normZ, 0f, 1f);
        worldPos = new Vector2(normX * _mapWidth, normZ * _mapDepth);
        return true;
    }

    public Vector2 MinimapToWorldClamped(Vector2 minimapPos)
    {
        if (_mapWidth <= 0f || _mapDepth <= 0f) return Vector2.Zero;
        float normX = (minimapPos.X - _mapRect.Position.X) / _mapRect.Size.X;
        float normZ = (minimapPos.Y - _mapRect.Position.Y) / _mapRect.Size.Y;
        normX = Mathf.Clamp(normX, 0f, 1f);
        normZ = Mathf.Clamp(normZ, 0f, 1f);
        return new Vector2(normX * _mapWidth, normZ * _mapDepth);
    }

    public bool IsPointOnMap(Vector2 minimapPos)
    {
        const float eps = 0.001f;
        return minimapPos.X >= _mapRect.Position.X - eps &&
               minimapPos.X <= _mapRect.End.X + eps &&
               minimapPos.Y >= _mapRect.Position.Y - eps &&
               minimapPos.Y <= _mapRect.End.Y + eps;
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton button)
        {
            AcceptEvent();
            HandleMouseButton(button.Position, button.ButtonIndex, button.Pressed);
        }
        else if (@event is InputEventMouseMotion motion)
        {
            AcceptEvent();
            bool isLeftPressed = (motion.ButtonMask & MouseButtonMask.Left) != 0 || Input.IsMouseButtonPressed(MouseButton.Left);
            HandleMouseMotion(motion.Position, isLeftPressed);
        }
    }

    internal void SimulateHover(Vector2 minimapPos) => HandleMouseMotion(minimapPos, isLeftPressed: false);

    internal void SimulateClick(Vector2 minimapPos, MouseButton button, bool pressed = true) =>
        HandleMouseButton(minimapPos, button, pressed);

    internal void SimulateDrag(Vector2 minimapPos) => HandleMouseMotion(minimapPos, isLeftPressed: true);

    private void HandleMouseButton(Vector2 position, MouseButton button, bool pressed)
    {
        if (button != MouseButton.Left) return;

        _isDraggingCamera = pressed && IsPointOnMap(position);
        if (_isDraggingCamera) MoveCameraToMinimapPos(position);
        QueueRedraw();
    }

    private void HandleMouseMotion(Vector2 position, bool isLeftPressed = false)
    {
        _hoverPos = position;
        if (_isDraggingCamera)
        {
            if (!isLeftPressed)
            {
                _isDraggingCamera = false;
            }
            else if (IsPointOnMap(position))
            {
                MoveCameraToMinimapPos(position);
            }
        }
        UpdateTooltip();
        QueueRedraw();
    }

    private void MoveCameraToMinimapPos(Vector2 minimapPos)
    {
        if (_cameraRig is null || !IsPointOnMap(minimapPos)) return;
        Vector2 worldPos = MinimapToWorldClamped(minimapPos);
        float groundY = _map?.GetHeight(worldPos.X, worldPos.Y) ?? 0f;
        _cameraRig.CenterOn(new Vector3(worldPos.X, groundY, worldPos.Y));
    }

    private void UpdateTooltip()
    {
        if (_session?.Replica is null || !IsPointOnMap(_hoverPos))
        {
            _tooltipPanel.Visible = false;
            _hoveredContactId = null;
            return;
        }

        ReadOnlySpan<ContactObservation> contacts = _session.Replica.Contacts;
        ContactObservation? found = null;
        float closestDist = float.MaxValue;

        for (int i = 0; i < contacts.Length; i++)
        {
            ref readonly ContactObservation c = ref contacts[i];
            if (c.State != ContactState.LastKnown) continue;
            Vector2 mPos = WorldToMinimap(new Vector2(c.Position.X, c.Position.Y));
            float dist = _hoverPos.DistanceTo(mPos);
            if (dist <= TacticalMinimapTheme.HoverHitRadius && dist < closestDist)
            {
                closestDist = dist;
                found = c;
            }
        }

        if (found is null)
        {
            _tooltipPanel.Visible = false;
            _hoveredContactId = null;
            return;
        }

        ContactObservation contact = found.Value;
        _hoveredContactId = contact.Id;
        int ageTicks = Math.Max(0, _session.Tick - contact.LastSeenTick);
        string typeName = GetUnitTypeName(contact.Type, contact.Classification);
        if (_displayedName != typeName)
        {
            _tooltipName.Text = typeName;
            _displayedName = typeName;
        }
        if (_displayedAge != ageTicks)
        {
            _tooltipAge.Text = ageTicks < _ageLabels.Length ? _ageLabels[ageTicks] : FormatAge(ageTicks);
            _displayedAge = ageTicks;
        }

        Vector2 markerPos = WorldToMinimap(new Vector2(contact.Position.X, contact.Position.Y));
        Vector2 tipSize = _tooltipPanel.GetCombinedMinimumSize();
        Vector2 desiredPos = markerPos + new Vector2(8f, -tipSize.Y - 2f);

        float maxX = Size.X - tipSize.X - 4f;
        float maxY = Size.Y - tipSize.Y - 4f;
        float clampedX = Mathf.Clamp(desiredPos.X, 4f, MathF.Max(4f, maxX));
        float clampedY = Mathf.Clamp(desiredPos.Y, 4f, MathF.Max(4f, maxY));

        _tooltipPanel.Position = new Vector2(clampedX, clampedY);
        _tooltipPanel.Visible = true;
    }

    private static string FormatAge(int ticks) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"[Last seen {ticks / (float)Simulation.TickRate:0.0}s ago]");

    private string GetUnitTypeName(ushort type, ContactClassification classification)
    {
        if (_catalog?.Units is not null && type < _catalog.Units.Length)
        {
            UnitDefinition def = _catalog.Units[type];
            if (!string.IsNullOrEmpty(def.Name)) return def.Name;
            if (!string.IsNullOrEmpty(def.Classification)) return def.Classification;
            if (!string.IsNullOrEmpty(def.Id)) return def.Id;
        }

        return classification switch
        {
            ContactClassification.Tank => "Tank",
            ContactClassification.Recon => "Recon",
            ContactClassification.Command or ContactClassification.CommandVehicle => "Command",
            ContactClassification.Apc => "APC",
            _ => "Vehicle"
        };
    }

    public override void _Draw()
    {
        // 1. Overall widget background panel
        DrawRect(new Rect2(Vector2.Zero, Size), TacticalMinimapTheme.WidgetBackground, filled: true);

        // 2. Pillarbox / letterbox margins (fields) outside the active map
        if (_mapRect.Position.Y > FramePadding)
        {
            DrawRect(new Rect2(FramePadding, FramePadding, Size.X - FramePadding * 2f, _mapRect.Position.Y - FramePadding), TacticalMinimapTheme.FieldColor, filled: true);
            float bottomY = _mapRect.End.Y;
            DrawRect(new Rect2(FramePadding, bottomY, Size.X - FramePadding * 2f, Size.Y - FramePadding - bottomY), TacticalMinimapTheme.FieldColor, filled: true);
        }
        if (_mapRect.Position.X > FramePadding)
        {
            DrawRect(new Rect2(FramePadding, FramePadding, _mapRect.Position.X - FramePadding, Size.Y - FramePadding * 2f), TacticalMinimapTheme.FieldColor, filled: true);
            float rightX = _mapRect.End.X;
            DrawRect(new Rect2(rightX, FramePadding, Size.X - FramePadding - rightX, Size.Y - FramePadding * 2f), TacticalMinimapTheme.FieldColor, filled: true);
        }

        // 3. Topographic map background texture
        if (_backgroundTexture is not null)
        {
            DrawTextureRect(_backgroundTexture, _mapRect, false);
        }

        // 4. Subtle military coordinate grid
        DrawCoordinateGrid();

        // 5. Border around map content
        DrawRect(_mapRect, TacticalMinimapTheme.FrameBorder, filled: false, width: 1.2f);

        // 6. Camera frustum ground footprint
        DrawCameraFrustum();

        // 7. Friendly unit markers
        DrawFriendlyUnits();

        // 8. Enemy contact markers
        DrawEnemyContacts();

        // 9. Outer frame border
        DrawRect(new Rect2(Vector2.Zero, Size), TacticalMinimapTheme.FrameBorder, filled: false, width: 1.5f);
    }

    private void DrawCoordinateGrid()
    {
        const int divisions = 4;
        for (int i = 1; i < divisions; i++)
        {
            float norm = i / (float)divisions;
            float gx = _mapRect.Position.X + norm * _mapRect.Size.X;
            float gy = _mapRect.Position.Y + norm * _mapRect.Size.Y;

            DrawLine(
                new Vector2(gx, _mapRect.Position.Y),
                new Vector2(gx, _mapRect.End.Y),
                TacticalMinimapTheme.GridLineColor,
                1.0f);

            DrawLine(
                new Vector2(_mapRect.Position.X, gy),
                new Vector2(_mapRect.End.X, gy),
                TacticalMinimapTheme.GridLineColor,
                1.0f);
        }
    }

    private void DrawCameraFrustum()
    {
        if (_camera is null || _mapWidth <= 0f || _mapDepth <= 0f) return;

        Span<Vector2> clipped = stackalloc Vector2[16];
        int count = ComputeClippedFrustum(clipped);
        if (count < 3) return;

        Span<Vector2> minimapPts = stackalloc Vector2[count];
        for (int i = 0; i < count; i++)
        {
            minimapPts[i] = WorldToMinimap(clipped[i]);
        }

        // Fill using pre-allocated array matching the vertex count
        if (count < _polyArrays.Length && _polyArrays[count] is not null)
        {
            Vector2[] poly = _polyArrays[count];
            for (int i = 0; i < count; i++) poly[i] = minimapPts[i];
            DrawColoredPolygon(poly, TacticalMinimapTheme.CameraFrustumFill);
        }

        // Outline
        for (int i = 0; i < count; i++)
        {
            Vector2 p0 = minimapPts[i];
            Vector2 p1 = minimapPts[(i + 1) % count];
            DrawLine(p0, p1, TacticalMinimapTheme.CameraFrustumOutline, TacticalMinimapTheme.CameraFrustumLineWidth, antialiased: true);
        }
    }

    internal int ComputeClippedFrustum(Span<Vector2> output)
    {
        if (_camera is null || _mapWidth <= 0f || _mapDepth <= 0f) return 0;

        Vector2 vpSize = _camera.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1280f, 720f);
        if (vpSize.X <= 0f || vpSize.Y <= 0f) vpSize = new Vector2(1280f, 720f);

        float focusY = _cameraRig?.Focus.Y ?? 0f;

        Span<Vector2> screenCorners = stackalloc Vector2[4]
        {
            Vector2.Zero,
            new Vector2(vpSize.X, 0f),
            vpSize,
            new Vector2(0f, vpSize.Y)
        };

        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 4; i++)
        {
            corners[i] = _camera.ProjectPosition(screenCorners[i], _camera.Near);
            corners[i + 4] = _camera.ProjectPosition(screenCorners[i], _camera.Far);
        }

        // Intersect the finite view volume, including when upper rays look above the horizon.
        Span<Vector2> section = stackalloc Vector2[12];
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            AddPlaneIntersection(corners[i], corners[next], focusY, section, ref count);
            AddPlaneIntersection(corners[i + 4], corners[next + 4], focusY, section, ref count);
            AddPlaneIntersection(corners[i], corners[i + 4], focusY, section, ref count);
        }

        if (count < 3) return 0;

        Vector2 center = Vector2.Zero;
        for (int i = 0; i < count; i++) center += section[i];
        center /= count;

        // At most six unique vertices. Sort around the center before rectangle clipping.
        for (int i = 1; i < count; i++)
        {
            Vector2 point = section[i];
            float angle = (point - center).Angle();
            int j = i;
            while (j > 0 && (section[j - 1] - center).Angle() > angle)
            {
                section[j] = section[j - 1];
                j--;
            }
            section[j] = point;
        }

        return ClipConvexPolygonToRect(section[..count], output, 0f, _mapWidth, 0f, _mapDepth);
    }

    private static void AddPlaneIntersection(
        Vector3 a, Vector3 b, float height, Span<Vector2> section, ref int count)
    {
        if ((a.Y > height) == (b.Y > height)) return;
        Vector3 hit = a.Lerp(b, (height - a.Y) / (b.Y - a.Y));
        Vector2 point = new(hit.X, hit.Z);
        for (int i = 0; i < count; i++)
        {
            if (section[i].DistanceSquaredTo(point) < 0.000001f) return;
        }
        section[count++] = point;
    }

    public static int ClipConvexPolygonToRect(
        ReadOnlySpan<Vector2> input,
        Span<Vector2> output,
        float xMin, float xMax, float zMin, float zMax)
    {
        if (input.Length < 3) return 0;

        Span<Vector2> bufA = stackalloc Vector2[16];
        Span<Vector2> bufB = stackalloc Vector2[16];

        for (int i = 0; i < input.Length; i++) bufA[i] = input[i];
        int count = input.Length;

        count = ClipAgainstPlane(bufA.Slice(0, count), bufB, true, true, xMin);
        if (count < 3) return 0;

        count = ClipAgainstPlane(bufB.Slice(0, count), bufA, true, false, xMax);
        if (count < 3) return 0;

        count = ClipAgainstPlane(bufA.Slice(0, count), bufB, false, true, zMin);
        if (count < 3) return 0;

        count = ClipAgainstPlane(bufB.Slice(0, count), output, false, false, zMax);
        return count;
    }

    private static int ClipAgainstPlane(
        ReadOnlySpan<Vector2> input,
        Span<Vector2> output,
        bool isX, bool isGreater, float planeVal)
    {
        int outCount = 0;
        Vector2 prev = input[^1];
        bool prevInside = IsInside(prev, isX, isGreater, planeVal);

        for (int i = 0; i < input.Length; i++)
        {
            Vector2 curr = input[i];
            bool currInside = IsInside(curr, isX, isGreater, planeVal);

            if (currInside)
            {
                if (!prevInside)
                {
                    output[outCount++] = Intersect(prev, curr, isX, planeVal);
                }
                output[outCount++] = curr;
            }
            else if (prevInside)
            {
                output[outCount++] = Intersect(prev, curr, isX, planeVal);
            }

            prev = curr;
            prevInside = currInside;
        }

        return outCount;
    }

    private static bool IsInside(Vector2 p, bool isX, bool isGreater, float planeVal)
    {
        float val = isX ? p.X : p.Y;
        return isGreater ? val >= planeVal : val <= planeVal;
    }

    private static Vector2 Intersect(Vector2 p1, Vector2 p2, bool isX, float planeVal)
    {
        float val1 = isX ? p1.X : p1.Y;
        float val2 = isX ? p2.X : p2.Y;
        float t = MathF.Abs(val2 - val1) > 1e-6f ? (planeVal - val1) / (val2 - val1) : 0f;
        return p1 + (p2 - p1) * t;
    }

    private void DrawFriendlyUnits()
    {
        RenderedOwnUnitCount = 0;
        if (_session?.Replica is null) return;

        ReadOnlySpan<OwnUnitObservation> units = _session.Replica.OwnUnits;
        for (int i = 0; i < units.Length; i++)
        {
            ref readonly OwnUnitObservation u = ref units[i];
            Vector2 mPos = WorldToMinimap(new Vector2(u.Position.X, u.Position.Y));

            // Solid circle for friendly unit
            DrawCircle(mPos, TacticalMinimapTheme.FriendlyMarkerRadius, TacticalMinimapTheme.FriendlyColor);
            DrawArc(mPos, TacticalMinimapTheme.FriendlyMarkerRadius, 0f, MathF.Tau, 12, TacticalMinimapTheme.FriendlyBorderColor, 1.0f);

            // Prominent selection ring
            if (_unitViews?.Selected.Contains(u.Id) ?? false)
            {
                DrawArc(mPos, TacticalMinimapTheme.SelectedRingRadius, 0f, MathF.Tau, 16, TacticalMinimapTheme.FriendlySelectedRing, TacticalMinimapTheme.SelectedRingWidth);
            }

            RenderedOwnUnitCount++;
        }
    }

    private void DrawEnemyContacts()
    {
        RenderedContactCount = 0;
        RenderedCurrentCount = 0;
        RenderedLastKnownCount = 0;
        if (_session?.Replica is null) return;

        ReadOnlySpan<ContactObservation> contacts = _session.Replica.Contacts;
        for (int i = 0; i < contacts.Length; i++)
        {
            ref readonly ContactObservation c = ref contacts[i];
            Vector2 mPos = WorldToMinimap(new Vector2(c.Position.X, c.Position.Y));

            if (c.State == ContactState.Current)
            {
                // Solid filled diamond
                DrawFilledDiamond(mPos, TacticalMinimapTheme.EnemyDiamondRadius, TacticalMinimapTheme.EnemyCurrentColor, TacticalMinimapTheme.EnemyCurrentBorderColor);
                RenderedCurrentCount++;
                RenderedContactCount++;
            }
            else if (c.State == ContactState.LastKnown)
            {
                float halfSize = TacticalMinimapTheme.EnemyLastKnownHalfSize;
                Vector2 corner = mPos - new Vector2(halfSize, halfSize);
                DrawRect(new Rect2(corner, new Vector2(2f * halfSize, 2f * halfSize)),
                    TacticalMinimapTheme.EnemyLastKnownColor, filled: false,
                    width: TacticalMinimapTheme.EnemyLastKnownLineWidth);
                RenderedLastKnownCount++;
                RenderedContactCount++;
            }
        }
    }

    private void DrawFilledDiamond(Vector2 center, float r, Color fill, Color border)
    {
        _diamondPoints[0] = new Vector2(center.X, center.Y - r);
        _diamondPoints[1] = new Vector2(center.X + r, center.Y);
        _diamondPoints[2] = new Vector2(center.X, center.Y + r);
        _diamondPoints[3] = new Vector2(center.X - r, center.Y);

        DrawColoredPolygon(_diamondPoints, fill);
        DrawLine(_diamondPoints[0], _diamondPoints[1], border, 1.0f);
        DrawLine(_diamondPoints[1], _diamondPoints[2], border, 1.0f);
        DrawLine(_diamondPoints[2], _diamondPoints[3], border, 1.0f);
        DrawLine(_diamondPoints[3], _diamondPoints[0], border, 1.0f);
    }

    public static ImageTexture GenerateBackground(
        StaticGeometry geometry,
        UstyePlanData? plan = null,
        int width = 512,
        int height = 512)
    {
        float mapWidth = geometry.HeightMap.Width;
        float mapDepth = geometry.HeightMap.Depth;

        int texWidth = width;
        int texHeight = height;
        if (mapWidth > 0f && mapDepth > 0f)
        {
            if (mapWidth >= mapDepth)
            {
                texWidth = width;
                texHeight = Math.Max(64, (int)(width * mapDepth / mapWidth));
            }
            else
            {
                texHeight = height;
                texWidth = Math.Max(64, (int)(height * mapWidth / mapDepth));
            }
        }

        byte[] pixels = new byte[texWidth * texHeight * 4];

        // 1. Fill base land color
        FillBuffer(pixels, TacticalMinimapTheme.LandColor);

        // 2. Contour lines from HeightMap
        RasterizeContours(pixels, texWidth, texHeight, geometry.HeightMap);

        // 3. Water features
        if (plan is not null)
        {
            RasterizeUstyeWater(pixels, texWidth, texHeight, plan, geometry);
        }
        else
        {
            RasterizeGenericWater(pixels, texWidth, texHeight, geometry, mapWidth, mapDepth);
        }

        // 4. Urban patches / blocks
        if (plan is not null)
        {
            RasterizeUstyeUrban(pixels, texWidth, texHeight, plan);
        }
        else
        {
            RasterizeGenericUrban(pixels, texWidth, texHeight, geometry, mapWidth, mapDepth);
        }

        // 5. Roads
        if (plan is not null)
        {
            RasterizeUstyeRoads(pixels, texWidth, texHeight, plan);
        }

        // 6. Crossings / Bridges
        if (plan is not null)
        {
            RasterizeUstyeCrossings(pixels, texWidth, texHeight, plan);
        }
        else
        {
            RasterizeGenericBridges(pixels, texWidth, texHeight, geometry, mapWidth, mapDepth);
        }

        Image image = Image.CreateEmpty(texWidth, texHeight, false, Image.Format.Rgba8);
        image.SetData(texWidth, texHeight, false, Image.Format.Rgba8, pixels);
        return ImageTexture.CreateFromImage(image);
    }

    private static void FillBuffer(byte[] pixels, Color color)
    {
        byte r = (byte)(color.R * 255f);
        byte g = (byte)(color.G * 255f);
        byte b = (byte)(color.B * 255f);
        byte a = (byte)(color.A * 255f);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }
    }

    private static void SetPixel(byte[] pixels, int w, int h, int x, int y, Color color)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int idx = (y * w + x) * 4;
        pixels[idx] = (byte)(color.R * 255f);
        pixels[idx + 1] = (byte)(color.G * 255f);
        pixels[idx + 2] = (byte)(color.B * 255f);
        pixels[idx + 3] = (byte)(color.A * 255f);
    }

    private static void BlendPixel(byte[] pixels, int w, int h, int x, int y, Color color)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int idx = (y * w + x) * 4;
        float alpha = color.A;
        float invAlpha = 1f - alpha;

        byte r = (byte)(color.R * 255f * alpha + pixels[idx] * invAlpha);
        byte g = (byte)(color.G * 255f * alpha + pixels[idx + 1] * invAlpha);
        byte b = (byte)(color.B * 255f * alpha + pixels[idx + 2] * invAlpha);

        pixels[idx] = r;
        pixels[idx + 1] = g;
        pixels[idx + 2] = b;
    }

    private static void RasterizeContours(byte[] pixels, int w, int h, HeightMap map)
    {
        const float contourInterval = 8.0f;
        const float indexInterval = 40.0f;

        float stepX = map.Width / MathF.Max(1, w - 1);
        float stepZ = map.Depth / MathF.Max(1, h - 1);

        float[] currRow = new float[w];
        float[] nextRow = new float[w];

        for (int x = 0; x < w; x++)
        {
            currRow[x] = map.GetHeight(x * stepX, 0f);
        }

        for (int y = 0; y < h; y++)
        {
            float nextZ = (y + 1 < h) ? (y + 1) * stepZ : y * stepZ;
            for (int x = 0; x < w; x++)
            {
                nextRow[x] = map.GetHeight(x * stepX, nextZ);
            }

            for (int x = 0; x < w; x++)
            {
                float hVal = currRow[x];
                if (hVal < 1.0f) continue; // Skip water level

                int level = (int)MathF.Floor(hVal / contourInterval);
                float hRight = (x + 1 < w) ? currRow[x + 1] : hVal;
                float hDown = (y + 1 < h) ? nextRow[x] : hVal;

                int levelRight = (int)MathF.Floor(hRight / contourInterval);
                int levelDown = (int)MathF.Floor(hDown / contourInterval);

                if (level != levelRight || level != levelDown)
                {
                    bool isIndex = (int)MathF.Floor(hVal / indexInterval) != (int)MathF.Floor(hRight / indexInterval) ||
                                   (int)MathF.Floor(hVal / indexInterval) != (int)MathF.Floor(hDown / indexInterval);

                    SetPixel(pixels, w, h, x, y, isIndex ? TacticalMinimapTheme.LandContourIndexColor : TacticalMinimapTheme.LandContourColor);
                }
            }

            Array.Copy(nextRow, currRow, w);
        }
    }

    private static void RasterizeUstyeWater(
        byte[] pixels,
        int w,
        int h,
        UstyePlanData plan,
        StaticGeometry geometry)
    {
        float mapW = plan.ExtentX;
        float mapD = plan.ExtentZ;
        float riverRadiusPx = (plan.RiverWidth * 0.5f / mapW) * w;

        // 1. Rasterize river spline segments
        NumericsVector2[] river = plan.RiverSpline.Length > 0 ? plan.RiverSpline : plan.River;
        for (int i = 0; i < river.Length - 1; i++)
        {
            Vector2 p0 = WorldToPixel(river[i].X, river[i].Y, mapW, mapD, w, h);
            Vector2 p1 = WorldToPixel(river[i + 1].X, river[i + 1].Y, mapW, mapD, w, h);
            RasterizeCapsule(pixels, w, h, p0, p1, riverRadiusPx, TacticalMinimapTheme.WaterColor);
        }

        // 2. Rasterize bay water slices and obstacles
        IReadOnlyList<TerrainObstacle> obstacles = geometry.TerrainObstacles;
        for (int i = 0; i < obstacles.Count; i++)
        {
            TerrainObstacle obs = obstacles[i];
            if ((obs.RequiredCapability & TraversabilityCapabilities.Water) != 0)
            {
                RasterizeRectWorld(pixels, w, h, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ, mapW, mapD, TacticalMinimapTheme.WaterColor);
            }
        }
    }

    private static void RasterizeGenericWater(
        byte[] pixels,
        int w,
        int h,
        StaticGeometry geometry,
        float mapW,
        float mapD)
    {
        IReadOnlyList<TerrainObstacle> obstacles = geometry.TerrainObstacles;
        for (int i = 0; i < obstacles.Count; i++)
        {
            TerrainObstacle obs = obstacles[i];
            if ((obs.RequiredCapability & TraversabilityCapabilities.Water) != 0)
            {
                RasterizeRectWorld(pixels, w, h, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ, mapW, mapD, TacticalMinimapTheme.WaterColor);
            }
        }
    }

    private static void RasterizeUstyeUrban(byte[] pixels, int w, int h, UstyePlanData plan)
    {
        float mapW = plan.ExtentX;
        float mapD = plan.ExtentZ;

        // Urban region block backgrounds
        for (int i = 0; i < plan.Regions.Length; i++)
        {
            UstyePlanData.Region reg = plan.Regions[i];
            if (reg.Kind is "urban" or "industrial")
            {
                RasterizeConvexPolygonWorld(pixels, w, h, reg.Polygon, mapW, mapD, new Color(TacticalMinimapTheme.UrbanColor.R, TacticalMinimapTheme.UrbanColor.G, TacticalMinimapTheme.UrbanColor.B, 0.65f));
            }
        }

        // Concrete structure footprints
        for (int i = 0; i < plan.Structures.Length; i++)
        {
            UstyePlanData.Structure st = plan.Structures[i];
            RasterizeRectWorld(
                pixels, w, h,
                st.Min.X, st.Min.Y,
                st.Min.X + st.Size.X, st.Min.Y + st.Size.Y,
                mapW, mapD,
                TacticalMinimapTheme.UrbanColor,
                TacticalMinimapTheme.UrbanBorderColor);
        }
    }

    private static void RasterizeGenericUrban(
        byte[] pixels,
        int w,
        int h,
        StaticGeometry geometry,
        float mapW,
        float mapD)
    {
        IReadOnlyList<OpaqueStructure> structures = geometry.Structures;
        for (int i = 0; i < structures.Count; i++)
        {
            OpaqueStructure st = structures[i];
            RasterizeRectWorld(
                pixels, w, h,
                st.MinX, st.MinZ,
                st.MaxX, st.MaxZ,
                mapW, mapD,
                TacticalMinimapTheme.UrbanColor,
                TacticalMinimapTheme.UrbanBorderColor);
        }
    }

    private static void RasterizeUstyeRoads(byte[] pixels, int w, int h, UstyePlanData plan)
    {
        float mapW = plan.ExtentX;
        float mapD = plan.ExtentZ;

        // Draw main roads
        for (int i = 0; i < plan.Roads.Length; i++)
        {
            UstyePlanData.Road road = plan.Roads[i];
            NumericsVector2[] pts = road.Points;
            for (int p = 0; p < pts.Length - 1; p++)
            {
                Vector2 p0 = WorldToPixel(pts[p].X, pts[p].Y, mapW, mapD, w, h);
                Vector2 p1 = WorldToPixel(pts[p + 1].X, pts[p + 1].Y, mapW, mapD, w, h);
                RasterizeLine(pixels, w, h, p0, p1, TacticalMinimapTheme.RoadCasingColor, 4);
                RasterizeLine(pixels, w, h, p0, p1, TacticalMinimapTheme.RoadColor, 2);
            }
        }

        // Draw access roads
        for (int i = 0; i < plan.AccessRoads.Length; i++)
        {
            UstyePlanData.Road road = plan.AccessRoads[i];
            NumericsVector2[] pts = road.Points;
            for (int p = 0; p < pts.Length - 1; p++)
            {
                Vector2 p0 = WorldToPixel(pts[p].X, pts[p].Y, mapW, mapD, w, h);
                Vector2 p1 = WorldToPixel(pts[p + 1].X, pts[p + 1].Y, mapW, mapD, w, h);
                RasterizeLine(pixels, w, h, p0, p1, TacticalMinimapTheme.RoadCasingColor, 3);
                RasterizeLine(pixels, w, h, p0, p1, TacticalMinimapTheme.RoadColor, 1);
            }
        }
    }

    private static void RasterizeUstyeCrossings(byte[] pixels, int w, int h, UstyePlanData plan)
    {
        float mapW = plan.ExtentX;
        float mapD = plan.ExtentZ;

        for (int i = 0; i < plan.Crossings.Length; i++)
        {
            UstyePlanData.Crossing c = plan.Crossings[i];
            Vector2 p0 = WorldToPixel(c.EndA.X, c.EndA.Y, mapW, mapD, w, h);
            Vector2 p1 = WorldToPixel(c.EndB.X, c.EndB.Y, mapW, mapD, w, h);
            float deckWidthPx = MathF.Max(TacticalMinimapTheme.CrossingMinimumTextureWidth, (c.DeckWidth / mapW) * w);
            RasterizeThickSegment(pixels, w, h, p0, p1, deckWidthPx, TacticalMinimapTheme.CrossingColor, TacticalMinimapTheme.CrossingBorderColor);
        }
    }

    private static void RasterizeGenericBridges(
        byte[] pixels,
        int w,
        int h,
        StaticGeometry geometry,
        float mapW,
        float mapD)
    {
        IReadOnlyList<BridgeStructure> bridges = geometry.Bridges;
        for (int i = 0; i < bridges.Count; i++)
        {
            BridgeStructure b = bridges[i];
            Vector2 p0 = WorldToPixel(b.Start.X, b.Start.Y, mapW, mapD, w, h);
            Vector2 p1 = WorldToPixel(b.End.X, b.End.Y, mapW, mapD, w, h);
            float widthPx = MathF.Max(3f, (b.Width / mapW) * w);
            RasterizeThickSegment(pixels, w, h, p0, p1, widthPx, TacticalMinimapTheme.CrossingColor, TacticalMinimapTheme.CrossingBorderColor);
        }
    }

    private static Vector2 WorldToPixel(float wx, float wz, float mapW, float mapD, int texW, int texH)
    {
        float px = (wx / mapW) * (texW - 1);
        float py = (wz / mapD) * (texH - 1);
        return new Vector2(px, py);
    }

    private static void RasterizeCapsule(byte[] pixels, int w, int h, Vector2 p0, Vector2 p1, float radius, Color color)
    {
        int minX = (int)MathF.Max(0f, MathF.Min(p0.X, p1.X) - radius - 1f);
        int maxX = (int)MathF.Min(w - 1, MathF.Max(p0.X, p1.X) + radius + 1f);
        int minY = (int)MathF.Max(0f, MathF.Min(p0.Y, p1.Y) - radius - 1f);
        int maxY = (int)MathF.Min(h - 1, MathF.Max(p0.Y, p1.Y) + radius + 1f);

        Vector2 d = p1 - p0;
        float dLenSq = d.LengthSquared();
        float rSq = radius * radius;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 pt = new(x + 0.5f, y + 0.5f);
                float distSq;
                if (dLenSq < 1e-4f)
                {
                    distSq = pt.DistanceSquaredTo(p0);
                }
                else
                {
                    float t = Mathf.Clamp(d.Dot(pt - p0) / dLenSq, 0f, 1f);
                    Vector2 proj = p0 + d * t;
                    distSq = pt.DistanceSquaredTo(proj);
                }

                if (distSq <= rSq)
                {
                    SetPixel(pixels, w, h, x, y, color);
                }
            }
        }
    }

    private static void RasterizeRectWorld(
        byte[] pixels,
        int w,
        int h,
        float minX, float minZ, float maxX, float maxZ,
        float mapW, float mapD,
        Color fill,
        Color? border = null)
    {
        int px0 = (int)MathF.Max(0f, (minX / mapW) * (w - 1));
        int py0 = (int)MathF.Max(0f, (minZ / mapD) * (h - 1));
        int px1 = (int)MathF.Min(w - 1, (maxX / mapW) * (w - 1));
        int py1 = (int)MathF.Min(h - 1, (maxZ / mapD) * (h - 1));

        for (int y = py0; y <= py1; y++)
        {
            for (int x = px0; x <= px1; x++)
            {
                if (border.HasValue && (x == px0 || x == px1 || y == py0 || y == py1))
                {
                    SetPixel(pixels, w, h, x, y, border.Value);
                }
                else
                {
                    SetPixel(pixels, w, h, x, y, fill);
                }
            }
        }
    }

    private static void RasterizeConvexPolygonWorld(
        byte[] pixels,
        int w,
        int h,
        NumericsVector2[] polygon,
        float mapW,
        float mapD,
        Color color)
    {
        if (polygon.Length < 3) return;

        Span<Vector2> pts = stackalloc Vector2[polygon.Length];
        int minY = h - 1;
        int maxY = 0;

        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 p = WorldToPixel(polygon[i].X, polygon[i].Y, mapW, mapD, w, h);
            pts[i] = p;
            int py = (int)p.Y;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }

        minY = Math.Max(0, minY);
        maxY = Math.Min(h - 1, maxY);

        for (int y = minY; y <= maxY; y++)
        {
            float scanY = y + 0.5f;
            float minX = float.MaxValue;
            float maxX = float.MinValue;

            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 p0 = pts[i];
                Vector2 p1 = pts[(i + 1) % pts.Length];

                if ((p0.Y <= scanY && p1.Y > scanY) || (p1.Y <= scanY && p0.Y > scanY))
                {
                    float t = (scanY - p0.Y) / (p1.Y - p0.Y);
                    float ix = p0.X + (p1.X - p0.X) * t;
                    if (ix < minX) minX = ix;
                    if (ix > maxX) maxX = ix;
                }
            }

            if (minX <= maxX)
            {
                int x0 = (int)MathF.Max(0f, minX);
                int x1 = (int)MathF.Min(w - 1, maxX);
                for (int x = x0; x <= x1; x++)
                {
                    BlendPixel(pixels, w, h, x, y, color);
                }
            }
        }
    }

    private static void RasterizeLine(byte[] pixels, int w, int h, Vector2 p0, Vector2 p1, Color color, int thickness)
    {
        int x0 = (int)p0.X;
        int y0 = (int)p0.Y;
        int x1 = (int)p1.X;
        int y1 = (int)p1.Y;

        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        int halfThick = thickness / 2;

        while (true)
        {
            for (int ty = -halfThick; ty <= halfThick; ty++)
            {
                for (int tx = -halfThick; tx <= halfThick; tx++)
                {
                    SetPixel(pixels, w, h, x0 + tx, y0 + ty, color);
                }
            }

            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private static void RasterizeThickSegment(
        byte[] pixels,
        int w,
        int h,
        Vector2 p0,
        Vector2 p1,
        float width,
        Color fill,
        Color border)
    {
        Vector2 d = p1 - p0;
        float len = d.Length();
        if (len < 1e-4f) return;

        Vector2 perp = new Vector2(-d.Y, d.X) / len * (width * 0.5f);
        Vector2 c0 = p0 - perp;
        Vector2 c1 = p0 + perp;
        Vector2 c2 = p1 + perp;
        Vector2 c3 = p1 - perp;

        int minX = (int)MathF.Max(0f, MathF.Min(MathF.Min(c0.X, c1.X), MathF.Min(c2.X, c3.X)) - 1f);
        int maxX = (int)MathF.Min(w - 1, MathF.Max(MathF.Max(c0.X, c1.X), MathF.Max(c2.X, c3.X)) + 1f);
        int minY = (int)MathF.Max(0f, MathF.Min(MathF.Min(c0.Y, c1.Y), MathF.Min(c2.Y, c3.Y)) - 1f);
        int maxY = (int)MathF.Min(h - 1, MathF.Max(MathF.Max(c0.Y, c1.Y), MathF.Max(c2.Y, c3.Y)) + 1f);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 pt = new(x + 0.5f, y + 0.5f);
                float t = Mathf.Clamp(d.Dot(pt - p0) / (len * len), 0f, 1f);
                Vector2 proj = p0 + d * t;
                float dist = pt.DistanceTo(proj);

                if (dist <= width * 0.5f)
                {
                    bool isBorder = dist >= width * 0.5f - 1.0f || t <= 0.05f || t >= 0.95f;
                    SetPixel(pixels, w, h, x, y, isBorder ? border : fill);
                }
            }
        }
    }
}
