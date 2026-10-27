using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Controls pre-click order preview, cursor states, group validation, and order outcome tracking.
/// Designed for zero steady-state per-frame GC allocations.
/// </summary>
internal sealed class OrderPreviewController
{
    private readonly struct ProfileKey : System.IEquatable<ProfileKey>
    {
        public readonly float Radius;
        public readonly TraversabilityCapabilities Capabilities;
        public readonly uint SupportSurface;

        public ProfileKey(float radius, TraversabilityCapabilities capabilities, uint supportSurface)
        {
            Radius = radius;
            Capabilities = capabilities;
            SupportSurface = supportSurface;
        }

        public bool Equals(ProfileKey other) =>
            Radius == other.Radius &&
            Capabilities == other.Capabilities &&
            SupportSurface == other.SupportSurface;
    }

    private struct CachedProfileEvaluation
    {
        public ProfileKey Key;
        public DestinationEvaluationResult Result;
    }

    private struct TrackedUnitOrder
    {
        public EntityId UnitId;
        public uint Sequence;
        public PathRequestStatus LastObservedNavStatus;
        public bool HasResult;
        public bool IsRejected;
    }

    private struct ValidUnitOrder
    {
        public EntityId UnitId;
        public System.Numerics.Vector2 Target;
    }

    private readonly DestinationEvaluator _evaluator;
    private readonly StaticGeometry _geometry;
    private readonly HeightMap _map;
    private readonly IUnitMovementCatalog _movementCatalog;
    private readonly OrderCursorOverlay _overlay;
    private readonly OrderDestinationView _destinationView;
    private readonly OrderNotification? _notification;

    private readonly CachedProfileEvaluation[] _profileCache = new CachedProfileEvaluation[16];
    private int _profileCacheCount;

    private readonly TrackedUnitOrder[] _trackedOrders = new TrackedUnitOrder[WireProtocol.MaxUnits];
    private int _trackedOrderCount;

    internal int TrackedOrderCount => _trackedOrderCount;

    private readonly ValidUnitOrder[] _validUnitsBuffer = new ValidUnitOrder[WireProtocol.MaxUnits];
    private readonly EntityId[] _skippedUnitsBuffer = new EntityId[WireProtocol.MaxUnits];

    private bool _orderOutcomeHandled;
    private bool _hasDivergentTargets;
    private uint _lastHandledCommandResultSeq;
    private Vector3 _lastOrderedTarget3D;

    public OrderPreviewController(
        StaticGeometry geometry,
        HeightMap map,
        UnitCatalog catalog,
        OrderCursorOverlay overlay,
        OrderDestinationView destinationView,
        OrderNotification? notification = null)
        : this(new DestinationEvaluator(geometry), geometry, map, new OpsMovementCatalog(catalog), overlay, destinationView, notification)
    {
    }

    public OrderPreviewController(
        DestinationEvaluator evaluator,
        StaticGeometry geometry,
        HeightMap map,
        UnitCatalog catalog,
        OrderCursorOverlay overlay,
        OrderDestinationView destinationView,
        OrderNotification? notification = null)
        : this(evaluator, geometry, map, new OpsMovementCatalog(catalog), overlay, destinationView, notification)
    {
    }

    public OrderPreviewController(
        DestinationEvaluator evaluator,
        StaticGeometry geometry,
        HeightMap map,
        IUnitMovementCatalog movementCatalog,
        OrderCursorOverlay overlay,
        OrderDestinationView destinationView,
        OrderNotification? notification = null)
    {
        _evaluator = evaluator ?? throw new System.ArgumentNullException(nameof(evaluator));
        _geometry = geometry ?? throw new System.ArgumentNullException(nameof(geometry));
        _map = map ?? throw new System.ArgumentNullException(nameof(map));
        _movementCatalog = movementCatalog ?? throw new System.ArgumentNullException(nameof(movementCatalog));
        _overlay = overlay ?? throw new System.ArgumentNullException(nameof(overlay));
        _destinationView = destinationView ?? throw new System.ArgumentNullException(nameof(destinationView));
        _notification = notification;
    }

