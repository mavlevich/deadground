using DeadGround.Engine;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Regression tests for the environment profile system, quality levels,
/// haze zoom scaling, profile switching, and zero allocation performance.
/// </summary>
internal static class EnvironmentProfileRegressionTests
{
    public static void Run(Node3D scene)
    {
        CheckProfilesLoad();
        CheckValidationErrors();
        CheckRequiredFieldsAbsence();
        CheckApplyProfileAndLight(scene);
        CheckRuntimeProfileSwitching(scene);
        CheckHazeScalingAtZoomLevels(scene);
        CheckHazeModeAndDensity(scene);
        CheckHashesUnaffected();
        CheckZeroAllocations(scene);
        CheckQualityLevels(scene);
        CheckPreviewSavedQuality(scene);
        CheckRoadFrontNormalsUpward(scene);
        CheckRenderBudgetValidation();
        CheckShadowZoomTiersAndFlatLayerShadows(scene);
        CheckShadowTargetLifecycleAcrossMapSwitch(scene);
        CheckRenderScaleViewportMapping(scene);
        CheckChunkedTerrainSharedEdgesAndLod(scene);
        CheckRegressionFailurePropagation();

        GD.Print("ENVIRONMENT_PROFILE_REGRESSIONS_OK 17/17 passed: load_profiles validation required_fields apply_light runtime_switching haze_scaling haze_mode hashes_unaffected zero_allocations quality_levels preview_saved_quality road_normals render_budget shadow_zoom_tiers map_switch_lifecycle render_scale_msaa chunked_terrain_lod");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckProfilesLoad()
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        Require(overcast.Id == "overcast", $"Expected id 'overcast', got '{overcast.Id}'");
        Require(overcast.Sun.Energy > 0f, "Overcast sun energy must be positive.");
        Require(overcast.Haze.Enabled, "Overcast haze must be enabled.");
        Require(overcast.Haze.CloseZoomDensity > overcast.Haze.WideZoomDensity, "Close zoom density must be higher than wide zoom density.");

        EnvironmentProfile clear = EnvironmentProfile.Load("clear_afternoon");
        Require(clear.Id == "clear_afternoon", $"Expected id 'clear_afternoon', got '{clear.Id}'");
        Require(clear.Sun.Energy > 0f, "Clear afternoon sun energy must be positive.");
        Require(clear.Haze.Enabled, "Clear afternoon haze must be enabled.");
    }

    private static void CheckValidationErrors()
    {
        string rawJson = EnvironmentProfile.ReadProfileJson("overcast");
        Require(!string.IsNullOrWhiteSpace(rawJson), "Unable to read overcast.json for validation tests.");

        // 1. Missing required field 'id'
        AssertValidationError(rawJson.Replace("\"id\": \"overcast\",", ""), "id");

        // 2. Out of range sun.pitch
        string badSunPitch = rawJson.Replace("\"pitch\": -48.0,", "\"pitch\": 10.0,");
        AssertValidationError(badSunPitch, "sun.pitch");

        // 3. Out of range sun.energy
        string badSunEnergy = rawJson.Replace("\"energy\": 0.95,", "\"energy\": 25.0,");
        AssertValidationError(badSunEnergy, "sun.energy");

        // 4. Invalid sun.color hex
        string badSunColor = rawJson.Replace("\"color\": \"#dce2e8\",", "\"color\": \"not-a-color\",");
        AssertValidationError(badSunColor, "sun.color");

        // 5. Out of range sky.sun_angle_max
        string badSky = rawJson.Replace("\"sun_angle_max\": 35.0,", "\"sun_angle_max\": 120.0,");
        AssertValidationError(badSky, "sky.sun_angle_max");

        // 6. Invalid ambient.source
        string badAmbient = rawJson.Replace("\"source\": \"Sky\",", "\"source\": \"InvalidSource\",");
        AssertValidationError(badAmbient, "ambient.source");

        // 7. Invalid tonemap.mode
        string badTonemap = rawJson.Replace("\"mode\": \"Filmic\",", "\"mode\": \"UnsupportedTonemap\",");
        AssertValidationError(badTonemap, "tonemap.mode");

        // 8. Out of range haze.density
        string badHazeDensity = rawJson.Replace("\"density\": 0.00014,", "\"density\": 0.8,");
        AssertValidationError(badHazeDensity, "haze.density");

        // 9. haze.depth_end <= haze.depth_begin
        string badHazeDepth = rawJson.Replace("\"depth_begin\": 15.0,", "\"depth_begin\": 5000.0,").Replace("\"depth_end\": 4500.0", "\"depth_end\": 1000.0");
        AssertValidationError(badHazeDepth, "haze.depth_end");
    }

    private static void AssertValidationError(string json, string expectedField)
    {
        try
        {
            EnvironmentProfile.Parse(json, "test_validation");
            throw new InvalidOperationException($"Expected validation error for field '{expectedField}', but parse succeeded.");
        }
        catch (EnvironmentProfileValidationException vex)
        {
            Require(vex.FieldName == expectedField || vex.Message.Contains(expectedField, StringComparison.OrdinalIgnoreCase),
                $"Expected field '{expectedField}', got '{vex.FieldName}': {vex.Message}");
        }
    }

