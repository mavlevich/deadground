namespace DeadGround.Sandbox;

/// <summary>
/// Frame timing analysis and percentile metrics report.
/// Preallocates buffers to avoid allocations during active frame timing.
/// </summary>
public sealed class FramePacingMetrics
{
    private readonly float[] _frameIntervalsMs;
    private readonly float[] _cpuTimesMs;
    private readonly float[] _gpuTimesMs;
    private int _count;

    public int Capacity => _frameIntervalsMs.Length;
    public int Count => _count;

    public FramePacingMetrics(int capacity = 2000)
    {
        if (capacity < 16) capacity = 16;
        _frameIntervalsMs = new float[capacity];
        _cpuTimesMs = new float[capacity];
        _gpuTimesMs = new float[capacity];
    }

    public void Record(float frameIntervalMs, float cpuTimeMs = 0f, float gpuTimeMs = -1f)
    {
        if (_count >= _frameIntervalsMs.Length) return;
        _frameIntervalsMs[_count] = frameIntervalMs;
        _cpuTimesMs[_count] = cpuTimeMs;
        _gpuTimesMs[_count] = gpuTimeMs;
        _count++;
    }

    public void Reset()
    {
        _count = 0;
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
        bool HasGpuTiming);

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
        bool hasGpu = false;
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
                hasGpu = true;
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
            hasGpu ? sumGpu / _count : 0f,
            hasGpu);
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
        string vsyncStr = vsync ? "ON" : "OFF";
        string maxFpsStr = maxFps == 0 ? "Unlimited" : maxFps.ToString();
        string gpuStr = res.HasGpuTiming ? $"{res.MeanGpuMs:F2} ms" : "Not collected (GPU profiling disabled)";
        string warmupStr = warmupFrames > 0 ? $"{warmupFrames} initial warmup frames discarded" : "None";

        return
            "------------------------------------------------------------------------\n" +
            "DEAD GROUND FRAME PACING BENCHMARK REPORT\n" +
            "------------------------------------------------------------------------\n" +
            $"Scenario:           {scenario}\n" +
            $"Platform:           {hardware}\n" +
            $"Resolution:         {resolution}\n" +
            $"Active Settings:    VSync={vsyncStr}, MaxFPS={maxFpsStr}, BackgroundCap=30\n" +
            $"Warmup Policy:      {warmupStr}\n" +
            $"Measurement Point:  _Process callback-to-callback (monotonic wall-clock)\n" +
            $"CPU Work Point:     _Process callback execution duration\n" +
            "------------------------------------------------------------------------\n" +
            $"Samples:            {res.SampleCount} frames\n" +
            "------------------------------------------------------------------------\n" +
            $"Frame Interval P50: {res.P50Ms:F2} ms ({1000f / MathF.Max(0.01f, res.P50Ms):F1} FPS)\n" +
            $"Frame Interval P95: {res.P95Ms:F2} ms ({1000f / MathF.Max(0.01f, res.P95Ms):F1} FPS)\n" +
            $"Frame Interval P99: {res.P99Ms:F2} ms ({1000f / MathF.Max(0.01f, res.P99Ms):F1} FPS)\n" +
            $"Frame Interval Max: {res.MaxMs:F2} ms ({1000f / MathF.Max(0.01f, res.MaxMs):F1} FPS)\n" +
            "------------------------------------------------------------------------\n" +
            $"Mean CPU Work:      {res.MeanCpuMs:F2} ms\n" +
            $"GPU Timing:         {gpuStr}\n" +
            $"Frames >  8.33 ms:  {res.LongFramesOver8Ms} ({(float)res.LongFramesOver8Ms / Math.Max(1, res.SampleCount) * 100f:F1}%)\n" +
            $"Frames > 16.67 ms:  {res.LongFramesOver16Ms} ({(float)res.LongFramesOver16Ms / Math.Max(1, res.SampleCount) * 100f:F1}%)\n" +
            $"Frames > 33.33 ms:  {res.LongFramesOver33Ms} ({(float)res.LongFramesOver33Ms / Math.Max(1, res.SampleCount) * 100f:F1}%)\n" +
            "------------------------------------------------------------------------\n";
    }
}
