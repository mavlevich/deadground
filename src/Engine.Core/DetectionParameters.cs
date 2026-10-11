namespace DeadGround.Engine;

/// <summary>
/// Configured target observation points relative to unit support surface.
/// Allows geometric partial visibility (e.g. hull-down behind a low wall).
/// </summary>
public sealed class TargetObservationPoints
{
    public float[] RelativeHeights { get; }

    public TargetObservationPoints(params float[] relativeHeights)
    {
        ArgumentNullException.ThrowIfNull(relativeHeights);
        if (relativeHeights.Length is < 1 or > 8)
            throw new ArgumentException("Target points count must be between 1 and 8.", nameof(relativeHeights));

        for (int i = 0; i < relativeHeights.Length; i++)
        {
            if (!float.IsFinite(relativeHeights[i]) || relativeHeights[i] < 0)
                throw new ArgumentOutOfRangeException(nameof(relativeHeights), "Relative height must be non-negative and finite.");
        }

        RelativeHeights = (float[])relativeHeights.Clone();
        Array.Sort(RelativeHeights);
    }
}

/// <summary>
/// Visual sensor parameters for an observing unit.
/// </summary>
public sealed class VisualSensorProfile
{
    public float Range { get; }
    public float EyeHeight { get; }

    public VisualSensorProfile(float range, float eyeHeight)
    {
        if (!float.IsFinite(range) || range <= 0)
            throw new ArgumentOutOfRangeException(nameof(range), "Visual range must be positive and finite.");
        if (!float.IsFinite(eyeHeight) || eyeHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(eyeHeight), "Eye height must be positive and finite.");

        Range = range;
        EyeHeight = eyeHeight;
    }
}

/// <summary>
/// Simulation-side detection, correlation and memory parameters.
/// </summary>
public sealed class DetectionTuning
{
    public const int DefaultMemoryDurationTicks = 100;
    public const int DefaultCorrelationWindowTicks = 60;
    public const float DefaultCorrelationDistanceGate = 25.0f;
    public const int DefaultEvaluationIntervalTicks = 2;
    public const float DefaultMaxCorrelationSpeed = 10.0f;
    public const float DefaultSlack = 5.0f;
    public const float DefaultMaxGate = 35.0f;

    public int MemoryDurationTicks { get; }
    public int CorrelationWindowTicks { get; }
    public float CorrelationDistanceGate { get; }
    public int EvaluationIntervalTicks { get; }
    public float MaxCorrelationSpeed { get; }
    public float Slack { get; }
    public float MaxGate { get; }

    public DetectionTuning(
        int memoryDurationTicks = DefaultMemoryDurationTicks,
        int correlationWindowTicks = DefaultCorrelationWindowTicks,
        float correlationDistanceGate = DefaultCorrelationDistanceGate,
        int evaluationIntervalTicks = DefaultEvaluationIntervalTicks,
        float maxCorrelationSpeed = DefaultMaxCorrelationSpeed,
        float slack = DefaultSlack,
        float? maxGate = null)
    {
        if (memoryDurationTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(memoryDurationTicks), "Memory duration must be positive.");
        if (correlationWindowTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(correlationWindowTicks), "Correlation window must be positive.");
        if (!float.IsFinite(correlationDistanceGate) || correlationDistanceGate <= 0)
            throw new ArgumentOutOfRangeException(nameof(correlationDistanceGate), "Correlation gate must be positive and finite.");
        if (evaluationIntervalTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(evaluationIntervalTicks), "Evaluation interval must be positive.");
        if (!float.IsFinite(maxCorrelationSpeed) || maxCorrelationSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCorrelationSpeed), "Max correlation speed must be positive and finite.");
        if (!float.IsFinite(slack) || slack < 0)
            throw new ArgumentOutOfRangeException(nameof(slack), "Slack must be non-negative and finite.");

        float resolvedMaxGate = maxGate ?? MathF.Max(correlationDistanceGate, DefaultMaxGate);
        if (!float.IsFinite(resolvedMaxGate) || resolvedMaxGate <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxGate), "Max gate must be positive and finite.");

        MemoryDurationTicks = memoryDurationTicks;
        CorrelationWindowTicks = correlationWindowTicks;
        CorrelationDistanceGate = correlationDistanceGate;
        EvaluationIntervalTicks = evaluationIntervalTicks;
        MaxCorrelationSpeed = maxCorrelationSpeed;
        Slack = slack;
        MaxGate = resolvedMaxGate;
    }
}