    private static void CheckApplyProfileAndLight(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            view.ApplyProfile(overcast, EnvironmentQuality.High);

            Require(Mathf.IsEqualApprox(view.Sun.LightEnergy, overcast.Sun.Energy),
                $"Sun energy expected {overcast.Sun.Energy}, got {view.Sun.LightEnergy}");
            Require(Mathf.IsEqualApprox(view.Sun.RotationDegrees.X, overcast.Sun.Pitch),
                $"Sun pitch expected {overcast.Sun.Pitch}, got {view.Sun.RotationDegrees.X}");
            Require(Mathf.IsEqualApprox(view.GodotEnvironment.TonemapExposure, overcast.Tonemap.Exposure),
                $"Tonemap exposure expected {overcast.Tonemap.Exposure}, got {view.GodotEnvironment.TonemapExposure}");
            Require(view.GodotEnvironment.FogEnabled == overcast.Haze.Enabled,
                "FogEnabled mismatch.");
            Require(view.GodotEnvironment.SsaoEnabled == true,
                "SSAO should be enabled on High quality.");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckRuntimeProfileSwitching(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            EnvironmentProfile clear = EnvironmentProfile.Load("clear_afternoon");

            view.ApplyProfile(overcast);
            int initialChildCount = view.GetChildCount();

            // Switch profiles repeatedly
            for (int i = 0; i < 10; i++)
            {
                view.SetProfile(clear);
                Require(view.ActiveProfile.Id == "clear_afternoon", "Active profile should be clear_afternoon");
                Require(Mathf.IsEqualApprox(view.Sun.LightEnergy, clear.Sun.Energy), "Sun energy should update on switch.");

                view.SetProfile(overcast);
                Require(view.ActiveProfile.Id == "overcast", "Active profile should be overcast");
                Require(Mathf.IsEqualApprox(view.Sun.LightEnergy, overcast.Sun.Energy), "Sun energy should update on switch.");

                Require(view.GetChildCount() == initialChildCount,
                    $"Child node count changed during profile switch: expected {initialChildCount}, got {view.GetChildCount()}");
            }
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckHazeScalingAtZoomLevels(Node3D scene)
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        float close = overcast.Haze.CloseZoomDensity;
        float wide = overcast.Haze.WideZoomDensity;

        // At close zoom (size <= 120), density is close zoom density
        float dClose = EnvironmentView.CalculateHazeDensity(45f, close, wide);
        Require(Mathf.IsEqualApprox(dClose, close, 1e-6f),
            $"Close zoom density expected {close}, got {dClose}");

        // At wide zoom (size >= 5500), density is wide zoom density
        float dWide = EnvironmentView.CalculateHazeDensity(5500f, close, wide);
        Require(Mathf.IsEqualApprox(dWide, wide, 1e-6f),
            $"Wide zoom density expected {wide}, got {dWide}");

        // At play zoom (size 220), density is intermediate
        float dPlay = EnvironmentView.CalculateHazeDensity(220f, close, wide);
        Require(dPlay < close && dPlay > wide,
            $"Play zoom density must be between {wide} and {close}, got {dPlay}");

        // Readability check: at maximum camera distance on Ustye (~8800m at size 5500),
        // transmission exp(-distance * density) must remain >= 0.60 (not washed out)
        float maxDistance = 8800f;
        float transmission = MathF.Exp(-maxDistance * dWide);
        Require(transmission >= 0.60f,
            $"Wide zoom transmission must be >= 0.60, got {transmission:F3} (haze density {dWide})");
    }

    private static void CheckHashesUnaffected()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        ulong mapHash1 = MatchSetup.ComputeMapHash(geometry);
        ulong contentHash1 = ScenarioContent.LoadCatalog("ustye-ground").ContentHash;
        MatchSetup setup1 = UstyeGroundScenario.CreateMatchSetup(geometry);

        // Load and switch profiles
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        EnvironmentProfile clear = EnvironmentProfile.Load("clear_afternoon");

        ulong mapHash2 = MatchSetup.ComputeMapHash(geometry);
        ulong contentHash2 = ScenarioContent.LoadCatalog("ustye-ground").ContentHash;
        MatchSetup setup2 = UstyeGroundScenario.CreateMatchSetup(geometry);

        Require(mapHash1 == mapHash2, "MapHash must not be affected by environment profile.");
        Require(contentHash1 == contentHash2, "ContentHash must not be affected by environment profile.");
        Require(setup1.MapHash == setup2.MapHash, "MatchSetup MapHash mismatch.");
        Require(setup1.ContentHash == setup2.ContentHash, "MatchSetup ContentHash mismatch.");
    }

    private static void CheckZeroAllocations(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            view.ApplyProfile(overcast);

            // Warmup
            for (int w = 0; w < 15; w++)
            {
                view.UpdateHazeForCamera(220f + w * 10f, Vector3.Zero);
            }

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 100; f++)
            {
                float cameraSize = 100f + f * 50f;
                view.UpdateHazeForCamera(cameraSize, new Vector3(f * 2f, 0f, f * 2f));
            }
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAlloc = allocAfter - allocBefore;

            Require(totalAlloc == 0,
                $"EnvironmentView.UpdateHazeForCamera allocated {totalAlloc} bytes across 100 frames. Must be 0 bytes.");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckQualityLevels(Node3D scene)
    {
        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
            view.ApplyProfile(overcast, EnvironmentQuality.High);

            // 1. High Quality
            Require(view.ActiveQuality == EnvironmentQuality.High, "Expected High quality.");
            Require(view.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits,
                "High quality shadow mode should be Parallel4Splits.");
            Require(view.GodotEnvironment.SsaoEnabled == true, "High quality SSAO should be enabled.");
            Require(Mathf.IsEqualApprox(view.GodotEnvironment.FogAerialPerspective, overcast.Haze.AerialPerspective),
                "High quality aerial perspective mismatch.");
            Require(Mathf.IsEqualApprox(view.Sun.DirectionalShadowMaxDistance, overcast.Sun.ShadowMaxDistance),
                "High quality shadow distance mismatch.");

            // 2. Medium Quality
            view.SetQuality(EnvironmentQuality.Medium);
            Require(view.ActiveQuality == EnvironmentQuality.Medium, "Expected Medium quality.");
            Require(view.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits,
                "Medium quality shadow mode should be Parallel2Splits.");
            Require(view.GodotEnvironment.SsaoEnabled == true, "Medium quality SSAO should be enabled.");
            Require(view.Sun.DirectionalShadowMaxDistance == 1600f, "Medium quality shadow distance should be 1600.");

            // 3. Low Quality
            view.SetQuality(EnvironmentQuality.Low);
            Require(view.ActiveQuality == EnvironmentQuality.Low, "Expected Low quality.");
            Require(view.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Orthogonal,
                "Low quality shadow mode should be Orthogonal.");
            Require(view.GodotEnvironment.SsaoEnabled == false, "Low quality SSAO must be disabled.");
            Require(view.GodotEnvironment.FogAerialPerspective == 0f, "Low quality aerial perspective must be 0.");
            Require(view.Sun.DirectionalShadowMaxDistance == 1200f, "Low quality shadow distance should be 1200.");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckRequiredFieldsAbsence()
    {
        string rawJson = EnvironmentProfile.ReadProfileJson("overcast");
        Require(!string.IsNullOrWhiteSpace(rawJson), "Unable to read overcast.json for required fields test.");

        // 1. Missing sun.energy (must reject rather than default to 0)
        string noSunEnergy = rawJson.Replace("\"energy\": 0.95,", "");
        AssertValidationError(noSunEnergy, "sun.energy");

        // 2. Missing sun.pitch
        string noSunPitch = rawJson.Replace("\"pitch\": -48.0,", "");
        AssertValidationError(noSunPitch, "sun.pitch");

        // 3. Missing sky.sky_top_color
        string noSkyColor = rawJson.Replace("\"sky_top_color\": \"#485a6c\",", "");
        AssertValidationError(noSkyColor, "sky.sky_top_color");

        // 4. Missing ambient.energy
        string noAmbientEnergy = rawJson.Replace("\"energy\": 0.65", "\"energy_removed\": 0.65");
        AssertValidationError(noAmbientEnergy, "ambient.energy");

        // 5. Missing tonemap.mode
        string noTonemapMode = rawJson.Replace("\"mode\": \"Filmic\",", "");
        AssertValidationError(noTonemapMode, "tonemap.mode");

        // 6. Missing haze.density
        string noHazeDensity = rawJson.Replace("\"density\": 0.00014,", "");
        AssertValidationError(noHazeDensity, "haze.density");

        // 7. Missing material_tints.terrain
        string noTerrainTint = rawJson.Replace("\"terrain\": \"#ffffff\",", "");
        AssertValidationError(noTerrainTint, "material_tints.terrain");
    }

    private static void CheckHazeModeAndDensity(Node3D scene)
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        Require(string.Equals(overcast.Haze.Mode, "Exponential", StringComparison.OrdinalIgnoreCase),
            $"Expected haze mode 'Exponential', got '{overcast.Haze.Mode}'");

        EnvironmentView view = new();
        scene.AddChild(view);
        try
        {
            view.ApplyProfile(overcast, EnvironmentQuality.High);
            Require(view.GodotEnvironment.FogMode == Godot.Environment.FogModeEnum.Exponential,
                $"Expected FogMode Exponential, got {view.GodotEnvironment.FogMode}");
            Require(Mathf.IsEqualApprox(view.GodotEnvironment.FogDensity, overcast.Haze.Density),
                $"Expected FogDensity {overcast.Haze.Density}, got {view.GodotEnvironment.FogDensity}");
        }
        finally
        {
            view.QueueFree();
        }
    }

