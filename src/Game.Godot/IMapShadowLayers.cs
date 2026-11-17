namespace DeadGround.Client;

/// <summary>
/// Abstraction over map render layers whose shadow casting is controlled by render budget tiers.
/// </summary>
public interface IMapShadowLayers
{
    bool VegetationShadowsEnabled { get; }
    bool TerrainShadowsEnabled { get; }
    void SetVegetationShadowsEnabled(bool enabled);
    void SetTerrainShadowsEnabled(bool enabled);
}
