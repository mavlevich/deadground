using System.Text.Json;
using Godot;

namespace DeadGround.Client;

// Presentation assets are keyed by content IDs, never by catalog ordering.
internal static class VehicleVisualCatalog
{
    private sealed record VisualDefinition(string Scene, float Scale);
    private static readonly Lazy<Dictionary<string, VisualDefinition>> Definitions = new(LoadDefinitions);
    private static readonly Dictionary<string, PackedScene> Scenes = new(StringComparer.Ordinal);

    internal static Node3D? Instantiate(string? unitId)
    {
        if (unitId is null || !Definitions.Value.TryGetValue(unitId, out VisualDefinition? definition))
            return null;

        if (!Scenes.TryGetValue(definition.Scene, out PackedScene? scene))
        {
            scene = GD.Load<PackedScene>(definition.Scene)
                ?? throw new InvalidOperationException($"Vehicle scene could not be loaded: {definition.Scene}");
            Scenes.Add(definition.Scene, scene);
        }

        Node3D model = scene.Instantiate<Node3D>();
        model.Name = "VehicleModel";
        model.Scale = Vector3.One * definition.Scale;
        return model;
    }

    private static Dictionary<string, VisualDefinition> LoadDefinitions()
    {
        string json = Godot.FileAccess.GetFileAsString("res://Assets/Vehicles/visuals.json");
        Dictionary<string, VisualDefinition> definitions = JsonSerializer.Deserialize<Dictionary<string, VisualDefinition>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Vehicle visual catalog is empty.");
        foreach (VisualDefinition definition in definitions.Values)
        {
            if (!definition.Scene.StartsWith("res://", StringComparison.Ordinal) ||
                !float.IsFinite(definition.Scale) || definition.Scale <= 0)
                throw new InvalidOperationException("Vehicle visual definitions require a resource scene and a positive scale.");
        }
        return definitions;
    }
}
