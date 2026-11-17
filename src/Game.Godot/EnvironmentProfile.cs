using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace DeadGround.Client;

public class EnvironmentProfileValidationException : InvalidOperationException
{
    public string FieldName { get; }

    public EnvironmentProfileValidationException(string fieldName, string message)
        : base(message)
    {
        FieldName = fieldName;
    }
}

public sealed record SunSettings(
    [property: JsonPropertyName("pitch")] float Pitch,
    [property: JsonPropertyName("yaw")] float Yaw,
    [property: JsonPropertyName("color")] string ColorHex,
    [property: JsonPropertyName("energy")] float Energy,
    [property: JsonPropertyName("shadow_enabled")] bool ShadowEnabled,
    [property: JsonPropertyName("shadow_max_distance")] float ShadowMaxDistance,
    [property: JsonPropertyName("shadow_pancake_size")] float ShadowPancakeSize,
    [property: JsonPropertyName("shadow_bias")] float ShadowBias,
    [property: JsonPropertyName("shadow_blur")] float ShadowBlur = 1.0f);

public sealed record SkySettings(
    [property: JsonPropertyName("sky_top_color")] string SkyTopColorHex,
    [property: JsonPropertyName("sky_horizon_color")] string SkyHorizonColorHex,
    [property: JsonPropertyName("ground_bottom_color")] string GroundBottomColorHex,
    [property: JsonPropertyName("ground_horizon_color")] string GroundHorizonColorHex,
    [property: JsonPropertyName("sun_angle_max")] float SunAngleMax,
    [property: JsonPropertyName("energy_multiplier")] float EnergyMultiplier);

public sealed record AmbientSettings(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("color")] string ColorHex,
    [property: JsonPropertyName("energy")] float Energy);

public sealed record TonemapSettings(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("exposure")] float Exposure,
    [property: JsonPropertyName("white")] float White);

public sealed record HazeSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("color")] string ColorHex,
    [property: JsonPropertyName("energy")] float Energy,
    [property: JsonPropertyName("density")] float Density,
    [property: JsonPropertyName("close_zoom_density")] float CloseZoomDensity,
    [property: JsonPropertyName("wide_zoom_density")] float WideZoomDensity,
    [property: JsonPropertyName("aerial_perspective")] float AerialPerspective,
    [property: JsonPropertyName("height")] float Height,
    [property: JsonPropertyName("height_density")] float HeightDensity,
    [property: JsonPropertyName("depth_begin")] float DepthBegin,
    [property: JsonPropertyName("depth_end")] float DepthEnd,
    [property: JsonPropertyName("mode")] string? Mode = "Exponential");

public sealed record SsaoSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("radius")] float Radius,
    [property: JsonPropertyName("intensity")] float Intensity,
    [property: JsonPropertyName("power")] float Power,
    [property: JsonPropertyName("detail")] float Detail);

public sealed record GlowSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("intensity")] float Intensity,
    [property: JsonPropertyName("bloom")] float Bloom);

public sealed record MaterialTintsSettings(
    [property: JsonPropertyName("terrain")] string TerrainHex,
    [property: JsonPropertyName("forest_conifer")] string ForestConiferHex,
    [property: JsonPropertyName("forest_deciduous")] string ForestDeciduousHex,
    [property: JsonPropertyName("water_shallows")] string WaterShallowsHex,
    [property: JsonPropertyName("water_deep")] string WaterDeepHex,
    [property: JsonPropertyName("roads_asphalt")] string RoadsAsphaltHex,
    [property: JsonPropertyName("roads_gravel")] string RoadsGravelHex,
    [property: JsonPropertyName("roads_dirt")] string RoadsDirtHex);