    private static void CheckPreviewSavedQuality(Node3D scene)
    {
        DisplaySettings originalConfig = SettingsPanel.LoadConfig();
        try
        {
            SettingsPanel.SaveConfig(originalConfig with { Quality = EnvironmentQuality.Low });

            // Case 1: Without CLI override, saved Low quality is applied
            UstyeMapPreview previewWithoutOverride = new([]);
            scene.AddChild(previewWithoutOverride);
            previewWithoutOverride.SetProcess(false);
            try
            {
                Require(previewWithoutOverride.EnvironmentView.ActiveQuality == EnvironmentQuality.Low,
                    $"UstyeMapPreview must apply saved Low quality when no CLI override is passed (got {previewWithoutOverride.EnvironmentView.ActiveQuality}).");
            }
            finally
            {
                previewWithoutOverride.QueueFree();
            }

            // Case 2: With CLI override --quality high, High quality is applied regardless of saved Low
            UstyeMapPreview previewWithOverride = new(["--quality", "high"]);
            scene.AddChild(previewWithOverride);
            previewWithOverride.SetProcess(false);
            try
            {
                Require(previewWithOverride.EnvironmentView.ActiveQuality == EnvironmentQuality.High,
                    $"UstyeMapPreview must apply CLI override High even when Low is saved (got {previewWithOverride.EnvironmentView.ActiveQuality}).");
            }
            finally
            {
                previewWithOverride.QueueFree();
            }
        }
        finally
        {
            SettingsPanel.SaveConfig(originalConfig);
        }
    }

    private static void CheckRoadFrontNormalsUpward(Node3D scene)
    {
        UstyePlanData plan = UstyePlanData.Load();
        UstyeMapView mapView = new(plan, includeScaleVehicles: false);
        scene.AddChild(mapView);
        try
        {
            Require(mapView.HasUpwardRoadFrontFaces(),
                "UstyeMapView roads must have upward-facing front faces.");

            Require(mapView.RoadsRoot.GetChildCount() > 0, "RoadsRoot must contain road meshes.");
            foreach (Node child in mapView.RoadsRoot.GetChildren())
            {
                if (child is MeshInstance3D mi && mi.Mesh is ArrayMesh mesh && mesh.GetSurfaceCount() > 0)
                {
                    using Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
                    Vector3[] verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    int[] idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                    Require(idx.Length >= 3, "Road mesh must contain triangles.");

                    for (int i = 0; i < idx.Length; i += 3)
                    {
                        Vector3 a = verts[idx[i]];
                        Vector3 b = verts[idx[i + 1]];
                        Vector3 c = verts[idx[i + 2]];
                        Vector3 clockwiseNormal = (c - a).Cross(b - a).Normalized();
                        float dotUp = clockwiseNormal.Dot(Vector3.Up);
                        Require(dotUp > 0.85f,
                            $"Road front face normal must point upward into the sky (got dotUp={dotUp:F3}).");
                    }
                }
            }
        }
        finally
        {
            mapView.QueueFree();
        }
    }

    private static void CheckRegressionFailurePropagation()
    {
        bool caught = false;
        try
        {
            Require(false, "Intentional failure to test propagation.");
        }
        catch (InvalidOperationException)
        {
            caught = true;
        }
        Require(caught, "Regression assertions must propagate InvalidOperationException.");
    }

    private static void CheckRenderBudgetValidation()
    {
        string raw = RenderBudget.ReadBudgetJson();
        RenderBudget loaded = RenderBudget.Parse(raw, "test_budget");
        Require(loaded.ShadowTiers.Close.ShadowEnabled, "Close tier must enable shadows.");
        Require(loaded.ShadowTiers.Close.TreeShadows, "Close tier must enable tree shadows.");
        Require(!loaded.ShadowTiers.Wide.TerrainShadows, "Wide tier must disable terrain shadows.");

        string noCloseEnabled = raw.Replace("\"shadow_enabled\": true,\n      \"shadow_splits\": 4,", "\"shadow_splits\": 4,");
        AssertValidationErrorBudget(noCloseEnabled, "shadow_tiers.close.shadow_enabled");

        string noCloseTrees = raw.Replace("\"tree_shadows\": true,", "");
        AssertValidationErrorBudget(noCloseTrees, "shadow_tiers.close.tree_shadows");

        string noWideTerrain = raw.Replace("\"terrain_shadows\": false", "");
        AssertValidationErrorBudget(noWideTerrain, "shadow_tiers.wide.terrain_shadows");
    }

    private static void AssertValidationErrorBudget(string json, string expectedField)
    {
        try
        {
            RenderBudget.Parse(json, "test_budget_validation");
            throw new InvalidOperationException($"Expected RenderBudget validation error for '{expectedField}', but parse succeeded.");
        }
        catch (EnvironmentProfileValidationException vex)
        {
            Require(vex.FieldName == expectedField || vex.Message.Contains(expectedField, StringComparison.OrdinalIgnoreCase),
                $"Expected RenderBudget field '{expectedField}', got '{vex.FieldName}': {vex.Message}");
        }
    }

    private static void CheckShadowZoomTiersAndFlatLayerShadows(Node3D scene)
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        RenderBudget budget = RenderBudget.LoadDefault();
        ShadowTiersSettings tiers = budget.ShadowTiers;

