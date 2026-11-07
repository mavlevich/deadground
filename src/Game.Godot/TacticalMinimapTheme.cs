using Godot;

namespace DeadGround.Client;

/// <summary>
/// Color palette, dimensions, and drawing styles for the tactical minimap,
/// based on Cold War military topographic maps.
/// </summary>
public static class TacticalMinimapTheme
{
    // Palette: Cold War military topographic map
    public static readonly Color LandColor = new(0.36f, 0.39f, 0.31f); // Muted gray-olive land
    public static readonly Color LandContourColor = new(0.44f, 0.48f, 0.38f); // Thin contour lines
    public static readonly Color LandContourIndexColor = new(0.41f, 0.45f, 0.35f); // Index contour lines
    public static readonly Color WaterColor = new(0.24f, 0.40f, 0.51f); // Gray-blue water
    public static readonly Color WaterShorelineColor = new(0.18f, 0.29f, 0.36f); // Shoreline
    public static readonly Color UrbanColor = new(0.20f, 0.21f, 0.18f); // High-contrast city blocks (#33362E)
    public static readonly Color UrbanBorderColor = new(0.13f, 0.14f, 0.11f); // Urban block borders (#21241C)
    public static readonly Color RoadColor = new(0.78f, 0.73f, 0.62f); // Contrasting pale ochre / warm buff (#C7BA9E)
    public static readonly Color RoadCasingColor = new(0.22f, 0.20f, 0.16f); // Road casing (#38332A)
    public static readonly Color CrossingColor = new(0.92f, 0.88f, 0.79f); // Bridge decks / crossings (#EBE0CA)
    public static readonly Color CrossingBorderColor = new(0.15f, 0.13f, 0.10f); // Crossing border (#26221A)

    // Frame and fields (pillarbox / letterbox margins)
    public static readonly Color WidgetBackground = new(0.11f, 0.13f, 0.10f, 0.95f); // Dark military panel (#1C211A)
    public static readonly Color FrameBorder = new(0.36f, 0.40f, 0.33f); // Frame outline (#5C6654)
    public static readonly Color FieldColor = new(0.14f, 0.16f, 0.13f); // Margins outside map (#242921)
    public static readonly Color GridLineColor = new(0.28f, 0.32f, 0.26f, 0.35f); // Coordinate grid lines

    // Camera view frustum
    public static readonly Color CameraFrustumOutline = new(0.96f, 0.82f, 0.38f, 0.95f); // Golden amber (#F5D161)
    public static readonly Color CameraFrustumFill = new(0.96f, 0.82f, 0.38f, 0.12f); // Subtle translucent fill

    // Unit and contact markers
    public static readonly Color FriendlyColor = new(0.31f, 0.69f, 0.45f); // Friendly military green (#4FB073)
    public static readonly Color FriendlyBorderColor = new(0.12f, 0.28f, 0.18f); // Friendly border
    public static readonly Color FriendlySelectedRing = new(1.0f, 0.95f, 0.70f); // Selection highlight (#FFF2B3)
    public static readonly Color EnemyCurrentColor = new(0.88f, 0.22f, 0.24f); // Solid hostile red (#E0383D)
    public static readonly Color EnemyCurrentBorderColor = new(0.35f, 0.08f, 0.09f);
    public static readonly Color EnemyLastKnownColor = new(0.91f, 0.43f, 0.32f); // Muted hostile orange-red (#E86E52)

    // Dimensions
    public const float CrossingMinimumTextureWidth = 7f;
    public const float FriendlyMarkerRadius = 3.5f;
    public const float SelectedRingRadius = 6.0f;
    public const float SelectedRingWidth = 1.8f;
    public const float EnemyDiamondRadius = 4.5f;
    public const float EnemyLastKnownHalfSize = 4.0f;
    public const float EnemyLastKnownLineWidth = 1.6f;
    public const float CameraFrustumLineWidth = 1.8f;
    public const float HoverHitRadius = 8.0f;
}
