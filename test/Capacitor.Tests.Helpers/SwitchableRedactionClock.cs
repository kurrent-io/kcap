namespace Capacitor.Tests.Helpers;

/// <summary>A record-budget clock that is frozen, or jumps far past every budget on each read.</summary>
public sealed class SwitchableRedactionClock : TimeProvider {
    long _timestamp;
    volatile bool _exhausted = true;
    public bool Exhausted { get => _exhausted; set => _exhausted = value; }
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _exhausted ? Interlocked.Add(ref _timestamp, 1_000_000_000) : Interlocked.Read(ref _timestamp);
}