    public bool TryRaycast(
        Camera3D camera,
        Vector2 screenPos,
        out Vector3 hitPoint3D,
        out uint supportSurface,
        out float surfaceHeight)
    {
        Vector3 origin = camera.ProjectRayOrigin(screenPos);
        Vector3 dir = camera.ProjectRayNormal(screenPos);
        float rayFar = camera.Far;

        System.Numerics.Vector3 start = new(origin.X, origin.Y, origin.Z);
        System.Numerics.Vector3 end = start + new System.Numerics.Vector3(dir.X, dir.Y, dir.Z) * rayFar;

        LineOfSightResult los = _geometry.CheckLineOfSight(start, end);

        if (!los.IsClear)
        {
            hitPoint3D = new Vector3(los.HitPoint.X, los.HitPoint.Y, los.HitPoint.Z);

            if (los.ObstructionKind == ObstructionKind.Structure &&
                _geometry.TryGetBridge(los.ObstructingStructureId, out BridgeStructure bridge))
            {
                supportSurface = (uint)bridge.Id.Value;
                surfaceHeight = bridge.GetHeight(new System.Numerics.Vector2(los.HitPoint.X, los.HitPoint.Z));
            }
            else
            {
                supportSurface = 0;
                surfaceHeight = _map.GetHeight(los.HitPoint.X, los.HitPoint.Z);
            }
            return true;
        }

        // Fallback to terrain heightmap raycast if line of sight didn't hit a structure
        if (_map.TryRaycast(start, new System.Numerics.Vector3(dir.X, dir.Y, dir.Z), rayFar, out System.Numerics.Vector3 terrainHit))
        {
            hitPoint3D = new Vector3(terrainHit.X, terrainHit.Y, terrainHit.Z);
            supportSurface = 0;
            surfaceHeight = terrainHit.Y;
            return true;
        }

        hitPoint3D = default;
        supportSurface = 0;
        surfaceHeight = 0f;
        return false;
    }

