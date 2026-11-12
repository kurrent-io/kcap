namespace Capacitor.Cli.Daemon.Tests.Unit;

public static class WatcherPatternWarmupHook {
    [Before(Assembly)]
    public static void CompileWatcherPatterns() => WatcherPatternWarmup.Compile();
}
// CI selection probe, to be discarded.