        // Pure hysteresis progression across Close (<=55), Play (55..185), Wide (>=185)
        ShadowZoomTier tier = EnvironmentView.SelectShadowZoomTier(35f, null, tiers);
        Require(tier == ShadowZoomTier.Close, "35 should start in Close tier.");
        Require(EnvironmentView.SelectShadowZoomTier(59f, tier, tiers) == ShadowZoomTier.Close,
            "59 within hysteresis must stay Close.");
        tier = EnvironmentView.SelectShadowZoomTier(61f, tier, tiers);
        Require(tier == ShadowZoomTier.Play, "61 past Close+hysteresis must switch to Play.");
        Require(EnvironmentView.SelectShadowZoomTier(51f, tier, tiers) == ShadowZoomTier.Play,
            "51 within hysteresis must stay Play.");
        Require(EnvironmentView.SelectShadowZoomTier(49f, tier, tiers) == ShadowZoomTier.Close,
            "49 below Close-hysteresis must return to Close.");
        Require(EnvironmentView.SelectShadowZoomTier(189f, ShadowZoomTier.Play, tiers) == ShadowZoomTier.Play,
            "189 within hysteresis must stay Play.");
        tier = EnvironmentView.SelectShadowZoomTier(191f, ShadowZoomTier.Play, tiers);
        Require(tier == ShadowZoomTier.Wide, "191 past PlayToWide+hysteresis must switch to Wide.");
        Require(EnvironmentView.SelectShadowZoomTier(181f, tier, tiers) == ShadowZoomTier.Wide,
            "181 within hysteresis must stay Wide.");
        Require(EnvironmentView.SelectShadowZoomTier(179f, tier, tiers) == ShadowZoomTier.Play,
            "179 below PlayToWide-hysteresis must return to Play.");

        UstyePlanData plan = UstyePlanData.Load();
        UstyeMapView mapView = new(plan, includeScaleVehicles: false);
        EnvironmentView envView = new(budget);
        scene.AddChild(mapView);
        scene.AddChild(envView);
        try
        {
            foreach (Node waterChild in mapView.WaterRoot.GetChildren())
            {
                if (waterChild is GeometryInstance3D gi)
                {
                    Require(gi.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                        $"Water '{waterChild.Name}' must have CastShadow=Off.");
                }
            }
            foreach (Node roadChild in mapView.RoadsRoot.GetChildren())
            {
                if (roadChild is MeshInstance3D roadMi && roadMi.Name.ToString().StartsWith("Road_", StringComparison.Ordinal))
                {
                    Require(roadMi.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                        $"Flat road '{roadMi.Name}' must have CastShadow=Off.");
                }
            }
            foreach (Node ctxChild in mapView.ContextRoot.GetChildren())
            {
                if (ctxChild is MeshInstance3D ctxMi && ctxMi.Name.ToString().StartsWith("Courtyard_", StringComparison.Ordinal))
                {
                    Require(ctxMi.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                        $"Flat pad '{ctxMi.Name}' must have CastShadow=Off.");
                }
            }

            envView.ApplyProfile(overcast, EnvironmentQuality.High);

            // Close zoom: full shadows + tree shadows ON + terrain shadows ON
            Require(envView.UpdateShadowTierForCamera(35f, mapView), "Initial Close tier must apply.");
            Require(envView.Sun.ShadowEnabled, "Close tier must enable shadows.");
            Require(envView.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits,
                "Close tier on High must use Parallel4Splits.");
            Require(mapView.VegetationShadowsEnabled, "Close tier must enable vegetation shadows.");
            Require(mapView.TerrainShadowsEnabled, "Close tier must enable terrain shadows.");
            foreach (MultiMeshInstance3D mmi in mapView.ConiferMultiMeshes)
            {
                Require(mmi.CastShadow == GeometryInstance3D.ShadowCastingSetting.On,
                    "Close tier must enable conifer CastShadow.");
            }

            // Play zoom: 2 splits + tree shadows OFF + terrain shadows ON
            Require(envView.UpdateShadowTierForCamera(70f, mapView), "Play tier must apply.");
            Require(envView.Sun.ShadowEnabled, "Play tier must keep shadows enabled.");
            Require(envView.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits,
                "Play tier must use Parallel2Splits.");
            Require(!mapView.VegetationShadowsEnabled, "Play tier must disable vegetation shadows.");
            Require(mapView.TerrainShadowsEnabled, "Play tier must keep terrain shadows enabled.");
            foreach (MultiMeshInstance3D mmi in mapView.DeciduousMultiMeshes)
            {
                Require(mmi.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                    "Play tier must disable deciduous CastShadow.");
            }

            // Steady state: no re-apply and zero allocations across 100 frames
            int countBefore = envView.ShadowTierApplyCount;
            _ = envView.UpdateShadowTierForCamera(70f, mapView);
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 100; f++)
            {
                bool changed = envView.UpdateShadowTierForCamera(70f + (f % 3), mapView);
                Require(!changed, "Steady-state jitter must not change shadow tier.");
                envView.UpdateShadowFade(0.016);
            }
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Require(envView.ShadowTierApplyCount == countBefore,
                $"Steady-state camera calls must not re-apply shadow settings (count {countBefore} -> {envView.ShadowTierApplyCount}).");
            Require(allocDelta == 0,
                $"UpdateShadowTierForCamera steady state must allocate 0 bytes, got {allocDelta}.");

