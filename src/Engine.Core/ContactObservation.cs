using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Lifecycle state of a perceived enemy contact.
/// </summary>
public enum ContactState : byte
{
    None = 0,
    Current = 1,
    LastKnown = 2
}

/// <summary>
/// Perceived classification of a contact.
/// Full recognition is deferred in this slice; detected enemies remain Unknown.
/// </summary>
public enum ContactClassification : byte
{
    Unknown = 0
}

/// <summary>
/// Permitted observation data for a perceived contact, exposed to client replicas and AI.
/// Never exposes truth EntityId, exact definition, velocity, or private state.
/// </summary>
public readonly record struct ContactObservation(
    ContactId Id,
    ContactState State,
    ContactClassification Classification,
    Vector2 Position,
    int LastSeenTick)
{
    public bool IsCurrent => State == ContactState.Current;
    public bool IsLastKnown => State == ContactState.LastKnown;
}
