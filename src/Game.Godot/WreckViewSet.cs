using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal sealed class WreckViewSet(
    Node3D parent,
    HeightMap map,
    Func<ushort, string?>? unitIdProvider = null,
    Func<string?>? factionProvider = null,
    StaticGeometry? geometry = null)
{
    public const int MaxActiveEmitters = 16;
    public const float Lod2CameraSize = UnitView.Lod2CameraSize;
    public const float Lod2HysteresisMargin = 10f;
    public const float DistanceHysteresisBonus = 15f;

    internal Dictionary<WreckId, WreckView> Views { get; } = new();
    private readonly HashSet<WreckId> _present = new(WireProtocol.MaxWrecks);
    private readonly List<WreckId> _removed = new(WireProtocol.MaxWrecks);

    private struct PendingDestruction
    {
        public uint EventId;
        public WreckId WreckId;
        public int ServerTick;
        public System.Numerics.Vector3 Position;
    }
    private readonly PendingDestruction[] _pendingDestructions = new PendingDestruction[64];
    private int _pendingCount;

    private struct SmokeCandidate
    {
        public WreckView View;
        public float EffectiveDistSq;
        public float AgeSeconds;
    }
    private static readonly SmokeCandidate[] s_candidates = new SmokeCandidate[WireProtocol.MaxWrecks * 2];
    private static readonly HashSet<WreckId> s_seenWrecks = new(WireProtocol.MaxWrecks * 2);
    private static bool s_smokeZoomAllowed = true;

    private float _cameraSize;

    internal int ActiveEmitterCount
    {
        get
        {
            int count = 0;
            foreach (WreckView view in Views.Values)
            {
                count += view.ActiveEmitterCount;
            }
            return count;
        }
    }

    internal int ActiveSmokeCount
    {
        get
        {
            int count = 0;
            foreach (WreckView view in Views.Values)
            {
                if (view.IsSmoking) count++;
            }
            return count;
        }
    }

    public WreckViewSet(
        Node3D parent,
        HeightMap map,
        DeadGround.Rules.Ops.UnitCatalog catalog,
        Func<string?>? factionProvider = null,
        StaticGeometry? geometry = null)
        : this(parent, map, def => def < catalog.Units.Length ? catalog.Units[def].Id : null, factionProvider, geometry)
    {
    }

    internal void Sync(ClientReplica replica)
    {
        _present.Clear();
        foreach (WreckObservation wreck in replica.Wrecks)
        {
            _present.Add(wreck.Id);
            if (!Views.TryGetValue(wreck.Id, out WreckView? view))
            {
                string? unitId = unitIdProvider?.Invoke(wreck.Definition) ?? UnitViewSet.GetDefaultUnitId(wreck.Definition);
                view = new WreckView(unitId, factionProvider?.Invoke())
                {
                    Id = wreck.Id
                };
                Views.Add(wreck.Id, view);
                parent.AddChild(view.Root);
                if (_cameraSize > 0f) view.UpdateLod(_cameraSize);

                for (int i = _pendingCount - 1; i >= 0; i--)
                {
                    if (_pendingDestructions[i].WreckId.Value != 0)
                    {
                        if (_pendingDestructions[i].WreckId == wreck.Id)
                        {
                            if (view.DestructionTick == 0)
                            {
                                view.DestructionTick = _pendingDestructions[i].ServerTick;
                            }
                            _pendingDestructions[i] = _pendingDestructions[--_pendingCount];
                            break;
                        }
                    }
                    else
                    {
                        System.Numerics.Vector2 pendingPos2D = new(_pendingDestructions[i].Position.X, _pendingDestructions[i].Position.Z);
                        if ((pendingPos2D - wreck.Position).LengthSquared() < 9.0f)
                        {
                            if (view.DestructionTick == 0)
                            {
                                view.DestructionTick = _pendingDestructions[i].ServerTick;
                            }
                            _pendingDestructions[i] = _pendingDestructions[--_pendingCount];
                            break;
                        }
                    }
                }
            }

            float groundY = geometry is not null
                ? geometry.GetSurfaceHeight(wreck.Position, wreck.SupportSurface)
                : map.GetHeight(wreck.Position.X, wreck.Position.Y);

            view.Update(wreck, groundY);
        }

        _removed.Clear();
        foreach (WreckId id in Views.Keys)
        {
            if (!_present.Contains(id))
                _removed.Add(id);
        }

        foreach (WreckId id in _removed)
        {
            WreckView view = Views[id];
            view.ClearEffects();
            parent.RemoveChild(view.Root);
            view.Root.QueueFree();
            Views.Remove(id);
        }
    }

    internal void ProcessEvents(ReadOnlySpan<CombatEventObservation> events, int serverTick)
    {
        foreach (CombatEventObservation ev in events)
        {
            if (ev.Kind != CombatEventKind.UnitDestroyed) continue;
            if (ev.WreckId.Value != 0)
            {
                if (Views.TryGetValue(ev.WreckId, out WreckView? match))
                {
                    if (match.DestructionTick == 0)
                    {
                        match.DestructionTick = ev.ServerTick;
                    }
                }
                else
                {
                    bool exists = false;
                    for (int i = 0; i < _pendingCount; i++)
                    {
                        if (_pendingDestructions[i].WreckId == ev.WreckId) { exists = true; break; }
                    }
                    if (!exists && _pendingCount < _pendingDestructions.Length)
                    {
                        _pendingDestructions[_pendingCount++] = new PendingDestruction
                        {
                            EventId = ev.EventId,
                            WreckId = ev.WreckId,
                            ServerTick = ev.ServerTick,
                            Position = ev.Target
                        };
                    }
                }
            }
            else
            {
                WreckView? match = FindClosestWreck3D(ev.Target, maxDist: 3.5f);
                if (match is not null)
                {
                    if (match.DestructionTick == 0)
                    {
                        match.DestructionTick = ev.ServerTick;
                    }
                }
                else
                {
                    bool exists = false;
                    for (int i = 0; i < _pendingCount; i++)
                    {
                        if (_pendingDestructions[i].EventId == ev.EventId) { exists = true; break; }
                    }
                    if (!exists && _pendingCount < _pendingDestructions.Length)
                    {
                        _pendingDestructions[_pendingCount++] = new PendingDestruction
                        {
                            EventId = ev.EventId,
                            WreckId = default,
                            ServerTick = ev.ServerTick,
                            Position = ev.Target
                        };
                    }
                }
            }
        }
    }

    internal void UpdateEffects(double delta, Vector3 cameraFocus, float cameraSize, int serverTick)
    {
        UpdateSceneEffects([this], delta, cameraFocus, cameraSize, serverTick, MaxActiveEmitters);
    }

    public static void UpdateSceneEffects(
        ReadOnlySpan<WreckViewSet> sets,
        double delta,
        Vector3 cameraFocus,
        float cameraSize,
        int serverTick,
        int maxActiveEmitters = MaxActiveEmitters)
    {
        if (s_smokeZoomAllowed)
        {
            if (cameraSize > Lod2CameraSize + Lod2HysteresisMargin) s_smokeZoomAllowed = false;
        }
        else
        {
            if (cameraSize < Lod2CameraSize - Lod2HysteresisMargin) s_smokeZoomAllowed = true;
        }

        if (!s_smokeZoomAllowed || maxActiveEmitters <= 0)
        {
            foreach (WreckViewSet set in sets)
            {
                foreach (WreckView view in set.Views.Values)
                {
                    if (view.IsSmoking || view.IsBurning)
                    {
                        view.UpdateEffects(0f, allowFire: false, allowSmoke: false);
                    }
                }
            }
            return;
        }

        int candidateCount = 0;
        System.Numerics.Vector2 focus2D = new(cameraFocus.X, cameraFocus.Z);
        s_seenWrecks.Clear();

        foreach (WreckViewSet set in sets)
        {
            foreach (WreckView view in set.Views.Values)
            {
                if (view.Id.Value != 0 && !s_seenWrecks.Add(view.Id))
                {
                    // Deduplicate wreck already present in another set: suppress duplicate effects
                    if (view.IsSmoking || view.IsBurning)
                    {
                        view.UpdateEffects(0f, allowFire: false, allowSmoke: false);
                    }
                    continue;
                }

                if (view.DestructionTick == 0) continue;
                float ageSeconds = (serverTick - view.DestructionTick) / (float)Simulation.TickRate;
                if (ageSeconds < 0f || ageSeconds >= WreckView.SmokeDurationSeconds)
                {
                    if (view.IsSmoking || view.IsBurning)
                    {
                        view.UpdateEffects(ageSeconds, allowFire: false, allowSmoke: false);
                    }
                    continue;
                }

                System.Numerics.Vector2 pos = new(view.Root.Position.X, view.Root.Position.Z);
                float actualDist = (pos - focus2D).Length();
                float effectiveDist = (view.IsSmoking || view.IsBurning)
                    ? MathF.Max(0f, actualDist - DistanceHysteresisBonus)
                    : actualDist;
                float effectiveDistSq = effectiveDist * effectiveDist;

                if (candidateCount < s_candidates.Length)
                {
                    s_candidates[candidateCount++] = new SmokeCandidate
                    {
                        View = view,
                        EffectiveDistSq = effectiveDistSq,
                        AgeSeconds = ageSeconds
                    };
                }
            }
        }

        for (int i = 0; i < candidateCount - 1; i++)
        {
            int minIdx = i;
            for (int j = i + 1; j < candidateCount; j++)
            {
                if (s_candidates[j].EffectiveDistSq < s_candidates[minIdx].EffectiveDistSq) minIdx = j;
            }
            if (minIdx != i)
            {
                SmokeCandidate tmp = s_candidates[i];
                s_candidates[i] = s_candidates[minIdx];
                s_candidates[minIdx] = tmp;
            }
        }

        int remainingBudget = maxActiveEmitters;
        for (int i = 0; i < candidateCount; i++)
        {
            ref readonly SmokeCandidate cand = ref s_candidates[i];
            bool wantsFire = cand.AgeSeconds < WreckView.FireDurationSeconds;
            bool allowFire = false;
            bool allowSmoke = false;

            if (wantsFire)
            {
                if (remainingBudget >= 2)
                {
                    allowFire = true;
                    allowSmoke = true;
                    remainingBudget -= 2;
                }
                else if (remainingBudget == 1)
                {
                    allowSmoke = true;
                    remainingBudget -= 1;
                }
            }
            else
            {
                if (remainingBudget >= 1)
                {
                    allowSmoke = true;
                    remainingBudget -= 1;
                }
            }

            cand.View.UpdateEffects(cand.AgeSeconds, allowFire, allowSmoke);
        }
    }

    private WreckView? FindClosestWreck3D(System.Numerics.Vector3 target, float maxDist)
    {
        float maxSq = maxDist * maxDist;
        WreckView? best = null;
        float bestDistSq = maxSq;
        foreach (WreckView view in Views.Values)
        {
            float dx = view.Root.Position.X - target.X;
            float dy = view.Root.Position.Y - target.Y;
            float dz = view.Root.Position.Z - target.Z;
            float dSq = dx * dx + dy * dy + dz * dz;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = view;
            }
        }
        return best;
    }

    private WreckView? FindClosestWreck(System.Numerics.Vector2 position, float maxDist)
    {
        float maxSq = maxDist * maxDist;
        WreckView? best = null;
        float bestDistSq = maxSq;
        foreach (WreckView view in Views.Values)
        {
            System.Numerics.Vector2 pos = new(view.Root.Position.X, view.Root.Position.Z);
            float dSq = (pos - position).LengthSquared();
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = view;
            }
        }
        return best;
    }

    internal void UpdateLod(float cameraSize)
    {
        _cameraSize = cameraSize;
        foreach (WreckView view in Views.Values)
        {
            view.UpdateLod(cameraSize);
        }
    }

    internal void Clear()
    {
        foreach (WreckView view in Views.Values)
        {
            view.ClearEffects();
            parent.RemoveChild(view.Root);
            view.Root.QueueFree();
        }
        Views.Clear();
        _present.Clear();
        _removed.Clear();
        _pendingCount = 0;
        s_smokeZoomAllowed = true;
    }
}