            // Wide zoom transitions shadow opacity smoothly over 0.3s, then disables directional + terrain shadows
            Require(envView.UpdateShadowTierForCamera(220f, mapView), "Wide tier must apply.");
            envView.UpdateShadowFade(0.15);
            Require(envView.Sun.ShadowEnabled && envView.Sun.ShadowOpacity < 0.9f && envView.Sun.ShadowOpacity > 0.1f,
                $"Expected mid-fade shadow opacity around 0.5, got {envView.Sun.ShadowOpacity}.");
            envView.UpdateShadowFade(0.20);
            Require(!envView.Sun.ShadowEnabled, "Wide tier must disable directional shadows once fade finishes.");
            Require(!mapView.VegetationShadowsEnabled, "Wide tier must keep vegetation shadows disabled.");
            Require(!mapView.TerrainShadowsEnabled, "Wide tier must disable terrain shadows.");
        }
        finally
        {
            envView.QueueFree();
            mapView.QueueFree();
        }
    }

    private static void CheckShadowTargetLifecycleAcrossMapSwitch(Node3D scene)
    {
        EnvironmentProfile overcast = EnvironmentProfile.Load("overcast");
        UstyePlanData plan = UstyePlanData.Load();
        UstyeMapView ustye = new(plan, includeScaleVehicles: false);
        HeightMap battleMap = HeightMap.CreatePrototype();
        TerrainView terrain = TerrainView.Create(battleMap);
        EnvironmentView envView = new();

        scene.AddChild(ustye);
        scene.AddChild(envView);
        try
        {
            envView.ApplyProfile(overcast, EnvironmentQuality.High);
            envView.UpdateShadowTierForCamera(35f, ustye);
            Require(ustye.VegetationShadowsEnabled, "Ustye close zoom must enable tree shadows.");

            // Dispose/remove old Ustye and switch to TerrainView (first via explicit clear, and simulate freed old ref)
            scene.RemoveChild(ustye);
            ustye.QueueFree();
            envView.SetShadowTarget(null);
            scene.AddChild(terrain);
            envView.UpdateShadowTierForCamera(220f, terrain);
            Require(!terrain.TerrainShadowsEnabled, "Battle TerrainView wide zoom must disable terrain shadows.");

            // Zoom to close and mutate quality/profile repeatedly: must affect terrain without touching queued-free ustye
            envView.UpdateShadowTierForCamera(35f, terrain);
            envView.UpdateShadowFade(0.35);
            Require(terrain.TerrainShadowsEnabled, "Battle TerrainView close zoom must enable terrain shadows.");
            envView.SetQuality(EnvironmentQuality.Medium);
            envView.SetProfile(EnvironmentProfile.Load("clear_afternoon"));
            Require(envView.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits,
                "Quality switch to Medium after map switch must clamp shadow splits to 2.");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(terrain)) terrain.QueueFree();
            envView.QueueFree();
        }
    }

    private static void CheckRenderScaleViewportMapping(Node3D scene)
    {
        Viewport? vp = scene.GetViewport();
        Require(vp is not null, "Scene must have a Viewport.");
        float origScale = vp!.Scaling3DScale;
        Viewport.Scaling3DModeEnum origMode = vp.Scaling3DMode;
        Viewport.Msaa origMsaa = vp.Msaa3D;
        try
        {
            // Verify Native100 is default when DisplaySettings has default or omitted render_scale
            DisplaySettings defaults = DisplaySettings.CreateDefault();
            Require(defaults.RenderScale == RenderScalePreset.Native100, "Default RenderScale must be Native100.");

            (float s6880, int aa6880) = SettingsPanel.ApplyRenderSettingsToViewport(
                vp, new DisplaySettings(RenderScale: RenderScalePreset.Auto, AntiAliasing: AntiAliasingPreset.Auto), new Vector2I(6880, 2880));
            Require(Mathf.IsEqualApprox(s6880, 0.50f), $"6880x2880 Auto scale expected 0.50, got {s6880}");
            Require(aa6880 == 0 && vp.Msaa3D == Viewport.Msaa.Disabled, "6880x2880 Auto AA expected Disabled.");
            Require(vp.Scaling3DMode == Viewport.Scaling3DModeEnum.Fsr, "0.50 scale must use FSR.");

            (float s2560, int aa2560) = SettingsPanel.ApplyRenderSettingsToViewport(
                vp, new DisplaySettings(RenderScale: RenderScalePreset.Auto, AntiAliasing: AntiAliasingPreset.Auto), new Vector2I(2560, 1600));
            Require(Mathf.Abs(s2560 - 0.9487f) < 0.01f, $"2560x1600 Auto scale expected ~0.95, got {s2560}");
            Require(aa2560 == 2 && vp.Msaa3D == Viewport.Msaa.Msaa2X, "2560x1600 Auto AA expected 2x.");

            (float s1440, int aa1440) = SettingsPanel.ApplyRenderSettingsToViewport(
                vp, new DisplaySettings(RenderScale: RenderScalePreset.Auto, AntiAliasing: AntiAliasingPreset.Auto), new Vector2I(1440, 900));
            Require(Mathf.IsEqualApprox(s1440, 1.0f), $"1440x900 Auto scale expected 1.0, got {s1440}");
            Require(aa1440 == 2 && vp.Msaa3D == Viewport.Msaa.Msaa2X, "1440x900 Auto AA expected 2x.");
            Require(vp.Scaling3DMode == Viewport.Scaling3DModeEnum.Bilinear, "1.0 scale must use Bilinear.");

            (float s75, int aa4) = SettingsPanel.ApplyRenderSettingsToViewport(
                vp, new DisplaySettings(RenderScale: RenderScalePreset.Scale75, AntiAliasing: AntiAliasingPreset.Msaa4x), new Vector2I(1440, 900));
            Require(Mathf.IsEqualApprox(s75, 0.75f), $"75% preset expected 0.75, got {s75}");
            Require(aa4 == 4 && vp.Msaa3D == Viewport.Msaa.Msaa4X, "4x preset expected Msaa4X.");
        }
        finally
        {
            vp.Scaling3DScale = origScale;
            vp.Scaling3DMode = origMode;
            vp.Msaa3D = origMsaa;
        }
    }

    private static void CheckChunkedTerrainSharedEdgesAndLod(Node3D scene)
    {
        // 1. Non-divisible rectangular map with steep ridges and narrow hollow
        const int cols = 146;
        const int rows = 94;
        ushort[] heights = new ushort[cols * rows];
        for (int z = 0; z < rows; z++)
        {
            for (int x = 0; x < cols; x++)
            {
                float h = 12f + 10f * MathF.Sin(x * 0.08f) * MathF.Cos(z * 0.07f);
                if (x >= 40 && x <= 60 && z >= 30 && z <= 50)
                {
                    // Steep hollow to verify shadow proxy lowers beneath valley floor
                    h = 2f;
                }
                heights[z * cols + x] = (ushort)MathF.Round(MathF.Max(0f, h) * 100f);
            }
        }
        HeightMap rectMap = new(cols, rows, 2f, 0.01f, heights);
        TerrainView rectTerrain = TerrainView.Create(rectMap);
        scene.AddChild(rectTerrain);
        try
        {
            Require(rectTerrain.ChunkCols == 2 && rectTerrain.ChunkRows == 1,
                $"Expected 2x1 chunks for 145x93 cells, got {rectTerrain.ChunkCols}x{rectTerrain.ChunkRows}.");
            Require(rectTerrain.HasUpwardFrontFaces(), "Rectangular terrain chunks must have upward front faces.");

            // Shared edge positions & normals must match bit-for-bit at every LOD
            TerrainView.TerrainChunk left = rectTerrain.Chunks[0];
            TerrainView.TerrainChunk right = rectTerrain.Chunks[1];
            for (int li = 0; li < rectTerrain.LodStrides.Count; li++)
            {
                int stride = rectTerrain.LodStrides[li];
                int[] lxs = TerrainView.BuildSampleIndices(left.StartCellX, left.EndCellX, stride);
                int[] lzs = TerrainView.BuildSampleIndices(left.StartCellZ, left.EndCellZ, stride);
                int[] rxs = TerrainView.BuildSampleIndices(right.StartCellX, right.EndCellX, stride);
                int[] rzs = TerrainView.BuildSampleIndices(right.StartCellZ, right.EndCellZ, stride);
                Require(lzs.Length == rzs.Length, "Shared Z sample count must match between adjacent chunks.");

                using Godot.Collections.Array la = left.LodMeshes[li].SurfaceGetArrays(0);
                using Godot.Collections.Array ra = right.LodMeshes[li].SurfaceGetArrays(0);
                Vector3[] lv = la[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                Vector3[] rv = ra[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                Vector3[] ln = la[(int)Mesh.ArrayType.Normal].AsVector3Array();
                Vector3[] rn = ra[(int)Mesh.ArrayType.Normal].AsVector3Array();

                for (int iz = 0; iz < lzs.Length; iz++)
                {
                    int liIdx = iz * lxs.Length + (lxs.Length - 1);
                    int riIdx = iz * rxs.Length;
                    Require(lv[liIdx].IsEqualApprox(rv[riIdx]),
                        $"Shared edge vertex mismatch at LOD {li}, row {iz}: {lv[liIdx]} vs {rv[riIdx]}.");
                    Require(ln[liIdx].IsEqualApprox(rn[riIdx]),
                        $"Shared edge normal mismatch at LOD {li}, row {iz}: {ln[liIdx]} vs {rn[riIdx]}.");
                }
            }

            // Visible chunks must not cast shadows; shadow proxies stay ShadowsOnly and toggle via Visible
            foreach (TerrainView.TerrainChunk chunk in rectTerrain.Chunks)
            {
                Require(chunk.VisibleInstance.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                    "Visible terrain chunk must have CastShadow=Off.");
                Require(chunk.ShadowProxyInstance is not null &&
                        chunk.ShadowProxyInstance.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly &&
                        chunk.ShadowProxyInstance.Visible,
                    "Shadow proxy chunk must use ShadowsOnly and Visible=true when enabled.");
            }
            rectTerrain.SetTerrainShadowsEnabled(false);
            foreach (TerrainView.TerrainChunk chunk in rectTerrain.Chunks)
            {
                Require(chunk.ShadowProxyInstance!.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly &&
                        !chunk.ShadowProxyInstance.Visible,
                    "Disabling terrain shadows must keep CastShadow=ShadowsOnly and hide proxy via Visible=false.");
            }
            rectTerrain.SetTerrainShadowsEnabled(true);
            foreach (TerrainView.TerrainChunk chunk in rectTerrain.Chunks)
            {
                Require(chunk.ShadowProxyInstance!.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly &&
                        chunk.ShadowProxyInstance.Visible,
                    "Re-enabling terrain shadows must keep CastShadow=ShadowsOnly and set Visible=true.");
            }
        }
        finally
        {
            scene.RemoveChild(rectTerrain);
            rectTerrain.QueueFree();
        }

        // 2. Diagonal edge-crossing 4m error counterexample (34x33 map, terminal odd column)
        const int ceCols = 34;
        const int ceRows = 33;
        ushort[] ceSamples = new ushort[ceCols * ceRows];
        ceSamples[1 * ceCols + 33] = 800;
        ceSamples[2 * ceCols + 33] = 1600;
        ceSamples[3 * ceCols + 33] = 800;
        HeightMap counterExampleMap = new(ceCols, ceRows, 1f, 0.01f, ceSamples);
        int[] ceXs = TerrainView.BuildSampleIndices(0, ceCols - 1, 2);
        int[] ceZs = TerrainView.BuildSampleIndices(0, ceRows - 1, 2);
        float ceErr = TerrainView.ComputeExactMaxHeightError(counterExampleMap, ceXs, ceZs);
        Require(ceErr >= 3.99f, $"Expected >= 4.0m diagonal edge-crossing error, got {ceErr:F3}m.");
        int pickedCeLod = TerrainView.SelectOrthoLodIndex(
            metersPerPixel: 70f / 800f,
            baseCellSizeMeters: 1f,
            strides: [1, 2],
            maxHeightErrors: [0f, ceErr],
            previousLodIndex: 0,
            previousMetersPerPixel: -1f);
        Require(pickedCeLod == 0, $"Selector must reject stride 2 with {ceErr:F3}m error, picked LOD {pickedCeLod}.");

        // 3. Stride-8 shadow proxy compensation with hollow outside first chunk (201x201, chunkCells=100)
        const int spCols = 201;
        const int spRows = 201;
        ushort[] spSamples = new ushort[spCols * spRows];
        Array.Fill(spSamples, (ushort)1000);
        spSamples[111 * spCols + 111] = 0;
        HeightMap spMap = new(spCols, spRows, 1f, 0.01f, spSamples);
        TerrainView spTerrain = TerrainView.Create(spMap, chunkCells: 100, shadowStride: 8);
        scene.AddChild(spTerrain);
        try
        {
            Require(spTerrain.ChunkCols == 2 && spTerrain.ChunkRows == 2,
                $"Expected 2x2 chunks for 201x201 map, got {spTerrain.ChunkCols}x{spTerrain.ChunkRows}.");
            TerrainView.TerrainChunk chunk11 = spTerrain.Chunks[3];
            int[] pxs = TerrainView.BuildSampleIndices(chunk11.StartCellX, chunk11.EndCellX, 8);
            int[] pzs = TerrainView.BuildSampleIndices(chunk11.StartCellZ, chunk11.EndCellZ, 8);
            using Godot.Collections.Array pa = chunk11.ShadowProxyInstance!.Mesh.SurfaceGetArrays(0);
            Vector3[] pv = pa[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            float hollowProxyY = TerrainView.EvaluateSurfaceHeightAtWorld(spMap, pxs, pzs, pv, 111f, 111f);
            Require(hollowProxyY <= 0.001f,
                $"Stride-8 shadow proxy must drop below 0m hollow at (111,111), got {hollowProxyY:F3}m.");

            // Shared proxy boundary between chunk (0,1) and (1,1) must match bit-for-bit
            TerrainView.TerrainChunk chunk01 = spTerrain.Chunks[2];
            int[] pxs01 = TerrainView.BuildSampleIndices(chunk01.StartCellX, chunk01.EndCellX, 8);
            using Godot.Collections.Array pa01 = chunk01.ShadowProxyInstance!.Mesh.SurfaceGetArrays(0);
            Vector3[] pv01 = pa01[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            for (int iz = 0; iz < pzs.Length; iz++)
            {
                Vector3 leftSeam = pv01[iz * pxs01.Length + (pxs01.Length - 1)];
                Vector3 rightSeam = pv[iz * pxs.Length];
                Require(leftSeam.IsEqualApprox(rightSeam),
                    $"Shadow proxy shared boundary mismatch at row {iz}: {leftSeam} vs {rightSeam}.");
            }
        }
        finally
        {
            scene.RemoveChild(spTerrain);
            spTerrain.QueueFree();
        }

        // 4. Synthetic rough map + grid never below active surface + 2x pixel height invariant
        const int rCols = 201;
        const int rRows = 201;
        ushort[] roughSamples = new ushort[rCols * rRows];
        for (int z = 0; z < rRows; z++)
        {
            for (int x = 0; x < rCols; x++)
            {
                float h = 15f + 6f * MathF.Sin(x * 0.25f) + 5f * MathF.Cos(z * 0.20f)
                    + (x == 103 && z == 103 ? -12f : 0f);
                roughSamples[z * rCols + x] = (ushort)MathF.Round(MathF.Max(0f, h) * 100f);
            }
        }
        HeightMap roughMap = new(rCols, rRows, 2f, 0.01f, roughSamples);
        TerrainView roughTerrain = TerrainView.Create(roughMap);
        MeshInstance3D roughGrid = TerrainView.CreateGrid(roughMap);
        roughTerrain.AttachGrid(roughGrid);
        scene.AddChild(roughTerrain);
        scene.AddChild(roughGrid);
        try
        {
            // Verify MaxCellScreenPixels=20 and error caps hold at every zoom
            foreach (float camSize in new[] { 35f, 160f, 360f, 900f, 2200f })
            {
                float mpp = camSize / 900f;
                int picked = TerrainView.SelectOrthoLodIndex(
                    mpp,
                    roughMap.CellSize,
                    roughTerrain.LodStrides.ToArray(),
                    roughTerrain.GlobalMaxHeightErrors.ToArray(),
                    0,
                    previousMetersPerPixel: -1f);
                float cellScreenPx = (roughTerrain.LodStrides[picked] * roughMap.CellSize) / mpp;
                if (picked > 0)
                {
                    Require(cellScreenPx <= TerrainView.MaxCellScreenPixels + 0.01f,
                        $"Selected coarse LOD {picked} has cell width {cellScreenPx:F2}px > {TerrainView.MaxCellScreenPixels}px.");
                }
                Require(roughTerrain.GlobalMaxHeightErrors[picked] <= TerrainView.MaxWorldHeightErrorMeters,
                    $"Selected LOD {picked} exceeds max world error.");
                Require(roughTerrain.GlobalMaxHeightErrors[picked] / mpp <= TerrainView.MaxScreenHeightErrorPixels + 0.01f,
                    $"Selected LOD {picked} exceeds max screen height error.");
            }
            // At cameraSize=160 (mpp=160/900=0.1778), stride=2 (4m) is 22.5px > 20px, so no coarse LOD may be picked
            Require(TerrainView.SelectOrthoLodIndex(
                160f / 900f, roughMap.CellSize, roughTerrain.LodStrides.ToArray(), roughTerrain.GlobalMaxHeightErrors.ToArray(), 0, -1f) == 0,
                "Must keep LOD 0 while stride 2 cell exceeds MaxCellScreenPixels.");

            // 2x window height must pick finer or equal LOD at same cameraSize
            roughTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 500f, 900f);
            int strideAt900 = roughTerrain.ActiveGlobalStride;
            roughTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 500f, 1800f);
            int strideAt1800 = roughTerrain.ActiveGlobalStride;
            Require(strideAt1800 <= strideAt900,
                $"2x viewport height must pick finer or equal LOD: 900px stride={strideAt900}, 1800px stride={strideAt1800}.");

            // Effective pixel height multiplies SubViewport height by Scaling3DScale
            SubViewport sv = new() { Size = new Vector2I(1280, 900), Scaling3DScale = 0.5f };
            try
            {
                float halfPx = TerrainView.GetEffectiveViewportPixelHeight(sv);
                Require(Mathf.IsEqualApprox(halfPx, 450f), $"Expected 450px at 0.5 scale, got {halfPx}.");
            }
            finally
            {
                sv.Free();
            }

            // Verify grid lines never sit below active terrain surface across zoom levels
            foreach (float camSize in new[] { 35f, 500f, 1800f })
            {
                roughTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, camSize, 900f);
                VerifyGridAboveActiveTerrain(roughMap, roughTerrain, roughGrid);
            }
        }
        finally
        {
            scene.RemoveChild(roughGrid);
            scene.RemoveChild(roughTerrain);
            roughGrid.QueueFree();
            roughTerrain.QueueFree();
        }

        // 5. Battle map LOD selection, ortho<->perspective round-trip, grid offset, and 0 steady-state allocs
        HeightMap battleMap = BattleScenario.CreateMap();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long liveBefore = GC.GetTotalMemory(false);
        long allocBeforeBuild = GC.GetTotalAllocatedBytes(precise: true);
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        TerrainView battleTerrain = TerrainView.Create(battleMap);
        swBuild.Stop();
        long allocAfterBuild = GC.GetTotalAllocatedBytes(precise: true);
        long retainedMemBytes = Math.Max(0L, GC.GetTotalMemory(true) - liveBefore);
        long totalAllocatedBytes = Math.Max(0L, allocAfterBuild - allocBeforeBuild);
        MeshInstance3D battleGrid = TerrainView.CreateGrid(battleMap);
        battleTerrain.AttachGrid(battleGrid);
        scene.AddChild(battleTerrain);
        scene.AddChild(battleGrid);
        try
        {
            Require(battleTerrain.Chunks.Count == 100, $"Expected 10x10 chunks on battle map, got {battleTerrain.Chunks.Count}.");

            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 35f, 900f);
            Require(battleTerrain.ActiveGlobalStride == 1, $"Close zoom 35 must pick stride 1, got {battleTerrain.ActiveGlobalStride}.");

            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 360f, 900f);
            Require(battleTerrain.ActiveGlobalStride >= 2, $"Zoom 360@900px should coarsen stride >= 2, got {battleTerrain.ActiveGlobalStride}.");

            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 700f, 900f);
            int wideStride = battleTerrain.ActiveGlobalStride;
            Require(wideStride >= 5, $"Zoom 700@900px should coarsen stride >= 5, got {wideStride}.");
            VerifyGridAboveActiveTerrain(battleMap, battleTerrain, battleGrid);

            // Perspective forces LOD0 across all chunks with zero allocations
            long perspAllocBefore = GC.GetAllocatedBytesForCurrentThread();
            battleTerrain.UpdateLod(Camera3D.ProjectionType.Perspective, 700f, 900f);
            long perspAllocDelta = GC.GetAllocatedBytesForCurrentThread() - perspAllocBefore;
            Require(perspAllocDelta == 0, $"Switching to perspective must allocate 0 bytes, got {perspAllocDelta}.");
            Require(battleTerrain.ActiveGlobalLodIndex == 0, "Perspective mode must use LOD 0.");
            foreach (TerrainView.TerrainChunk chunk in battleTerrain.Chunks)
            {
                Require(chunk.CurrentLodIndex == 0 &&
                        ReferenceEquals(chunk.VisibleInstance.Mesh, chunk.LodMeshes[0]),
                    "Every chunk in perspective mode must use LodMeshes[0].");
            }

            // Return to ortho at same zoom must synchronize all chunks back to coarse LOD with matching edges
            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 700f, 900f);
            Require(battleTerrain.ActiveGlobalStride == wideStride,
                $"Return from perspective must restore stride {wideStride}, got {battleTerrain.ActiveGlobalStride}.");
            foreach (TerrainView.TerrainChunk chunk in battleTerrain.Chunks)
            {
                Require(chunk.CurrentLodIndex == battleTerrain.ActiveGlobalLodIndex &&
                        ReferenceEquals(chunk.VisibleInstance.Mesh, chunk.LodMeshes[battleTerrain.ActiveGlobalLodIndex]),
                    "Return from perspective must synchronize every chunk to ActiveGlobalLodIndex.");
            }

            // Runtime transition timing across all LODs
            var swSwitch = System.Diagnostics.Stopwatch.StartNew();
            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 35f, 900f);
            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 360f, 900f);
            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 900f, 900f);
            battleTerrain.UpdateLod(Camera3D.ProjectionType.Orthogonal, 2000f, 900f);
            swSwitch.Stop();

            // Steady-state zero allocations across 120 frames covering full Main frame path (Camera3D + cached height)
            SubViewport cachedSv = new() { Size = new Vector2I(1280, 900), Scaling3DScale = 1.0f };
            Camera3D testCam = new() { Projection = Camera3D.ProjectionType.Orthogonal, Size = 2000f };
            try
            {
                float cachedHeightPx = TerrainView.GetEffectiveViewportPixelHeight(cachedSv);
                Require(Mathf.IsEqualApprox(cachedHeightPx, 900f), $"Expected initial cached height 900, got {cachedHeightPx}.");

                // Warm once before measuring allocations so JIT/branch state is settled
                Main.UpdateTerrainLodStep(battleTerrain, testCam, cachedHeightPx);

                int switchesBefore = battleTerrain.LodSwitchCount;
                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int f = 0; f < 120; f++)
                {
                    testCam.Size = 2000f + (f % 2);
                    Main.UpdateTerrainLodStep(battleTerrain, testCam, cachedHeightPx);
                }
                long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                Require(battleTerrain.LodSwitchCount == switchesBefore, "Steady-state jitter must not switch terrain LOD.");
                Require(allocDelta == 0, $"Main terrain LOD frame step must allocate 0 bytes, got {allocDelta}.");

                // Resize / Scaling3DScale event updates cached pixel height when queried outside the frame loop
                cachedSv.Size = new Vector2I(1280, 1800);
                cachedSv.Scaling3DScale = 0.5f;
                cachedHeightPx = TerrainView.GetEffectiveViewportPixelHeight(cachedSv);
                Require(Mathf.IsEqualApprox(cachedHeightPx, 900f), $"Expected 1800*0.5=900 after resize+scale, got {cachedHeightPx}.");
            }
            finally
            {
                testCam.Free();
                cachedSv.Free();
            }

            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"TERRAIN_CHUNKED_PERF chunks={battleTerrain.Chunks.Count} build_ms={swBuild.Elapsed.TotalMilliseconds:F1} allocated_kb={totalAllocatedBytes / 1024} retained_kb={retainedMemBytes / 1024} switch4_ms={swSwitch.Elapsed.TotalMilliseconds:F3} max_errors=[{string.Join(",", battleTerrain.GlobalMaxHeightErrors.Select(e => $"{e:F3}"))}]"));
        }
        finally
        {
            scene.RemoveChild(battleGrid);
            scene.RemoveChild(battleTerrain);
            battleGrid.QueueFree();
            battleTerrain.QueueFree();
        }

        // 6. Repeated create & dispose lifecycle check
        for (int iter = 0; iter < 2; iter++)
        {
            TerrainView copy = TerrainView.Create(battleMap);
            scene.AddChild(copy);
            copy.UpdateLod(Camera3D.ProjectionType.Orthogonal, 360f, 900f);
            scene.RemoveChild(copy);
            copy.Free();
        }
    }

    private static void VerifyGridAboveActiveTerrain(HeightMap map, TerrainView terrain, MeshInstance3D grid)
    {
        int lod = terrain.ActiveGlobalLodIndex;
        int stride = terrain.LodStrides[lod];
        float liftY = grid.Position.Y;
        using Godot.Collections.Array ga = grid.Mesh.SurfaceGetArrays(0);
        Vector3[] gv = ga[(int)Mesh.ArrayType.Vertex].AsVector3Array();

        // Cache active chunk sample arrays and vertex arrays for fast lookup
        TerrainView.TerrainChunk[] chunks = terrain.Chunks.ToArray();
        Vector3[][] chunkVerts = new Vector3[chunks.Length][];
        int[][] chunkXs = new int[chunks.Length][];
        int[][] chunkZs = new int[chunks.Length][];
        for (int c = 0; c < chunks.Length; c++)
        {
            using Godot.Collections.Array ca = chunks[c].VisibleInstance.Mesh.SurfaceGetArrays(0);
            chunkVerts[c] = ca[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            chunkXs[c] = TerrainView.BuildSampleIndices(chunks[c].StartCellX, chunks[c].EndCellX, stride);
            chunkZs[c] = TerrainView.BuildSampleIndices(chunks[c].StartCellZ, chunks[c].EndCellZ, stride);
        }

        int step = Math.Max(1, gv.Length / 1500);
        for (int i = 0; i + 1 < gv.Length; i += 2 * step)
        {
            Vector3 p0 = gv[i];
            Vector3 p1 = gv[i + 1];
            Vector3 mid = (p0 + p1) * 0.5f;
            CheckPointAbove(p0.X, p0.Z, p0.Y + liftY);
            CheckPointAbove(mid.X, mid.Z, mid.Y + liftY);
        }

        void CheckPointAbove(float wx, float wz, float gridWorldY)
        {
            float gx = Math.Clamp(wx / map.CellSize, 0f, map.Columns - 1);
            float gz = Math.Clamp(wz / map.CellSize, 0f, map.Rows - 1);
            int chunkIdx = -1;
            for (int c = 0; c < chunks.Length; c++)
            {
                if (gx >= chunks[c].StartCellX && gx <= chunks[c].EndCellX &&
                    gz >= chunks[c].StartCellZ && gz <= chunks[c].EndCellZ)
                {
                    chunkIdx = c;
                    break;
                }
            }
            if (chunkIdx < 0) return;
            float activeTerrainY = TerrainView.EvaluateSurfaceHeightAtWorld(
                map, chunkXs[chunkIdx], chunkZs[chunkIdx], chunkVerts[chunkIdx], wx, wz);
            Require(gridWorldY >= activeTerrainY + 0.05f,
                $"Grid point ({wx:F1},{wz:F1}) height {gridWorldY:F3} fell below active LOD {lod} surface {activeTerrainY:F3}.");
        }
    }
}
