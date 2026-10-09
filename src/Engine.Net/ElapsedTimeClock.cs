namespace DeadGround.Engine.Net;

// Timer notifications only wake the host. A monotonic timestamp measures the time to simulate.
public sealed class ElapsedTimeClock
{
    private readonly TimeProvider _time;
    private long _previous;

    public ElapsedTimeClock(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _previous = _time.GetTimestamp();
    }

    public double ReadSeconds()
    {
        long now = _time.GetTimestamp();
        double seconds = _time.GetElapsedTime(_previous, now).TotalSeconds;
        _previous = now;
        return seconds;
    }
}
