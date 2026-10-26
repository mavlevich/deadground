using System.Text.Json.Serialization;

namespace DeadGround.Engine;

/// <summary>
/// Bitmask capabilities that govern which terrain types a unit can traverse.
/// Distinct from MovementClass, which describes physical clearance envelope / radius.
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<TraversabilityCapabilities>))]
public enum TraversabilityCapabilities : byte
{
    None = 0,

    // Standard dry ground (meadows, dirt, roads, sparse woodland)
    Ground = 1 << 0,

    // Dense forest (passable for foot infantry, impassable for vehicles)
    DenseForest = 1 << 1,

    // Water bodies (passable for amphibious / naval craft, impassable for normal ground units)
    Water = 1 << 2,

    // Bridge deck structures (passable corridor across otherwise impassable water)
    Bridge = 1 << 3,

    // Common capability presets
    DefaultVehicle = Ground | Bridge,
    Foot = Ground | DenseForest | Bridge,
    Amphibious = Ground | Water | Bridge
}
