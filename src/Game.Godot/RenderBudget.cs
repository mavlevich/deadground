using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadGround.Client;

public sealed record ShadowTierSettings(
    [property: JsonPropertyName("shadow_enabled")] bool ShadowEnabled,
    [property: JsonPropertyName("shadow_splits")] int ShadowSplits,
    [property: JsonPropertyName("shadow_max_distance")] float ShadowMaxDistance,
    [property: JsonPropertyName("tree_shadows")] bool TreeShadows,
    [property: JsonPropertyName("terrain_shadows")] bool TerrainShadows);

public sealed record ShadowTiersSettings(
    [property: JsonPropertyName("close_to_play")] float CloseToPlay,
    [property: JsonPropertyName("play_to_wide")] float PlayToWide,
    [property: JsonPropertyName("hysteresis")] float Hysteresis,
    [property: JsonPropertyName("close")] ShadowTierSettings Close,
    [property: JsonPropertyName("play")] ShadowTierSettings Play,
    [property: JsonPropertyName("wide")] ShadowTierSettings Wide);

public sealed record RenderBudget(
    [property: JsonPropertyName("shadow_tiers")] ShadowTiersSettings ShadowTiers)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static RenderBudget Default { get; } = new(
        new ShadowTiersSettings(
            CloseToPlay: 55.0f,
            PlayToWide: 185.0f,
            Hysteresis: 5.0f,
            Close: new ShadowTierSettings(ShadowEnabled: true, ShadowSplits: 4, ShadowMaxDistance: 1800.0f, TreeShadows: true, TerrainShadows: true),
            Play: new ShadowTierSettings(ShadowEnabled: true, ShadowSplits: 2, ShadowMaxDistance: 1400.0f, TreeShadows: false, TerrainShadows: true),
            Wide: new ShadowTierSettings(ShadowEnabled: false, ShadowSplits: 1, ShadowMaxDistance: 1000.0f, TreeShadows: false, TerrainShadows: false)));

    public static RenderBudget Parse(string json, string sourceName = "JSON")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new EnvironmentProfileValidationException("json", $"Empty render budget JSON from {sourceName}.");
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            ValidateJsonStructure(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new EnvironmentProfileValidationException("json", $"JSON parsing failed for {sourceName}: {ex.Message}");
        }

        RenderBudget? budget;
        try
        {
            budget = JsonSerializer.Deserialize<RenderBudget>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new EnvironmentProfileValidationException("json", $"JSON parsing failed for {sourceName}: {ex.Message}");
        }

        if (budget is null)
        {
            throw new EnvironmentProfileValidationException("render_budget", $"Deserialization returned null for {sourceName}.");
        }

        budget.Validate();
        return budget;
    }

    public void Validate()
    {
        if (ShadowTiers is null)
        {
            throw new EnvironmentProfileValidationException("shadow_tiers", "Render budget missing required section 'shadow_tiers'.");
        }
        if (ShadowTiers.CloseToPlay <= 0f || ShadowTiers.PlayToWide <= ShadowTiers.CloseToPlay)
        {
            throw new EnvironmentProfileValidationException("shadow_tiers", "shadow_tiers must satisfy 0 < close_to_play < play_to_wide.");
        }
        if (ShadowTiers.Hysteresis < 0f || ShadowTiers.Hysteresis >= ShadowTiers.CloseToPlay || ShadowTiers.Hysteresis * 2f >= (ShadowTiers.PlayToWide - ShadowTiers.CloseToPlay))
        {
            throw new EnvironmentProfileValidationException("shadow_tiers.hysteresis", $"Field 'shadow_tiers.hysteresis' value {ShadowTiers.Hysteresis} is out of valid range.");
        }
        ValidateTier(ShadowTiers.Close, "shadow_tiers.close");
        ValidateTier(ShadowTiers.Play, "shadow_tiers.play");
        ValidateTier(ShadowTiers.Wide, "shadow_tiers.wide");
    }

    private static void ValidateTier(ShadowTierSettings? tier, string path)
    {
        if (tier is null)
        {
            throw new EnvironmentProfileValidationException(path, $"Missing required shadow tier '{path}'.");
        }
        if (tier.ShadowSplits != 1 && tier.ShadowSplits != 2 && tier.ShadowSplits != 4)
        {
            throw new EnvironmentProfileValidationException($"{path}.shadow_splits", $"Field '{path}.shadow_splits' must be 1, 2, or 4 (got {tier.ShadowSplits}).");
        }
        if (tier.ShadowMaxDistance < 10f || tier.ShadowMaxDistance > 10000f)
        {
            throw new EnvironmentProfileValidationException($"{path}.shadow_max_distance", $"Field '{path}.shadow_max_distance' value {tier.ShadowMaxDistance} is out of range [10, 10000].");
        }
    }

    private static void ValidateJsonStructure(JsonElement root)
    {
        RequireProperty(root, "shadow_tiers", "shadow_tiers");
        JsonElement tiers = root.GetProperty("shadow_tiers");
        RequireProperty(tiers, "close_to_play", "shadow_tiers.close_to_play");
        RequireProperty(tiers, "play_to_wide", "shadow_tiers.play_to_wide");
        RequireProperty(tiers, "hysteresis", "shadow_tiers.hysteresis");
        ValidateTierStructure(tiers, "close", "shadow_tiers.close");
        ValidateTierStructure(tiers, "play", "shadow_tiers.play");
        ValidateTierStructure(tiers, "wide", "shadow_tiers.wide");
    }

    private static void ValidateTierStructure(JsonElement tiers, string name, string fullPath)
    {
        RequireProperty(tiers, name, fullPath);
        JsonElement tier = tiers.GetProperty(name);
        RequireProperty(tier, "shadow_enabled", $"{fullPath}.shadow_enabled");
        RequireProperty(tier, "shadow_splits", $"{fullPath}.shadow_splits");
        RequireProperty(tier, "shadow_max_distance", $"{fullPath}.shadow_max_distance");
        RequireProperty(tier, "tree_shadows", $"{fullPath}.tree_shadows");
        RequireProperty(tier, "terrain_shadows", $"{fullPath}.terrain_shadows");
    }

    private static void RequireProperty(JsonElement element, string propName, string fullPath)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new EnvironmentProfileValidationException(fullPath, $"Expected JSON object for '{fullPath}'.");
        }

        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (prop.NameEquals(propName) || prop.Name.Equals(propName, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind != JsonValueKind.Null && prop.Value.ValueKind != JsonValueKind.Undefined)
                {
                    return;
                }
            }
        }

        throw new EnvironmentProfileValidationException(fullPath, $"Missing required field '{fullPath}'.");
    }

    public static string ReadBudgetJson()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "content/render/render_budget.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../content/render/render_budget.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../content/render/render_budget.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../content/render/render_budget.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../content/render/render_budget.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "content/render/render_budget.json")),
            Path.GetFullPath("content/render/render_budget.json")
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        const string godotPath = "res://content/render/render_budget.json";
        if (Godot.FileAccess.FileExists(godotPath))
        {
            return Godot.FileAccess.GetFileAsString(godotPath);
        }

        Assembly asm = typeof(RenderBudget).Assembly;
        using Stream? stream = asm.GetManifestResourceStream("DeadGround.Render.render_budget.json");
        if (stream is not null)
        {
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        throw new FileNotFoundException("Unable to locate 'content/render/render_budget.json'.");
    }

    public static RenderBudget LoadDefault() => Parse(ReadBudgetJson(), "render_budget");
}
