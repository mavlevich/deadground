using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace DeadGround.Client;

// An instantiated vehicle: a scaled root with one child per LOD. LOD0 exists from the start;
// the others are instantiated the first time they are shown.
internal sealed class VehicleVisual
{
    private readonly PackedScene[] _scenes;
    private readonly Node3D?[] _lods;

    internal VehicleVisual(Node3D root, PackedScene[] scenes, float trackGauge, float trackWrap)
    {
        Root = root;
        _scenes = scenes;
        _lods = new Node3D?[scenes.Length];
        TrackGauge = trackGauge;
        TrackWrap = trackWrap;
        GetOrCreateLod(0);
    }

    internal Node3D Root { get; }
    internal int LodCount => _scenes.Length;

    // Distance between track centers in model meters, or 0 when the tracks do not animate.
    internal float TrackGauge { get; }

    // Travel period in model meters at which track travel wraps without a visible jump, or 0.
    internal float TrackWrap { get; }

    internal bool IsLoaded(int lod) => _lods[lod] is not null;

    internal Node3D GetOrCreateLod(int lod)
    {
        Node3D? node = _lods[lod];
        if (node is not null) return node;
        node = _scenes[lod].Instantiate<Node3D>();
        node.Name = $"LOD{lod}";
        node.Visible = lod == 0;
        Root.AddChild(node);
        _lods[lod] = node;
        return node;
    }
}

// Presentation assets are keyed by content IDs, never by catalog ordering.
// A definition may override its scene per faction ID; factions come from the match setup.
internal static class VehicleVisualCatalog
{
    private sealed record VisualVariant(
        string Scene,
        float Scale,
        string[]? Lods = null,
        [property: JsonPropertyName("track_gauge")] float TrackGauge = 0f,
        [property: JsonPropertyName("track_wrap")] float TrackWrap = 0f);

    private sealed record VisualDefinition(
        string Scene,
        float Scale,
        string[]? Lods = null,
        [property: JsonPropertyName("track_gauge")] float TrackGauge = 0f,
        [property: JsonPropertyName("track_wrap")] float TrackWrap = 0f,
        Dictionary<string, VisualVariant>? Factions = null);

    private static readonly Lazy<Dictionary<string, VisualDefinition>> Definitions = new(LoadDefinitions);
    private static readonly Dictionary<string, PackedScene> Scenes = new(StringComparer.Ordinal);

    internal static VehicleVisual? InstantiateVisual(string? key, string? faction)
    {
        if (key is null || !Definitions.Value.TryGetValue(key, out VisualDefinition? definition))
            return null;

        VisualVariant variant = faction is not null && definition.Factions is not null &&
            definition.Factions.TryGetValue(faction, out VisualVariant? factionVariant)
                ? factionVariant
                : new VisualVariant(definition.Scene, definition.Scale, definition.Lods, definition.TrackGauge, definition.TrackWrap);

        Node3D root = new() { Name = "VehicleModel", Scale = Vector3.One * variant.Scale };
        PackedScene[] scenes = new PackedScene[1 + (variant.Lods?.Length ?? 0)];
        for (int i = 0; i < scenes.Length; i++)
            scenes[i] = LoadScene(i == 0 ? variant.Scene : variant.Lods![i - 1]);
        return new VehicleVisual(root, scenes, variant.TrackGauge, variant.TrackWrap);
    }

    private static PackedScene LoadScene(string path)
    {
        if (!Scenes.TryGetValue(path, out PackedScene? scene))
        {
            scene = GD.Load<PackedScene>(path)
                ?? throw new InvalidOperationException($"Vehicle scene could not be loaded: {path}");
            Scenes.Add(path, scene);
        }
        return scene;
    }

    private static Dictionary<string, VisualDefinition> LoadDefinitions()
    {
        string json = Godot.FileAccess.GetFileAsString("res://Assets/Vehicles/visuals.json");
        Dictionary<string, VisualDefinition> definitions = JsonSerializer.Deserialize<Dictionary<string, VisualDefinition>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Vehicle visual catalog is empty.");
        foreach (VisualDefinition definition in definitions.Values)
        {
            Validate(definition.Scene, definition.Scale, definition.Lods, definition.TrackGauge, definition.TrackWrap);
            if (definition.Factions is null)
                continue;
            foreach (VisualVariant variant in definition.Factions.Values)
                Validate(variant.Scene, variant.Scale, variant.Lods, variant.TrackGauge, variant.TrackWrap);
        }
        return definitions;
    }

    private static void Validate(string scene, float scale, string[]? lods, float trackGauge, float trackWrap)
    {
        if (!float.IsFinite(trackWrap) || trackWrap < 0)
            throw new InvalidOperationException("Vehicle track wrap must be a non-negative distance.");
        if (!IsResource(scene) || !float.IsFinite(scale) || scale <= 0)
            throw new InvalidOperationException("Vehicle visual definitions require a resource scene and a positive scale.");
        if (lods is not null && Array.Exists(lods, lod => !IsResource(lod)))
            throw new InvalidOperationException("Vehicle LOD scenes must be resource paths.");
        if (!float.IsFinite(trackGauge) || trackGauge < 0)
            throw new InvalidOperationException("Vehicle track gauge must be a non-negative distance.");
    }

    private static bool IsResource(string? path) => path is not null && path.StartsWith("res://", StringComparison.Ordinal);
}
