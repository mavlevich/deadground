using System.Numerics;

namespace DeadGround.Sandbox;

/// <summary>
/// Diagnostic sample recorded per render frame to inspect vehicle movement smoothness.
/// Value type with zero heap allocations during recording.
/// </summary>
public readonly record struct MotionSample(
    double ElapsedSeconds,
    int ServerTick,
    double RenderTick,
    int LeftTick,
    int RightTick,
    float Alpha,
    Vector2 Position,
    float Displacement,
    float HullHeading = 0f,
    int Lod = 0,
    double RawDeltaSeconds = 0.0);

/// <summary>
/// Preallocated, bounded diagnostics buffer to inspect motion pacing, stalls, and discontinuities.
/// Avoids per-frame console logging and allocations in steady state.
/// </summary>
public sealed class MotionDiagnostics
{
    private readonly MotionSample[] _samples;
    private int _count;

    public int Capacity => _samples.Length;
    public int Count => _count;

    public MotionDiagnostics(int capacity = 2000)
    {
        _samples = new MotionSample[Math.Max(16, capacity)];
    }

    public void Record(
        double elapsedSeconds,
        int serverTick,
        double renderTick,
        int leftTick,
        int rightTick,
        float alpha,
        Vector2 position,
        float displacement,
        float hullHeading = 0f,
        int lod = 0,
        double rawDeltaSeconds = 0.0)
    {
        if (_count >= _samples.Length) return;
        _samples[_count++] = new MotionSample(
            elapsedSeconds,
            serverTick,
            renderTick,
            leftTick,
            rightTick,
            alpha,
            position,
            displacement,
            hullHeading,
            lod,
            rawDeltaSeconds <= 0.0 ? elapsedSeconds : rawDeltaSeconds);
    }

    public void Reset()
    {
        _count = 0;
    }

    public ReadOnlySpan<MotionSample> Samples => _samples.AsSpan(0, _count);

    public readonly record struct Analysis(
        int SampleCount,
        int StallFrames,
        int JumpFrames,
        int StutterFrames,
        int ClockStallFrames,
        int ClockJumpFrames,
        int ClockStutterFrames,
        int PositionStallFrames,
        int PositionJumpFrames,
        int PositionStutterFrames,
        float MaxDisplacement,
        float MinDisplacement,
        float MeanDisplacement,
        double MaxDeltaTick,
        double MinDeltaTick,
        double MeanDeltaTick);

    /// <summary>
    /// Analyzes continuous motion samples after an optional warm-up period.
    /// Exposes clock pacing and spatial motion metrics, with aggregate counters representing
    /// the boolean union of affected frame identities.
    /// When expectedSpeed >= 0 is supplied, spatial checks evaluate displacement against nominal travel.
    /// When expectedSpeed < 0 (unspecified / default), only descriptive statistics (min/max/mean displacement)
    /// are reported without classifying legitimate stops, acceleration, or turns as rendering defects.
    /// </summary>
    public Analysis Analyze(int warmupFrames = 30, float expectedSpeed = -1f)
    {
        if (warmupFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(warmupFrames), "Warmup frames cannot be negative.");
        }

        int startIndex = Math.Max(1, warmupFrames);
        if (_count <= startIndex)
        {
            return default;
        }

        int warmCount = _count - startIndex;
        int clockStalls = 0;
        int clockJumps = 0;
        int clockStutters = 0;
        int posStalls = 0;
        int posJumps = 0;
        int posStutters = 0;
        int stallFrames = 0;
        int jumpFrames = 0;
        int stutterFrames = 0;

        float maxDisp = float.MinValue;
        float minDisp = float.MaxValue;
        float sumDisp = 0f;
        double maxDTick = double.MinValue;
        double minDTick = double.MaxValue;
        double sumDTick = 0.0;

        for (int i = startIndex; i < _count; i++)
        {
            MotionSample current = _samples[i];
            MotionSample previous = _samples[i - 1];

            double dTick = current.RenderTick - previous.RenderTick;
            float disp = current.Displacement;
            double dt = current.ElapsedSeconds;
            double expectedDTick = dt * 20.0; // 20 Hz simulation rate

            sumDisp += disp;
            sumDTick += dTick;

            if (disp > maxDisp) maxDisp = disp;
            if (disp < minDisp) minDisp = disp;
            if (dTick > maxDTick) maxDTick = dTick;
            if (dTick < minDTick) minDTick = dTick;

            // Clock pacing checks
            bool isClockStall = dTick < expectedDTick * 0.5;
            bool isClockJump = dTick > expectedDTick * 1.5;
            bool isClockStutter = Math.Abs(dTick - expectedDTick) > 0.08;

            if (isClockStall) clockStalls++;
            if (isClockJump) clockJumps++;
            if (isClockStutter) clockStutters++;

            // Spatial motion checks: only performed when explicit reference speed is provided
            bool isPosStall = false;
            bool isPosJump = false;
            bool isPosStutter = false;

            if (expectedSpeed >= 0f)
            {
                float expectedDisp = expectedSpeed * (float)dt;
                if (expectedSpeed > 0f)
                {
                    isPosStall = disp < expectedDisp * 0.5f;
                    isPosJump = disp > expectedDisp * 1.5f;
                    isPosStutter = MathF.Abs(disp - expectedDisp) > 0.15f * expectedDisp;
                }
                else
                {
                    // Unit expected to be stationary: any non-negligible displacement is an unexpected jump
                    isPosJump = disp > 0.005f;
                }

                if (isPosStall) posStalls++;
                if (isPosJump) posJumps++;
                if (isPosStutter) posStutters++;
            }

            // Frame-identity boolean union: each frame is counted at most once
            if (isClockStall || isPosStall) stallFrames++;
            if (isClockJump || isPosJump) jumpFrames++;
            if (isClockStutter || isPosStutter) stutterFrames++;
        }

        return new Analysis(
            warmCount,
            stallFrames,
            jumpFrames,
            stutterFrames,
            clockStalls,
            clockJumps,
            clockStutters,
            posStalls,
            posJumps,
            posStutters,
            maxDisp,
            minDisp,
            sumDisp / warmCount,
            maxDTick,
            minDTick,
            sumDTick / warmCount);
    }
}
