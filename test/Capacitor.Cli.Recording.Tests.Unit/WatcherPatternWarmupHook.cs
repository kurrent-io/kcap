namespace Capacitor.Cli.Recording.Tests.Unit;

public static class WatcherPatternWarmupHook {
    [Before(Assembly)]
    public static void CompileWatcherPatterns() => WatcherPatternWarmup.Compile();
}
