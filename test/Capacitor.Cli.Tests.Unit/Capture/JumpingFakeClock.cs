using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Capture;

/// <summary>A fake clock that also jumps once, on a chosen reading of its timestamp.</summary>
sealed class JumpingFakeClock : FakeTimeProvider {
    int      _readingsLeft;
    TimeSpan _jump;

    /// <summary>Jumps by <paramref name="jump"/> on the given reading from now, counting from one.</summary>
    public void JumpOnReading(int reading, TimeSpan jump) {
        _readingsLeft = reading;
        _jump         = jump;
    }

    public override long GetTimestamp() {
        if (_readingsLeft > 0 && --_readingsLeft == 0) Advance(_jump);

        return base.GetTimestamp();
    }
}
