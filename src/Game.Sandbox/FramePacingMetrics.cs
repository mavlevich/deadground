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
    private readonly float[] _gpuTimesMs;
    private int _count;
    private int _totalSamples;

    // Exact streaming accumulators maintained in O(1) without allocations even past percentile capacity
    private double _sumCpuMs;
    private double _sumGpuMs;
    private int _gpuSampleCount;
    private float _maxMs;
    private int _over8;
    private int _over16;
    private int _over33;
    private int _histLe8;
    private int _hist8To16;
    private int _hist16To25;
    private int _hist25To33;
    private int _hist33To42;
    private int _histGt42;

    private int _phaseSampleCount;
    private double _sumSessionMs;
    private float _maxSessionMs;
    private double _sumFogOfWarMs;
    private float _maxFogOfWarMs;
    private double _sumViewsMs;
    private float _maxViewsMs;
    private double _sumOrderPreviewMs;
    private float _maxOrderPreviewMs;
    private double _sumMinimapMs;
    private float _maxMinimapMs;
    private double _sumEffectsAndHudMs;
    private float _maxEffectsAndHudMs;
    private CounterAccumulator _drawCalls;
    private CounterAccumulator _primitives;
    private CounterAccumulator _visibleDrawCalls;
    private CounterAccumulator _visiblePrimitives;
    private CounterAccumulator _shadowDrawCalls;
    private CounterAccumulator _shadowPrimitives;
    private int _lastActiveSceneNodes = -1;
    private int _totalLodSwitches;

    public int Capacity => _frameIntervalsMs.Length;
    public int Count => _count;
    public int TotalCount => _totalSamples;
    public bool IsBufferSaturated => _totalSamples > _count;
    public int DroppedPercentileSamples => _totalSamples - _count;

    public FramePacingMetrics(int capacity = 2000)
    {
        if (capacity < 16) capacity = 16;
        _frameIntervalsMs = new float[capacity];
        _gpuTimesMs = new float[capacity];
    }

    public void Record(float frameIntervalMs, float cpuTimeMs = 0f, float gpuTimeMs = -1f)
    {
        RecordCore(frameIntervalMs, cpuTimeMs, gpuTimeMs);
    }

    public void Record(float frameIntervalMs, float cpuTimeMs, float gpuTimeMs, in FramePhaseSample phases)
    {
        RecordCore(frameIntervalMs, cpuTimeMs, gpuTimeMs);

        _phaseSampleCount++;
        _sumSessionMs += phases.SessionMs;
        if (phases.SessionMs > _maxSessionMs) _maxSessionMs = phases.SessionMs;

        _sumFogOfWarMs += phases.FogOfWarMs;
        if (phases.FogOfWarMs > _maxFogOfWarMs) _maxFogOfWarMs = phases.FogOfWarMs;

        _sumViewsMs += phases.ViewsMs;
        if (phases.ViewsMs > _maxViewsMs) _maxViewsMs = phases.ViewsMs;

        _sumOrderPreviewMs += phases.OrderPreviewMs;
        if (phases.OrderPreviewMs > _maxOrderPreviewMs) _maxOrderPreviewMs = phases.OrderPreviewMs;

        _sumMinimapMs += phases.MinimapMs;
        if (phases.MinimapMs > _maxMinimapMs) _maxMinimapMs = phases.MinimapMs;

        _sumEffectsAndHudMs += phases.EffectsAndHudMs;
        if (phases.EffectsAndHudMs > _maxEffectsAndHudMs) _maxEffectsAndHudMs = phases.EffectsAndHudMs;

        _drawCalls.Add(phases.RenderDrawCalls);
        _primitives.Add(phases.RenderPrimitives);
        _visibleDrawCalls.Add(phases.VisibleDrawCalls);
        _visiblePrimitives.Add(phases.VisiblePrimitives);
        _shadowDrawCalls.Add(phases.ShadowDrawCalls);
        _shadowPrimitives.Add(phases.ShadowPrimitives);

        if (phases.ActiveSceneNodes >= 0) _lastActiveSceneNodes = phases.ActiveSceneNodes;
        _totalLodSwitches += phases.LodSwitches;
    }

    private void RecordCore(float frameIntervalMs, float cpuTimeMs, float gpuTimeMs)
    {
        if (_count < _frameIntervalsMs.Length)
        {
            _frameIntervalsMs[_count] = frameIntervalMs;
            _gpuTimesMs[_count] = gpuTimeMs;
            _count++;
        }

        _totalSamples++;
        _sumCpuMs += cpuTimeMs;
        if (gpuTimeMs >= 0f)
        {
            _sumGpuMs += gpuTimeMs;
            _gpuSampleCount++;
        }
        if (frameIntervalMs > _maxMs) _maxMs = frameIntervalMs;

        if (frameIntervalMs > 8.333f) _over8++;
        if (frameIntervalMs > 16.667f) _over16++;
        if (frameIntervalMs > 33.333f) _over33++;

        if (frameIntervalMs <= 8.333f) _histLe8++;
        else if (frameIntervalMs <= 16.667f) _hist8To16++;
        else if (frameIntervalMs <= 25.0f) _hist16To25++;
        else if (frameIntervalMs <= 33.333f) _hist25To33++;
        else if (frameIntervalMs <= 41.667f) _hist33To42++;
        else _histGt42++;
    }

    public void Reset()
    {
        _count = 0;
        _totalSamples = 0;
        _sumCpuMs = 0.0;
        _sumGpuMs = 0.0;
        _gpuSampleCount = 0;
        _maxMs = 0f;
        _over8 = 0;
        _over16 = 0;
        _over33 = 0;
        _histLe8 = 0;
        _hist8To16 = 0;
        _hist16To25 = 0;
        _hist25To33 = 0;
        _hist33To42 = 0;
        _histGt42 = 0;
        _phaseSampleCount = 0;
        _sumSessionMs = 0.0;
        _maxSessionMs = 0f;
        _sumFogOfWarMs = 0.0;
        _maxFogOfWarMs = 0f;
        _sumViewsMs = 0.0;
        _maxViewsMs = 0f;
        _sumOrderPreviewMs = 0.0;
        _maxOrderPreviewMs = 0f;
        _sumMinimapMs = 0.0;
        _maxMinimapMs = 0f;
        _sumEffectsAndHudMs = 0.0;
        _maxEffectsAndHudMs = 0f;
        _drawCalls = default;
        _primitives = default;
        _visibleDrawCalls = default;
        _visiblePrimitives = default;
        _shadowDrawCalls = default;
        _shadowPrimitives = default;
        _lastActiveSceneNodes = -1;
        _totalLodSwitches = 0;
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
        float GpuP95Ms = 0f,
        string Histogram = "",
        int StoredSampleCount = 0,
        bool ArePercentilesTruncated = false);

    public PercentileResults CalculateResults()
    {
        if (_totalSamples == 0)
        {
            return new PercentileResults(0, 0f, 0f, 0f, 0f, 0, 0, 0, 0f, 0f, false);
        }

        // Copy stored frame intervals in milliseconds for sorting
        float[] sortedMs = new float[_count];
        int gpuStoredCount = 0;
        for (int i = 0; i < _count; i++)
        {
            sortedMs[i] = _frameIntervalsMs[i];
            if (_gpuTimesMs[i] >= 0f)
            {
                gpuStoredCount++;
            }
        }

        string histSummary = string.Create(CultureInfo.InvariantCulture,
            $"<=8.3ms:{_histLe8} | 8.3-16.7:{_hist8To16} | 16.7-25:{_hist16To25} | 25-33.3:{_hist25To33} | 33.3-41.7:{_hist33To42} | >41.7:{_histGt42}");

        Array.Sort(sortedMs);

        float p50 = GetPercentile(sortedMs, 0.50f);
        float p95 = GetPercentile(sortedMs, 0.95f);
        float p99 = GetPercentile(sortedMs, 0.99f);
        bool hasGpu = _gpuSampleCount > 0;
        float gpuP50 = 0f;
        float gpuP95 = 0f;
        if (gpuStoredCount > 0)
        {
            float[] sortedGpu = new float[gpuStoredCount];
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
            _totalSamples,
            p50,
            p95,
            p99,
            _maxMs,
            _over16,
            _over33,
            _over8,
            (float)(_sumCpuMs / _totalSamples),
            hasGpu ? (float)(_sumGpuMs / _gpuSampleCount) : 0f,
            hasGpu,
            gpuP50,
            gpuP95,
            histSummary,
            _count,
            _totalSamples > _count);
    }

    public PhaseSummary CalculatePhaseSummary()
    {
        if (_totalSamples == 0 || _phaseSampleCount == 0)
        {
            return default;
        }

        bool hasCounters = _drawCalls.HasData || _primitives.HasData || _lastActiveSceneNodes >= 0;
        bool hasPassCounters = _visibleDrawCalls.HasData || _visiblePrimitives.HasData || _shadowDrawCalls.HasData || _shadowPrimitives.HasData;
        return new PhaseSummary(
            HasPhaseData: true,
            MeanSessionMs: (float)(_sumSessionMs / _phaseSampleCount),
            MaxSessionMs: _maxSessionMs,
            MeanFogOfWarMs: (float)(_sumFogOfWarMs / _phaseSampleCount),
            MaxFogOfWarMs: _maxFogOfWarMs,
            MeanViewsMs: (float)(_sumViewsMs / _phaseSampleCount),
            MaxViewsMs: _maxViewsMs,
            MeanOrderPreviewMs: (float)(_sumOrderPreviewMs / _phaseSampleCount),
            MaxOrderPreviewMs: _maxOrderPreviewMs,
            MeanMinimapMs: (float)(_sumMinimapMs / _phaseSampleCount),
            MaxMinimapMs: _maxMinimapMs,
            MeanEffectsAndHudMs: (float)(_sumEffectsAndHudMs / _phaseSampleCount),
            MaxEffectsAndHudMs: _maxEffectsAndHudMs,
            HasRenderCounters: hasCounters,
            MeanDrawCalls: _drawCalls.Mean,
            MaxDrawCalls: _drawCalls.Max,
            MeanPrimitives: _primitives.Mean,
            MaxPrimitives: _primitives.Max,
            LastActiveSceneNodes: _lastActiveSceneNodes,
            TotalLodSwitches: _totalLodSwitches,
            HasPassCounters: hasPassCounters,
            MeanVisibleDrawCalls: _visibleDrawCalls.Mean,
            MaxVisibleDrawCalls: _visibleDrawCalls.Max,
            MeanVisiblePrimitives: _visiblePrimitives.Mean,
            MaxVisiblePrimitives: _visiblePrimitives.Max,
            MeanShadowDrawCalls: _shadowDrawCalls.Mean,
            MaxShadowDrawCalls: _shadowDrawCalls.Max,
            MeanShadowPrimitives: _shadowPrimitives.Mean,
            MaxShadowPrimitives: _shadowPrimitives.Max);
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
        string samplesStr = res.ArePercentilesTruncated
            ? string.Create(CultureInfo.InvariantCulture, $"{res.SampleCount} frames (percentile buffer full: {res.StoredSampleCount}/{res.SampleCount} kept; summary/histogram exact)")
            : string.Create(CultureInfo.InvariantCulture, $"{res.SampleCount} frames");

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
            $"Samples:            {samplesStr}\n" +
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
            $"Frame Histogram:    {res.Histogram}\n" +
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
