using System.Globalization;

namespace DeadGround.Sandbox;

/// <summary>
/// Frame timing analysis and percentile metrics report.
/// Preallocates buffers to avoid allocations during active frame timing.
/// </summary>
public sealed class FramePacingMetrics
{
    public readonly record struct FramePhaseSample(
        float SessionMs = 0f,
        float FogOfWarMs = 0f,
        float ViewsMs = 0f,
        float OrderPreviewMs = 0f,
        float MinimapMs = 0f,
        float EffectsAndHudMs = 0f,
        int RenderDrawCalls = -1,
        int RenderPrimitives = -1,
        int ActiveSceneNodes = -1,
        int LodSwitches = 0,
        int VisibleDrawCalls = -1,
        int VisiblePrimitives = -1,
        int ShadowDrawCalls = -1,
        int ShadowPrimitives = -1);

    public readonly record struct PhaseSummary(
        bool HasPhaseData,
        float MeanSessionMs,
        float MaxSessionMs,
        float MeanFogOfWarMs,
        float MaxFogOfWarMs,
        float MeanViewsMs,
        float MaxViewsMs,
        float MeanOrderPreviewMs,
        float MaxOrderPreviewMs,
        float MeanMinimapMs,
        float MaxMinimapMs,
        float MeanEffectsAndHudMs,
        float MaxEffectsAndHudMs,
        bool HasRenderCounters,
        float MeanDrawCalls,
        int MaxDrawCalls,
        float MeanPrimitives,
        int MaxPrimitives,
        int LastActiveSceneNodes,
        int TotalLodSwitches,
        bool HasPassCounters = false,
        float MeanVisibleDrawCalls = 0f,
        int MaxVisibleDrawCalls = 0,
        float MeanVisiblePrimitives = 0f,
        int MaxVisiblePrimitives = 0,
        float MeanShadowDrawCalls = 0f,
        int MaxShadowDrawCalls = 0,
        float MeanShadowPrimitives = 0f,
        int MaxShadowPrimitives = 0);

    private struct CounterAccumulator
    {
        private long _sum;
        private int _count;
        public int Max { get; private set; }
        public readonly bool HasData => _count > 0;
        public readonly float Mean => _count > 0 ? (float)_sum / _count : 0f;

        public void Add(int value)
        {
            if (value < 0) return;
            _sum += value;
            _count++;
            if (value > Max) Max = value;
        }
    }

    private readonly float[] _frameIntervalsMs;
    private readonly float[] _cpuTimesMs;
    private readonly float[] _gpuTimesMs;
    private readonly FramePhaseSample[] _phaseSamples;
    private readonly bool[] _hasPhaseAt;
    private int _phaseSampleCount;
    private int _count;

    public int Capacity => _frameIntervalsMs.Length;
    public int Count => _count;

    public FramePacingMetrics(int capacity = 2000)
    {
        if (capacity < 16) capacity = 16;
        _frameIntervalsMs = new float[capacity];
        _cpuTimesMs = new float[capacity];
        _gpuTimesMs = new float[capacity];
        _phaseSamples = new FramePhaseSample[capacity];
        _hasPhaseAt = new bool[capacity];
    }

    public void Record(float frameIntervalMs, float cpuTimeMs = 0f, float gpuTimeMs = -1f)
    {
        if (_count >= _frameIntervalsMs.Length) return;
        _frameIntervalsMs[_count] = frameIntervalMs;
        _cpuTimesMs[_count] = cpuTimeMs;
        _gpuTimesMs[_count] = gpuTimeMs;
        _phaseSamples[_count] = default;
        _hasPhaseAt[_count] = false;
        _count++;
    }

    public void Record(float frameIntervalMs, float cpuTimeMs, float gpuTimeMs, in FramePhaseSample phases)
    {
        if (_count >= _frameIntervalsMs.Length) return;
        _frameIntervalsMs[_count] = frameIntervalMs;
        _cpuTimesMs[_count] = cpuTimeMs;
        _gpuTimesMs[_count] = gpuTimeMs;
        _phaseSamples[_count] = phases;
        _hasPhaseAt[_count] = true;
        _phaseSampleCount++;
        _count++;
    }

    public void Reset()
    {
        _count = 0;
        _phaseSampleCount = 0;
    }

