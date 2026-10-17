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
/// Recognition fidelity level of a perceived enemy contact.
/// </summary>
public enum DetectionLevel : byte
{
    None = 0,
    Detected = 1,
    Identified = 2
}

/// <summary>
/// Perceived classification of a contact.
/// </summary>
public enum ContactClassification : byte
{
    Unknown = 0,
    Vehicle = 1,
    Tank = 2,
    Recon = 3,
    CommandVehicle = 4,
    Command = 4,
    Apc = 5
}

/// <summary>
/// Permitted observation data for a perceived contact, exposed to client replicas and AI.
/// Never exposes truth EntityId, exact definition, velocity, or private state.
/// </summary>
public readonly record struct ContactObservation(
    ContactId Id,
    ContactState State,
    DetectionLevel Level,
    ContactClassification Classification,
    Vector2 Position,
    int LastSeenTick)
{
    public bool IsCurrent => State == ContactState.Current;
    public bool IsLastKnown => State == ContactState.LastKnown;
    public bool IsIdentified => Level == DetectionLevel.Identified;

    public ContactObservation(
        ContactId id,
        ContactState state,
        ContactClassification classification,
        Vector2 position,
        int lastSeenTick)
        : this(
            id,
            state,
            classification == ContactClassification.Unknown ? DetectionLevel.Detected : DetectionLevel.Identified,
            classification,
            position,
            lastSeenTick)
    {
    }
}