public sealed record EnvironmentProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sun")] SunSettings Sun,
    [property: JsonPropertyName("sky")] SkySettings Sky,
    [property: JsonPropertyName("ambient")] AmbientSettings Ambient,
    [property: JsonPropertyName("tonemap")] TonemapSettings Tonemap,
    [property: JsonPropertyName("haze")] HazeSettings Haze,
    [property: JsonPropertyName("ssao")] SsaoSettings? Ssao,
    [property: JsonPropertyName("glow")] GlowSettings? Glow,
    [property: JsonPropertyName("material_tints")] MaterialTintsSettings MaterialTints)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        string clean = hex.Trim();
        if (clean.StartsWith('#')) clean = clean[1..];
        if (clean.Length != 3 && clean.Length != 4 && clean.Length != 6 && clean.Length != 8)
        {
            return false;
        }

        foreach (char c in clean)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }

        try
        {
            color = new Color(hex.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new EnvironmentProfileValidationException("id", "Environment profile missing required field 'id'.");
        }
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new EnvironmentProfileValidationException("name", "Environment profile missing required field 'name'.");
        }

        if (Sun is null)
        {
            throw new EnvironmentProfileValidationException("sun", "Environment profile missing required section 'sun'.");
        }
        if (Sun.Pitch < -90f || Sun.Pitch > 0f)
        {
            throw new EnvironmentProfileValidationException("sun.pitch", $"Field 'sun.pitch' value {Sun.Pitch} is out of range [-90, 0].");
        }
        if (Sun.Yaw < -360f || Sun.Yaw > 360f)
        {
            throw new EnvironmentProfileValidationException("sun.yaw", $"Field 'sun.yaw' value {Sun.Yaw} is out of range [-360, 360].");
        }
        if (!TryParseHexColor(Sun.ColorHex, out _))
        {
            throw new EnvironmentProfileValidationException("sun.color", $"Field 'sun.color' is missing or not a valid hex color: '{Sun.ColorHex}'.");
        }
        if (Sun.Energy < 0f || Sun.Energy > 16f)
        {
            throw new EnvironmentProfileValidationException("sun.energy", $"Field 'sun.energy' value {Sun.Energy} is out of range [0, 16].");
        }
        if (Sun.ShadowMaxDistance < 10f || Sun.ShadowMaxDistance > 10000f)
        {
            throw new EnvironmentProfileValidationException("sun.shadow_max_distance", $"Field 'sun.shadow_max_distance' value {Sun.ShadowMaxDistance} is out of range [10, 10000].");
        }
        if (Sun.ShadowPancakeSize < 0f || Sun.ShadowPancakeSize > 100f)
        {
            throw new EnvironmentProfileValidationException("sun.shadow_pancake_size", $"Field 'sun.shadow_pancake_size' value {Sun.ShadowPancakeSize} is out of range [0, 100].");
        }
        if (Sun.ShadowBias < 0f || Sun.ShadowBias > 1f)
        {
            throw new EnvironmentProfileValidationException("sun.shadow_bias", $"Field 'sun.shadow_bias' value {Sun.ShadowBias} is out of range [0, 1].");
        }
        if (Sun.ShadowBlur < 0f || Sun.ShadowBlur > 10f)
        {
            throw new EnvironmentProfileValidationException("sun.shadow_blur", $"Field 'sun.shadow_blur' value {Sun.ShadowBlur} is out of range [0, 10].");
        }

        if (Sky is null)
        {
            throw new EnvironmentProfileValidationException("sky", "Environment profile missing required section 'sky'.");
        }
        if (!TryParseHexColor(Sky.SkyTopColorHex, out _))
        {
            throw new EnvironmentProfileValidationException("sky.sky_top_color", $"Field 'sky.sky_top_color' is missing or not a valid hex color: '{Sky.SkyTopColorHex}'.");
        }
        if (!TryParseHexColor(Sky.SkyHorizonColorHex, out _))
        {
            throw new EnvironmentProfileValidationException("sky.sky_horizon_color", $"Field 'sky.sky_horizon_color' is missing or not a valid hex color: '{Sky.SkyHorizonColorHex}'.");
        }
        if (!TryParseHexColor(Sky.GroundBottomColorHex, out _))
        {
            throw new EnvironmentProfileValidationException("sky.ground_bottom_color", $"Field 'sky.ground_bottom_color' is missing or not a valid hex color: '{Sky.GroundBottomColorHex}'.");
        }
        if (!TryParseHexColor(Sky.GroundHorizonColorHex, out _))
        {
            throw new EnvironmentProfileValidationException("sky.ground_horizon_color", $"Field 'sky.ground_horizon_color' is missing or not a valid hex color: '{Sky.GroundHorizonColorHex}'.");
        }
        if (Sky.SunAngleMax < 0f || Sky.SunAngleMax > 90f)
        {
            throw new EnvironmentProfileValidationException("sky.sun_angle_max", $"Field 'sky.sun_angle_max' value {Sky.SunAngleMax} is out of range [0, 90].");
        }
        if (Sky.EnergyMultiplier < 0f || Sky.EnergyMultiplier > 16f)
        {
            throw new EnvironmentProfileValidationException("sky.energy_multiplier", $"Field 'sky.energy_multiplier' value {Sky.EnergyMultiplier} is out of range [0, 16].");
        }

        if (Ambient is null)
        {
            throw new EnvironmentProfileValidationException("ambient", "Environment profile missing required section 'ambient'.");
        }
        string ambientSource = Ambient.Source?.Trim() ?? "";
        if (!ambientSource.Equals("Sky", StringComparison.OrdinalIgnoreCase) &&
            !ambientSource.Equals("Color", StringComparison.OrdinalIgnoreCase) &&
            !ambientSource.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new EnvironmentProfileValidationException("ambient.source", $"Field 'ambient.source' has invalid source '{Ambient.Source}'. Expected Sky, Color, or Disabled.");
        }
        if (!TryParseHexColor(Ambient.ColorHex, out _))
        {
            throw new EnvironmentProfileValidationException("ambient.color", $"Field 'ambient.color' is missing or not a valid hex color: '{Ambient.ColorHex}'.");
        }
        if (Ambient.Energy < 0f || Ambient.Energy > 16f)
        {
            throw new EnvironmentProfileValidationException("ambient.energy", $"Field 'ambient.energy' value {Ambient.Energy} is out of range [0, 16].");
        }

        if (Tonemap is null)
        {
            throw new EnvironmentProfileValidationException("tonemap", "Environment profile missing required section 'tonemap'.");
        }
        string tonemapMode = Tonemap.Mode?.Trim() ?? "";
        if (!tonemapMode.Equals("Linear", StringComparison.OrdinalIgnoreCase) &&
            !tonemapMode.Equals("Reinhard", StringComparison.OrdinalIgnoreCase) &&
            !tonemapMode.Equals("Filmic", StringComparison.OrdinalIgnoreCase) &&
            !tonemapMode.Equals("Aces", StringComparison.OrdinalIgnoreCase))
        {
            throw new EnvironmentProfileValidationException("tonemap.mode", $"Field 'tonemap.mode' has invalid mode '{Tonemap.Mode}'. Expected Linear, Reinhard, Filmic, or Aces.");
        }
        if (Tonemap.Exposure <= 0f || Tonemap.Exposure > 16f)
        {
            throw new EnvironmentProfileValidationException("tonemap.exposure", $"Field 'tonemap.exposure' value {Tonemap.Exposure} is out of range (0, 16].");
        }
        if (Tonemap.White <= 0f || Tonemap.White > 16f)
        {
            throw new EnvironmentProfileValidationException("tonemap.white", $"Field 'tonemap.white' value {Tonemap.White} is out of range (0, 16].");
        }

        if (Haze is null)
        {
            throw new EnvironmentProfileValidationException("haze", "Environment profile missing required section 'haze'.");
        }
        if (!TryParseHexColor(Haze.ColorHex, out _))
        {
            throw new EnvironmentProfileValidationException("haze.color", $"Field 'haze.color' is missing or not a valid hex color: '{Haze.ColorHex}'.");
        }
        if (Haze.Energy < 0f || Haze.Energy > 16f)
        {
            throw new EnvironmentProfileValidationException("haze.energy", $"Field 'haze.energy' value {Haze.Energy} is out of range [0, 16].");
        }
        if (Haze.Density < 0f || Haze.Density > 0.05f)
        {
            throw new EnvironmentProfileValidationException("haze.density", $"Field 'haze.density' value {Haze.Density} is out of range [0, 0.05].");
        }
        if (Haze.CloseZoomDensity < 0f || Haze.CloseZoomDensity > 0.05f)
        {
            throw new EnvironmentProfileValidationException("haze.close_zoom_density", $"Field 'haze.close_zoom_density' value {Haze.CloseZoomDensity} is out of range [0, 0.05].");
        }
        if (Haze.WideZoomDensity < 0f || Haze.WideZoomDensity > 0.05f)
        {
            throw new EnvironmentProfileValidationException("haze.wide_zoom_density", $"Field 'haze.wide_zoom_density' value {Haze.WideZoomDensity} is out of range [0, 0.05].");
        }
        if (Haze.AerialPerspective < 0f || Haze.AerialPerspective > 1f)
        {
            throw new EnvironmentProfileValidationException("haze.aerial_perspective", $"Field 'haze.aerial_perspective' value {Haze.AerialPerspective} is out of range [0, 1].");
        }
        if (Haze.Height < -500f || Haze.Height > 2000f)
        {
            throw new EnvironmentProfileValidationException("haze.height", $"Field 'haze.height' value {Haze.Height} is out of range [-500, 2000].");
        }
        if (Haze.HeightDensity < -10f || Haze.HeightDensity > 10f)
        {
            throw new EnvironmentProfileValidationException("haze.height_density", $"Field 'haze.height_density' value {Haze.HeightDensity} is out of range [-10, 10].");
        }
        if (Haze.DepthBegin < 0f || Haze.DepthBegin > 10000f)
        {
            throw new EnvironmentProfileValidationException("haze.depth_begin", $"Field 'haze.depth_begin' value {Haze.DepthBegin} is out of range [0, 10000].");
        }
        if (Haze.DepthEnd <= Haze.DepthBegin || Haze.DepthEnd > 20000f)
        {
            throw new EnvironmentProfileValidationException("haze.depth_end", $"Field 'haze.depth_end' value {Haze.DepthEnd} must be greater than depth_begin {Haze.DepthBegin} and at most 20000.");
        }

        if (Ssao is not null)
        {
            if (Ssao.Radius <= 0f || Ssao.Radius > 16f)
            {
                throw new EnvironmentProfileValidationException("ssao.radius", $"Field 'ssao.radius' value {Ssao.Radius} is out of range (0, 16].");
            }
            if (Ssao.Intensity < 0f || Ssao.Intensity > 16f)
            {
                throw new EnvironmentProfileValidationException("ssao.intensity", $"Field 'ssao.intensity' value {Ssao.Intensity} is out of range [0, 16].");
            }
        }

        if (Glow is not null)
        {
            if (Glow.Intensity < 0f || Glow.Intensity > 16f)
            {
                throw new EnvironmentProfileValidationException("glow.intensity", $"Field 'glow.intensity' value {Glow.Intensity} is out of range [0, 16].");
            }
            if (Glow.Bloom < 0f || Glow.Bloom > 16f)
            {
                throw new EnvironmentProfileValidationException("glow.bloom", $"Field 'glow.bloom' value {Glow.Bloom} is out of range [0, 16].");
            }
        }

        if (MaterialTints is null)
        {
            throw new EnvironmentProfileValidationException("material_tints", "Environment profile missing required section 'material_tints'.");
        }
        if (!TryParseHexColor(MaterialTints.TerrainHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.terrain", $"Field 'material_tints.terrain' is missing or not a valid hex color: '{MaterialTints.TerrainHex}'.");
        }
        if (!TryParseHexColor(MaterialTints.ForestConiferHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.forest_conifer", $"Field 'material_tints.forest_conifer' is missing or not a valid hex color: '{MaterialTints.ForestConiferHex}'.");
        }
        if (!TryParseHexColor(MaterialTints.ForestDeciduousHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.forest_deciduous", $"Field 'material_tints.forest_deciduous' is missing or not a valid hex color: '{MaterialTints.ForestDeciduousHex}'.");
        }
        if (!TryParseHexColor(MaterialTints.WaterShallowsHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.water_shallows", $"Field 'material_tints.water_shallows' is missing or not a valid hex color: '{MaterialTints.WaterShallowsHex}'.");
        }
        if (!TryParseHexColor(MaterialTints.WaterDeepHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.water_deep", $"Field 'material_tints.water_deep' is missing or not a valid hex color: '{MaterialTints.WaterDeepHex}'.");
        }
        if (!TryParseHexColor(MaterialTints.RoadsAsphaltHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.roads_asphalt", $"Field 'material_tints.roads_asphalt' is missing or not a valid hex color: '{MaterialTints.RoadsAsphaltHex}'.");
        }
        if (!TryParseHexColor(MaterialTints.RoadsGravelHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.roads_gravel", $"Field 'material_tints.roads_gravel' is missing or not a valid hex color: '{MaterialTints.RoadsGravelHex}'.");
        }
        if (!TryParseHexColor(MaterialTints.RoadsDirtHex, out _))
        {
            throw new EnvironmentProfileValidationException("material_tints.roads_dirt", $"Field 'material_tints.roads_dirt' is missing or not a valid hex color: '{MaterialTints.RoadsDirtHex}'.");
        }
    }

    private static void ValidateJsonStructure(JsonElement root)
    {
        RequireProperty(root, "id", "id");
        RequireProperty(root, "name", "name");

        RequireProperty(root, "sun", "sun");
        JsonElement sun = root.GetProperty("sun");
        RequireProperty(sun, "pitch", "sun.pitch");
        RequireProperty(sun, "yaw", "sun.yaw");
        RequireProperty(sun, "color", "sun.color");
        RequireProperty(sun, "energy", "sun.energy");
        RequireProperty(sun, "shadow_enabled", "sun.shadow_enabled");
        RequireProperty(sun, "shadow_max_distance", "sun.shadow_max_distance");
        RequireProperty(sun, "shadow_pancake_size", "sun.shadow_pancake_size");
        RequireProperty(sun, "shadow_bias", "sun.shadow_bias");

        RequireProperty(root, "sky", "sky");
        JsonElement sky = root.GetProperty("sky");
        RequireProperty(sky, "sky_top_color", "sky.sky_top_color");
        RequireProperty(sky, "sky_horizon_color", "sky.sky_horizon_color");
        RequireProperty(sky, "ground_bottom_color", "sky.ground_bottom_color");
        RequireProperty(sky, "ground_horizon_color", "sky.ground_horizon_color");
        RequireProperty(sky, "sun_angle_max", "sky.sun_angle_max");
        RequireProperty(sky, "energy_multiplier", "sky.energy_multiplier");

        RequireProperty(root, "ambient", "ambient");
        JsonElement ambient = root.GetProperty("ambient");
        RequireProperty(ambient, "source", "ambient.source");
        RequireProperty(ambient, "color", "ambient.color");
        RequireProperty(ambient, "energy", "ambient.energy");

        RequireProperty(root, "tonemap", "tonemap");
        JsonElement tonemap = root.GetProperty("tonemap");
        RequireProperty(tonemap, "mode", "tonemap.mode");
        RequireProperty(tonemap, "exposure", "tonemap.exposure");
        RequireProperty(tonemap, "white", "tonemap.white");

        RequireProperty(root, "haze", "haze");
        JsonElement haze = root.GetProperty("haze");
        RequireProperty(haze, "enabled", "haze.enabled");
        RequireProperty(haze, "color", "haze.color");
        RequireProperty(haze, "energy", "haze.energy");
        RequireProperty(haze, "density", "haze.density");
        RequireProperty(haze, "close_zoom_density", "haze.close_zoom_density");
        RequireProperty(haze, "wide_zoom_density", "haze.wide_zoom_density");
        RequireProperty(haze, "aerial_perspective", "haze.aerial_perspective");
        RequireProperty(haze, "height", "haze.height");
        RequireProperty(haze, "height_density", "haze.height_density");
        RequireProperty(haze, "depth_begin", "haze.depth_begin");
        RequireProperty(haze, "depth_end", "haze.depth_end");

        if (root.TryGetProperty("ssao", out JsonElement ssao) && ssao.ValueKind == JsonValueKind.Object)
        {
            if (ssao.TryGetProperty("enabled", out JsonElement ssaoEnabled) && ssaoEnabled.GetBoolean())
            {
                RequireProperty(ssao, "radius", "ssao.radius");
                RequireProperty(ssao, "intensity", "ssao.intensity");
                RequireProperty(ssao, "power", "ssao.power");
                RequireProperty(ssao, "detail", "ssao.detail");
            }
        }

        if (root.TryGetProperty("glow", out JsonElement glow) && glow.ValueKind == JsonValueKind.Object)
        {
            if (glow.TryGetProperty("enabled", out JsonElement glowEnabled) && glowEnabled.GetBoolean())
            {
                RequireProperty(glow, "intensity", "glow.intensity");
                RequireProperty(glow, "bloom", "glow.bloom");
            }
        }

        RequireProperty(root, "material_tints", "material_tints");
        JsonElement tints = root.GetProperty("material_tints");
        RequireProperty(tints, "terrain", "material_tints.terrain");
        RequireProperty(tints, "forest_conifer", "material_tints.forest_conifer");
        RequireProperty(tints, "forest_deciduous", "material_tints.forest_deciduous");
        RequireProperty(tints, "water_shallows", "material_tints.water_shallows");
        RequireProperty(tints, "water_deep", "material_tints.water_deep");
        RequireProperty(tints, "roads_asphalt", "material_tints.roads_asphalt");
        RequireProperty(tints, "roads_gravel", "material_tints.roads_gravel");
        RequireProperty(tints, "roads_dirt", "material_tints.roads_dirt");
    }

    private static void RequireProperty(JsonElement element, string propName, string fullPath)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new EnvironmentProfileValidationException(fullPath, $"Expected JSON object for '{fullPath}'.");
        }

        bool found = false;
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            if (prop.NameEquals(propName) || prop.Name.Equals(propName, StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind != JsonValueKind.Null && prop.Value.ValueKind != JsonValueKind.Undefined)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            throw new EnvironmentProfileValidationException(fullPath, $"Missing required field '{fullPath}'.");
        }
    }

    public static EnvironmentProfile Parse(string json, string sourceName = "JSON")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new EnvironmentProfileValidationException("json", $"Empty JSON content from {sourceName}.");
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

        EnvironmentProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<EnvironmentProfile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new EnvironmentProfileValidationException("json", $"JSON parsing failed for {sourceName}: {ex.Message}");
        }

        if (profile is null)
        {
            throw new EnvironmentProfileValidationException("profile", $"Deserialization returned null for {sourceName}.");
        }

        profile.Validate();
        return profile;
    }

    public static EnvironmentProfile LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Environment profile file not found: {path}", path);
        }
        string json = File.ReadAllText(path);
        return Parse(json, path);
    }

    public static string? TryResolveProfilePath(string profileId)
    {
        string fileName = $"{profileId}.json";
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "content/environments", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../content/environments", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../content/environments", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../content/environments", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../content/environments", fileName)),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "content/environments", fileName)),
            Path.GetFullPath(Path.Combine("content/environments", fileName))
        ];

        foreach (string path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static string ReadProfileJson(string profileId)
    {
        string sanitized = string.IsNullOrWhiteSpace(profileId) ? "overcast" : profileId.Trim().ToLowerInvariant();
        string? resolved = TryResolveProfilePath(sanitized);
        if (resolved is not null && File.Exists(resolved))
        {
            return File.ReadAllText(resolved);
        }

        string godotPath = $"res://content/environments/{sanitized}.json";
        if (Godot.FileAccess.FileExists(godotPath))
        {
            return Godot.FileAccess.GetFileAsString(godotPath);
        }

        Assembly asm = typeof(EnvironmentProfile).Assembly;
        string resName = $"DeadGround.Environments.{sanitized}.json";
        using Stream? stream = asm.GetManifestResourceStream(resName);
        if (stream is not null)
        {
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        throw new FileNotFoundException($"Unable to locate environment profile JSON '{profileId}'.");
    }

    public static EnvironmentProfile BaselineProfile { get; } = new(
        "baseline",
        "Baseline (Main)",
        new SunSettings(-55.0f, -35.0f, "#fff0d2", 1.2f, true, 1800.0f, 15.0f, 0.04f),
        new SkySettings("#17211c", "#17211c", "#17211c", "#17211c", 30.0f, 1.0f),
        new AmbientSettings("Color", "#bfd0c3", 0.45f),
        new TonemapSettings("Filmic", 1.0f, 1.0f),
        new HazeSettings(false, "#8c9ca8", 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 10.0f, 1000.0f, "Exponential"),
        new SsaoSettings(false, 1.0f, 0.0f, 1.0f, 0.5f),
        new GlowSettings(false, 0.0f, 0.0f),
        new MaterialTintsSettings("#ffffff", "#ffffff", "#ffffff", "#ffffff", "#ffffff", "#ffffff", "#ffffff", "#ffffff"));

    public static EnvironmentProfile Load(string profileId)
    {
        string sanitized = string.IsNullOrWhiteSpace(profileId) ? "overcast" : profileId.Trim().ToLowerInvariant();
        if (sanitized == "baseline")
        {
            return BaselineProfile;
        }

        string json = ReadProfileJson(sanitized);
        return Parse(json, sanitized);
    }
}