    public void UpdateHover(
        Camera3D camera,
        Vector2 mousePos,
        Vector2 viewportSize,
        bool isPointerOccluded,
        bool hasEnemyContactUnderMouse,
        UnitViewSet unitViews,
        ClientReplica replica)
    {
        if (isPointerOccluded || hasEnemyContactUnderMouse || unitViews.Selected.Count == 0)
        {
            _overlay.HideTooltip();
            _destinationView.HideGhost();
            return;
        }

        if (!TryRaycast(camera, mousePos, out Vector3 hitPoint3D, out uint hitSurface, out float surfaceHeight))
        {
            _overlay.HideTooltip();
            _destinationView.HideGhost();
            return;
        }

        System.Numerics.Vector2 target2D = new(hitPoint3D.X, hitPoint3D.Z);
        _profileCacheCount = 0;

        int totalCount = 0;
        int validCount = 0;
        DestinationBlockReason primaryReason = DestinationBlockReason.None;
        DestinationValidity combinedValidity = DestinationValidity.Blocked;
        System.Numerics.Vector2 adjustedTarget = target2D;

        foreach (EntityId unitId in unitViews.Selected)
        {
            if (!replica.TryGetOwnUnit(unitId, out OwnUnitObservation own))
            {
                continue;
            }

            totalCount++;
            float radius = 1.0f;
            TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle;
            if (_movementCatalog.TryGetProfile(own.Definition, out UnitMovementProfile profile))
            {
                radius = profile.Radius;
                capabilities = profile.Capabilities;
            }

            ProfileKey key = new(radius, capabilities, own.SupportSurface);
            DestinationEvaluationResult evalResult = default;
            bool foundCached = false;

            for (int k = 0; k < _profileCacheCount; k++)
            {
                if (_profileCache[k].Key.Equals(key))
                {
                    evalResult = _profileCache[k].Result;
                    foundCached = true;
                    break;
                }
            }

            if (!foundCached)
            {
                if (_evaluator.IsPointClear(target2D, radius, capabilities, hitSurface))
                {
                    evalResult = new DestinationEvaluationResult(
                        DestinationValidity.Valid,
                        DestinationBlockReason.None,
                        target2D,
                        target2D,
                        hitSurface,
                        _evaluator.GetSurfaceHeight(target2D, hitSurface));

                    if (_profileCacheCount < _profileCache.Length)
                    {
                        _profileCache[_profileCacheCount++] = new CachedProfileEvaluation { Key = key, Result = evalResult };
                    }
                }
                else
                {
                    // Target requires snapping or is blocked; evaluate per unit based on its start position.
                    // Snapped targets are NOT cached by profile because snapping depends on start position.
                    evalResult = _evaluator.Evaluate(target2D, radius, capabilities, hitSurface, own.Position);
                }
            }

            if (evalResult.IsUsable)
            {
                validCount++;
                if (evalResult.Validity == DestinationValidity.Adjusted)
                {
                    combinedValidity = DestinationValidity.Adjusted;
                    if (adjustedTarget == target2D)
                    {
                        adjustedTarget = evalResult.AdjustedTarget;
                    }
                }
                else if (combinedValidity != DestinationValidity.Adjusted)
                {
                    combinedValidity = DestinationValidity.Valid;
                }
            }
            else if (primaryReason == DestinationBlockReason.None)
            {
                primaryReason = evalResult.Reason;
            }
        }

        if (totalCount == 0)
        {
            _overlay.HideTooltip();
            _destinationView.HideGhost();
            return;
        }

        if (validCount == 0)
        {
            combinedValidity = DestinationValidity.Blocked;
        }

        _overlay.SetPreviewState(
            visible: true,
            validity: combinedValidity,
            reason: primaryReason,
            validCount: validCount,
            totalCount: totalCount);

        _overlay.UpdatePosition(mousePos, viewportSize);

        if (combinedValidity == DestinationValidity.Adjusted)
        {
            float ghostH = _evaluator.GetSurfaceHeight(adjustedTarget, hitSurface);
            _destinationView.ShowGhost(new Vector3(adjustedTarget.X, ghostH + 0.10f, adjustedTarget.Y));
        }
        else
        {
            _destinationView.HideGhost();
        }
    }

