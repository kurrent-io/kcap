using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon;

/// <summary>Warns at every interval a boot stays short of connecting, naming the step it is in, so a boot
/// that never finishes says where it stopped. A kill from outside leaves only the last of these.</summary>
internal sealed partial class StartupWatch : IDisposable {
    readonly ILogger      _logger;
    readonly TimeProvider _time;
    readonly long         _started;
    readonly ITimer       _timer;
    string                _step;

    public StartupWatch(ILogger logger, TimeProvider time, string step, TimeSpan interval) {
        _logger  = logger;
        _time    = time;
        _step    = step;
        _started = time.GetTimestamp();
        _timer   = time.CreateTimer(_ => Warn(), null, interval, interval);
    }

    public void Entering(string step) => Volatile.Write(ref _step, step);

    public void Dispose() => _timer.Dispose();

    void Warn() => LogStillStarting(_logger, (int)_time.GetElapsedTime(_started).TotalSeconds, Volatile.Read(ref _step));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup has not finished {Seconds}s after the host was built; it is {Step}")]
    static partial void LogStillStarting(ILogger logger, int seconds, string step);
}
