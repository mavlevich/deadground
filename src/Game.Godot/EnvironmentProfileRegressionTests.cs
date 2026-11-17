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
        CheckRegressionFailurePropagation();

        GD.Print("ENVIRONMENT_PROFILE_REGRESSIONS_OK 16/16 passed: load_profiles validation required_fields apply_light runtime_switching haze_scaling haze_mode hashes_unaffected zero_allocations quality_levels preview_saved_quality road_normals render_budget shadow_zoom_tiers map_switch_lifecycle render_scale_msaa");
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
        MeshInstance3D terrainMesh = TerrainView.Create(battleMap);
        TerrainShadowTarget terrainTarget = new(terrainMesh);
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
            scene.AddChild(terrainMesh);
            envView.UpdateShadowTierForCamera(220f, terrainTarget);
            Require(!terrainTarget.TerrainShadowsEnabled, "Battle TerrainView wide zoom must disable terrain shadows.");

            // Zoom to close and mutate quality/profile repeatedly: must affect terrainTarget without touching queued-free ustye
            envView.UpdateShadowTierForCamera(35f, terrainTarget);
            envView.UpdateShadowFade(0.35);
            Require(terrainTarget.TerrainShadowsEnabled, "Battle TerrainView close zoom must enable terrain shadows.");
            envView.SetQuality(EnvironmentQuality.Medium);
            envView.SetProfile(EnvironmentProfile.Load("clear_afternoon"));
            Require(envView.Sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits,
                "Quality switch to Medium after map switch must clamp shadow splits to 2.");
        }
        finally
        {
            if (GodotObject.IsInstanceValid(terrainMesh)) terrainMesh.QueueFree();
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
}