    public bool ExecuteMoveClick(
        Camera3D camera,
        Vector2 mousePos,
        UnitViewSet unitViews,
        IGameSession session,
        out string notificationMessage)
    {
        notificationMessage = string.Empty;

        if (unitViews.Selected.Count == 0)
        {
            return false;
        }

        if (!TryRaycast(camera, mousePos, out Vector3 hitPoint3D, out uint hitSurface, out float surfaceHeight))
        {
            return false;
        }

        System.Numerics.Vector2 target2D = new(hitPoint3D.X, hitPoint3D.Z);
        ClientReplica replica = session.Replica;

        int validCount = 0;
        int skippedCount = 0;
        int overflowCount = 0;
        DestinationBlockReason primaryBlockedReason = DestinationBlockReason.None;

        foreach (EntityId unitId in unitViews.Selected)
        {
            if (!replica.TryGetOwnUnit(unitId, out OwnUnitObservation own))
            {
                continue;
            }

            float radius = 1.0f;
            TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle;
            if (_movementCatalog.TryGetProfile(own.Definition, out UnitMovementProfile profile))
            {
                radius = profile.Radius;
                capabilities = profile.Capabilities;
            }

            DestinationEvaluationResult res = _evaluator.Evaluate(target2D, radius, capabilities, hitSurface, own.Position);

            if (res.IsUsable)
            {
                if (validCount < _validUnitsBuffer.Length)
                {
                    _validUnitsBuffer[validCount++] = new ValidUnitOrder
                    {
                        UnitId = unitId,
                        Target = res.AdjustedTarget
                    };
                }
                else
                {
                    overflowCount++;
                }
            }
            else
            {
                if (skippedCount < _skippedUnitsBuffer.Length)
                {
                    _skippedUnitsBuffer[skippedCount++] = unitId;
                }
                if (primaryBlockedReason == DestinationBlockReason.None)
                {
                    primaryBlockedReason = res.Reason;
                }
            }
        }

        if (validCount == 0)
        {
            // All selected units are blocked; do NOT send command and do NOT cancel existing orders
            string reason = DestinationBlockReasons.GetReasonText(primaryBlockedReason);
            notificationMessage = string.IsNullOrEmpty(reason)
                ? "Cannot move: Destination blocked."
                : $"Cannot move: {reason}";
            _notification?.ShowNotification(notificationMessage);
            return false;
        }

        // Send move command to valid units with their individual targets
        _orderOutcomeHandled = false;
        _trackedOrderCount = 0;
        int sentMove = 0;

        for (int i = 0; i < validCount; i++)
        {
            ValidUnitOrder order = _validUnitsBuffer[i];
            if (session.Move(order.UnitId, order.Target))
            {
                sentMove++;
                uint seq = session.LastSubmittedCommandSequence;
                if (_trackedOrderCount < _trackedOrders.Length)
                {
                    _trackedOrders[_trackedOrderCount++] = new TrackedUnitOrder
                    {
                        UnitId = order.UnitId,
                        Sequence = seq,
                        LastObservedNavStatus = PathRequestStatus.None,
                        HasResult = false
                    };
                }
            }
        }

        bool allSameTarget = true;
        System.Numerics.Vector2 primaryTarget = _validUnitsBuffer[0].Target;
        for (int i = 1; i < validCount; i++)
        {
            if (_validUnitsBuffer[i].Target != primaryTarget)
            {
                allSameTarget = false;
                break;
            }
        }
        _hasDivergentTargets = !allSameTarget;

        if (allSameTarget)
        {
            float markerHeight = _evaluator.GetSurfaceHeight(primaryTarget, hitSurface);
            _lastOrderedTarget3D = new Vector3(primaryTarget.X, markerHeight + 0.12f, primaryTarget.Y);
            _destinationView.Show(_lastOrderedTarget3D, OrderMarkerVisualState.Sent);
            _destinationView.HideGhost();
        }
        else
        {
            _destinationView.ClearSubmittedMarker();
            _destinationView.HideGhost();
        }

        if (overflowCount > 0)
        {
            notificationMessage = allSameTarget
                ? $"{sentMove} units ordered to move. {overflowCount} units exceeded max batch limit of {_validUnitsBuffer.Length}."
                : $"{sentMove} units ordered to move to individual destinations. {overflowCount} units exceeded max batch limit of {_validUnitsBuffer.Length}.";
        }
        else if (skippedCount > 0)
        {
            string skipReason = DestinationBlockReasons.GetReasonText(primaryBlockedReason);
            notificationMessage = allSameTarget
                ? $"{sentMove} units ordered to move. {skippedCount} units skipped: {skipReason}"
                : $"{sentMove} units ordered to move to individual destinations. {skippedCount} units skipped: {skipReason}";
        }
        else
        {
            if (sentMove == 1)
            {
                notificationMessage = $"Move order submitted / 1 unit / {primaryTarget.X:F0}, {primaryTarget.Y:F0} m";
            }
            else if (allSameTarget)
            {
                notificationMessage = $"Move order submitted / {sentMove} units / {primaryTarget.X:F0}, {primaryTarget.Y:F0} m";
            }
            else
            {
                notificationMessage = $"Move order submitted / {sentMove} units / individual destinations";
            }
        }

        _notification?.ShowNotification(notificationMessage);
        return true;
    }

