internal sealed class ManualTime : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(double seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
}
