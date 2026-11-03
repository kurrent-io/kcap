namespace Capacitor.Cli.Core.Harness.Dsh;

/// <summary>DeepSeek Harness as this process sees it. Recording-only: no daemon hosts it.</summary>
public sealed class DshHarness : IHarness<DshHarness> {
    DshHarness(DshPaths paths) => Paths = paths;

    /// <summary>Resolves dsh's one override, <c>DSH_HOME</c>, which replaces <c>~/.dsh</c> wholesale.</summary>
    public static DshHarness FromEnvironment(UserHome home) =>
        Over(new(home, Environment.GetEnvironmentVariable("DSH_HOME")));

    /// <summary>Over a layout resolved elsewhere — a test's, or a subset.</summary>
    public static DshHarness Over(DshPaths paths) => new(paths);

    public static HarnessId Id        => HarnessId.Dsh;
    public static string    Label     => "DeepSeek Harness";
    public static string    CliBinary => "dsh";

    public DshPaths Paths { get; }

    public HarnessSignals Signals => new() {
        LaunchSignal   = probe => probe.Finds(CliBinary),
        UserDataSignal = Paths.HasUserData,
        Wired          = () => DshPluginInstaller.IsInstalled(Paths),
    };
}