    public void UpdateOrders(IGameSession session, double delta)
    {
        _destinationView.Update(delta);

        // Check command result from server
        if (session.LastCommandResult is { } result && result.Sequence != _lastHandledCommandResultSeq)
        {
            _lastHandledCommandResultSeq = result.Sequence;
            for (int i = 0; i < _trackedOrderCount; i++)
            {
                if (_trackedOrders[i].Sequence == result.Sequence)
                {
                    if (!result.Queued)
                    {
                        _trackedOrders[i].IsRejected = true;
                        _trackedOrders[i].HasResult = true;
                        _trackedOrders[i].LastObservedNavStatus = PathRequestStatus.InvalidDestination;
                        _notification?.ShowNotification($"Order #{result.Sequence} rejected by server");
                    }
                    else if (_destinationView.CurrentState == OrderMarkerVisualState.Sent && !_hasDivergentTargets)
                    {
                        _destinationView.SetState(OrderMarkerVisualState.Processing);
                    }
                    break;
                }
            }
        }

        if (_orderOutcomeHandled || _trackedOrderCount == 0) return;

        ClientReplica replica = session.Replica;
        int activeTracked = 0;
        int pathFoundCount = 0;
        int failedCount = 0;
        int searchingCount = 0;

        for (int i = 0; i < _trackedOrderCount; i++)
        {
            ref TrackedUnitOrder tracked = ref _trackedOrders[i];

            if (!replica.TryGetOwnUnit(tracked.UnitId, out OwnUnitObservation obs))
            {
                // Unit removed / destroyed; clear tracked order
                continue;
            }

            activeTracked++;

            if (tracked.IsRejected)
            {
                failedCount++;
            }
            else if (obs.NavOrderSequence == tracked.Sequence)
            {
                tracked.LastObservedNavStatus = obs.NavStatus;
                tracked.HasResult = true;

                switch (obs.NavStatus)
                {
                    case PathRequestStatus.PathFound:
                        pathFoundCount++;
                        break;
                    case PathRequestStatus.NoPath:
                    case PathRequestStatus.CapacityExceeded:
                    case PathRequestStatus.InvalidDestination:
                        failedCount++;
                        break;
                    case PathRequestStatus.Searching:
                    case PathRequestStatus.Pending:
                        searchingCount++;
                        break;
                }
            }
            else if (obs.NavOrderSequence < tracked.Sequence)
            {
                // Stale snapshot prior to server processing this order
                searchingCount++;
            }
        }

        if (activeTracked == 0)
        {
            _trackedOrderCount = 0;
            _destinationView.Clear();
            return;
        }

        // Update destination marker visual state based on correlated unit statuses
        if (searchingCount == 0)
        {
            if (pathFoundCount > 0 && failedCount == 0)
            {
                if (!_hasDivergentTargets)
                {
                    _destinationView.SetState(OrderMarkerVisualState.PathFound);
                }
                _orderOutcomeHandled = true;
            }
            else if (failedCount > 0 && pathFoundCount == 0)
            {
                if (!_hasDivergentTargets)
                {
                    _destinationView.SetState(OrderMarkerVisualState.Failed);
                }
                _notification?.ShowNotification("Route not found");
                _orderOutcomeHandled = true;
            }
            else if (failedCount > 0 && pathFoundCount > 0)
            {
                if (!_hasDivergentTargets)
                {
                    _destinationView.SetState(OrderMarkerVisualState.Partial);
                }
                _notification?.ShowNotification($"Move order: {pathFoundCount} of {activeTracked} units route found, {failedCount} failed");
                _orderOutcomeHandled = true;
            }
        }
        else if (searchingCount > 0 && _destinationView.CurrentState == OrderMarkerVisualState.Sent && !_hasDivergentTargets)
        {
            _destinationView.SetState(OrderMarkerVisualState.Processing);
        }
    }

    public void OnUnitSelectionChanged()
    {
        _overlay.HideTooltip();
        _destinationView.HideGhost();
    }

    public void Reset()
    {
        _trackedOrderCount = 0;
        _lastHandledCommandResultSeq = 0;
        _orderOutcomeHandled = false;
        _hasDivergentTargets = false;
        _destinationView.Clear();
        _overlay.HideTooltip();
    }
}
