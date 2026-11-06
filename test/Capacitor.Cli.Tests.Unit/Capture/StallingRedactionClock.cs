namespace Capacitor.Cli.Tests.Unit.Capture;

/// <summary>A record-budget clock that stands still except for a stall the test arms.</summary>
sealed class StallingRedactionClock : TimeProvider {
    long _timestamp;
    int  _readingsLeft;
    long _stall;
    bool _repeats;

    public int Readings { get; private set; }

    public override long TimestampFrequency => 1000;

    /// <summary>Jumps by <paramref name="stall"/> on the given reading from now, counting from one.</summary>
    public void StallOnReading(int reading, TimeSpan stall) {
        _readingsLeft = reading;
        _stall        = (long)stall.TotalMilliseconds;
        _repeats      = false;
    }

    /// <summary>Jumps as <see cref="StallOnReading"/> does, then on every reading after that one.</summary>
    public void StallFromReading(int reading, TimeSpan stall) {
        StallOnReading(reading, stall);
        _repeats = true;
    }

    public override long GetTimestamp() {
        Readings++;

        if (_readingsLeft > 0 && --_readingsLeft == 0) {
            _timestamp    += _stall;
            _readingsLeft =  _repeats ? 1 : 0;
        }

        return _timestamp;
    }
}
