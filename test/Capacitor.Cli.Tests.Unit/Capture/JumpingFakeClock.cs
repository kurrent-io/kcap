using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Capture;

/// <summary>A fake clock that also jumps once, on a chosen reading of its timestamp.</summary>
sealed class JumpingFakeClock : FakeTimeProvider {
    // The hook's deadline and its drain read the clock concurrently, so a reading, the jump it triggers
    // and the time it returns happen as one step.
    readonly Lock _gate = new();
    int           _readingsLeft;
    TimeSpan      _jump;

    /// <summary>Jumps by <paramref name="jump"/> on the given reading from now, counting from one.</summary>
    public void JumpOnReading(int reading, TimeSpan jump) {
        lock (_gate) {
            _readingsLeft = reading;
            _jump         = jump;
        }
    }

    public override long GetTimestamp() {
        lock (_gate) {
            if (_readingsLeft > 0 && --_readingsLeft == 0) Advance(_jump);

            return base.GetTimestamp();
        }
    }
}