    public readonly record struct PercentileResults(
        int SampleCount,
        float P50Ms,
        float P95Ms,
        float P99Ms,
        float MaxMs,
        int LongFramesOver16Ms,
        int LongFramesOver33Ms,
        int LongFramesOver8Ms,
        float MeanCpuMs,
        float MeanGpuMs,
        bool HasGpuTiming,
        float GpuP50Ms = 0f,
        float GpuP95Ms = 0f);

    public PercentileResults CalculateResults()
    {
        if (_count == 0)
        {
            return new PercentileResults(0, 0f, 0f, 0f, 0f, 0, 0, 0, 0f, 0f, false);
        }

        // Copy frame intervals in milliseconds for sorting
        float[] sortedMs = new float[_count];
        float sumCpu = 0f;
        float sumGpu = 0f;
        int gpuCount = 0;
        int over16 = 0;
        int over33 = 0;
        int over8 = 0;

        for (int i = 0; i < _count; i++)
        {
            float ms = _frameIntervalsMs[i];
            sortedMs[i] = ms;
            sumCpu += _cpuTimesMs[i];
            if (_gpuTimesMs[i] >= 0f)
            {
                sumGpu += _gpuTimesMs[i];
                gpuCount++;
            }

            if (ms > 8.333f) over8++;
            if (ms > 16.667f) over16++;
            if (ms > 33.333f) over33++;
        }

        Array.Sort(sortedMs);

        float p50 = GetPercentile(sortedMs, 0.50f);
        float p95 = GetPercentile(sortedMs, 0.95f);
        float p99 = GetPercentile(sortedMs, 0.99f);
        float max = sortedMs[^1];
        bool hasGpu = gpuCount > 0;
        float gpuP50 = 0f;
        float gpuP95 = 0f;
        if (hasGpu)
        {
            float[] sortedGpu = new float[gpuCount];
            int gi = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_gpuTimesMs[i] >= 0f)
                {
                    sortedGpu[gi++] = _gpuTimesMs[i];
                }
            }
            Array.Sort(sortedGpu);
            gpuP50 = GetPercentile(sortedGpu, 0.50f);
            gpuP95 = GetPercentile(sortedGpu, 0.95f);
        }

        return new PercentileResults(
            _count,
            p50,
            p95,
            p99,
            max,
            over16,
            over33,
            over8,
            sumCpu / _count,
            hasGpu ? sumGpu / gpuCount : 0f,
            hasGpu,
            gpuP50,
            gpuP95);
    }

    public PhaseSummary CalculatePhaseSummary()
    {
        if (_count == 0 || _phaseSampleCount == 0)
        {
            return default;
        }

        float sumSession = 0f, maxSession = 0f;
        float sumFog = 0f, maxFog = 0f;
        float sumViews = 0f, maxViews = 0f;
        float sumPreview = 0f, maxPreview = 0f;
        float sumMinimap = 0f, maxMinimap = 0f;
        float sumEffectsHud = 0f, maxEffectsHud = 0f;

        CounterAccumulator drawCalls = default;
        CounterAccumulator primitives = default;
        CounterAccumulator visibleDrawCalls = default;
        CounterAccumulator visiblePrimitives = default;
        CounterAccumulator shadowDrawCalls = default;
        CounterAccumulator shadowPrimitives = default;

        int lastActiveNodes = -1;
        int totalLodSwitches = 0;

        for (int i = 0; i < _count; i++)
        {
            if (!_hasPhaseAt[i]) continue;
            ref readonly FramePhaseSample s = ref _phaseSamples[i];
            sumSession += s.SessionMs;
            if (s.SessionMs > maxSession) maxSession = s.SessionMs;

            sumFog += s.FogOfWarMs;
            if (s.FogOfWarMs > maxFog) maxFog = s.FogOfWarMs;

            sumViews += s.ViewsMs;
            if (s.ViewsMs > maxViews) maxViews = s.ViewsMs;

            sumPreview += s.OrderPreviewMs;
            if (s.OrderPreviewMs > maxPreview) maxPreview = s.OrderPreviewMs;

            sumMinimap += s.MinimapMs;
            if (s.MinimapMs > maxMinimap) maxMinimap = s.MinimapMs;

            sumEffectsHud += s.EffectsAndHudMs;
            if (s.EffectsAndHudMs > maxEffectsHud) maxEffectsHud = s.EffectsAndHudMs;

            drawCalls.Add(s.RenderDrawCalls);
            primitives.Add(s.RenderPrimitives);
            visibleDrawCalls.Add(s.VisibleDrawCalls);
            visiblePrimitives.Add(s.VisiblePrimitives);
            shadowDrawCalls.Add(s.ShadowDrawCalls);
            shadowPrimitives.Add(s.ShadowPrimitives);
            if (s.ActiveSceneNodes >= 0)
            {
                lastActiveNodes = s.ActiveSceneNodes;
            }
            totalLodSwitches += s.LodSwitches;
        }

        float invPhaseCount = 1f / _phaseSampleCount;
        bool hasCounters = drawCalls.HasData || primitives.HasData || lastActiveNodes >= 0;
        bool hasPassCounters = visibleDrawCalls.HasData || visiblePrimitives.HasData || shadowDrawCalls.HasData || shadowPrimitives.HasData;
        return new PhaseSummary(
            HasPhaseData: true,
            MeanSessionMs: sumSession * invPhaseCount,
            MaxSessionMs: maxSession,
            MeanFogOfWarMs: sumFog * invPhaseCount,
            MaxFogOfWarMs: maxFog,
            MeanViewsMs: sumViews * invPhaseCount,
            MaxViewsMs: maxViews,
            MeanOrderPreviewMs: sumPreview * invPhaseCount,
            MaxOrderPreviewMs: maxPreview,
            MeanMinimapMs: sumMinimap * invPhaseCount,
            MaxMinimapMs: maxMinimap,
            MeanEffectsAndHudMs: sumEffectsHud * invPhaseCount,
            MaxEffectsAndHudMs: maxEffectsHud,
            HasRenderCounters: hasCounters,
            MeanDrawCalls: drawCalls.Mean,
            MaxDrawCalls: drawCalls.Max,
            MeanPrimitives: primitives.Mean,
            MaxPrimitives: primitives.Max,
            LastActiveSceneNodes: lastActiveNodes,
            TotalLodSwitches: totalLodSwitches,
            HasPassCounters: hasPassCounters,
            MeanVisibleDrawCalls: visibleDrawCalls.Mean,
            MaxVisibleDrawCalls: visibleDrawCalls.Max,
            MeanVisiblePrimitives: visiblePrimitives.Mean,
            MaxVisiblePrimitives: visiblePrimitives.Max,
            MeanShadowDrawCalls: shadowDrawCalls.Mean,
            MaxShadowDrawCalls: shadowDrawCalls.Max,
            MeanShadowPrimitives: shadowPrimitives.Mean,
            MaxShadowPrimitives: shadowPrimitives.Max);
    }

    private static float GetPercentile(float[] sorted, float percentile)
    {
        if (sorted.Length == 0) return 0f;
        if (sorted.Length == 1) return sorted[0];
        float idx = percentile * (sorted.Length - 1);
        int low = (int)MathF.Floor(idx);
        int high = (int)MathF.Ceiling(idx);
        if (low == high) return sorted[low];
        float t = idx - low;
        return sorted[low] * (1f - t) + sorted[high] * t;
    }

    public string FormatReport(
        string scenario,
        string hardware,
        string resolution,
        bool vsync,
        int maxFps,
        int warmupFrames = 0)
    {
        PercentileResults res = CalculateResults();
        PhaseSummary phases = CalculatePhaseSummary();
        string vsyncStr = vsync ? "ON" : "OFF";
        string maxFpsStr = maxFps == 0 ? "Unlimited" : maxFps.ToString();
        string gpuStr = res.HasGpuTiming
            ? string.Create(CultureInfo.InvariantCulture, $"p50={res.GpuP50Ms:F2} ms | p95={res.GpuP95Ms:F2} ms | mean={res.MeanGpuMs:F2} ms")
            : "Not collected (GPU profiling disabled)";
        string bottleneckStr = res.HasGpuTiming && res.GpuP50Ms > res.MeanCpuMs
            ? "GPU bound"
            : "CPU bound";
        string warmupStr = warmupFrames > 0 ? $"{warmupFrames} initial warmup frames discarded" : "None";

        string report = string.Create(CultureInfo.InvariantCulture,
            $"------------------------------------------------------------------------\n" +
            $"DEAD GROUND FRAME PACING BENCHMARK REPORT\n" +
            $"------------------------------------------------------------------------\n" +
            $"Scenario:           {scenario}\n" +
            $"Platform:           {hardware}\n" +
            $"Resolution:         {resolution}\n" +
            $"Active Settings:    VSync={vsyncStr}, MaxFPS={maxFpsStr}, BackgroundCap=30\n" +
            $"Warmup Policy:      {warmupStr}\n" +
            $"Measurement Point:  _Process callback-to-callback (monotonic wall-clock)\n" +
            $"CPU Work Point:     _Process callback duration (subset of total engine CPU)\n" +
            $"------------------------------------------------------------------------\n" +
            $"Samples:            {res.SampleCount} frames\n" +
            $"------------------------------------------------------------------------\n" +
            $"Frame Interval P50: {res.P50Ms:F2} ms ({1000f / MathF.Max(0.01f, res.P50Ms):F1} FPS)\n" +
            $"Frame Interval P95: {res.P95Ms:F2} ms ({1000f / MathF.Max(0.01f, res.P95Ms):F1} FPS)\n" +
            $"Frame Interval P99: {res.P99Ms:F2} ms ({1000f / MathF.Max(0.01f, res.P99Ms):F1} FPS)\n" +
            $"Frame Interval Max: {res.MaxMs:F2} ms ({1000f / MathF.Max(0.01f, res.MaxMs):F1} FPS)\n" +
            $"------------------------------------------------------------------------\n" +
            $"Mean CPU Work:      {res.MeanCpuMs:F2} ms\n" +
            $"GPU Timing:         {gpuStr}\n" +
            $"Bottleneck:         {bottleneckStr} (GPU p50 vs _Process mean)\n" +
            $"Frames >  8.33 ms:  {res.LongFramesOver8Ms} ({(float)res.LongFramesOver8Ms / Math.Max(1, res.SampleCount) * 100f:F1}%)\n" +
            $"Frames > 16.67 ms:  {res.LongFramesOver16Ms} ({(float)res.LongFramesOver16Ms / Math.Max(1, res.SampleCount) * 100f:F1}%)\n" +
            $"Frames > 33.33 ms:  {res.LongFramesOver33Ms} ({(float)res.LongFramesOver33Ms / Math.Max(1, res.SampleCount) * 100f:F1}%)\n" +
            $"------------------------------------------------------------------------\n");

        if (phases.HasPhaseData)
        {
            report += string.Create(CultureInfo.InvariantCulture,
                $"CPU PHASE BREAKDOWN (mean / max ms)\n" +
                $"  Session Advance:  {phases.MeanSessionMs:F2} / {phases.MaxSessionMs:F2} ms\n" +
                $"  Fog of War:       {phases.MeanFogOfWarMs:F2} / {phases.MaxFogOfWarMs:F2} ms\n" +
                $"  Views & LODs:     {phases.MeanViewsMs:F2} / {phases.MaxViewsMs:F2} ms\n" +
                $"  Effects & HUD:    {phases.MeanEffectsAndHudMs:F2} / {phases.MaxEffectsAndHudMs:F2} ms\n" +
                $"  Order Preview:    {phases.MeanOrderPreviewMs:F2} / {phases.MaxOrderPreviewMs:F2} ms\n" +
                $"  Minimap:          {phases.MeanMinimapMs:F2} / {phases.MaxMinimapMs:F2} ms\n" +
                $"------------------------------------------------------------------------\n");
            if (phases.HasRenderCounters)
            {
                report += string.Create(CultureInfo.InvariantCulture,
                    $"RENDER COUNTERS\n" +
                    $"  Draw Calls:       mean {phases.MeanDrawCalls:F0} / max {phases.MaxDrawCalls}\n" +
                    $"  Primitives:       mean {phases.MeanPrimitives:F0} / max {phases.MaxPrimitives}\n" +
                    $"  Scene Nodes:      {phases.LastActiveSceneNodes}\n" +
                    $"  LOD Switches:     {phases.TotalLodSwitches}\n");
                if (phases.HasPassCounters)
                {
                    report += string.Create(CultureInfo.InvariantCulture,
                        $"  Visible Pass:     calls {phases.MeanVisibleDrawCalls:F0}/{phases.MaxVisibleDrawCalls} | prims {phases.MeanVisiblePrimitives:F0}/{phases.MaxVisiblePrimitives}\n" +
                        $"  Shadow Pass:      calls {phases.MeanShadowDrawCalls:F0}/{phases.MaxShadowDrawCalls} | prims {phases.MeanShadowPrimitives:F0}/{phases.MaxShadowPrimitives}\n");
                }
                report += "------------------------------------------------------------------------\n";
            }
        }

        return report;
    }
}
