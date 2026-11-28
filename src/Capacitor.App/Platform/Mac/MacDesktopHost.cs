using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.Services.Notifications;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Mac;

[SupportedOSPlatform("macos")]
public sealed class MacDesktopHost(string cliDirectory, bool updateRelaunch, SingleInstanceLock singleInstance) : DesktopHost(cliDirectory, updateRelaunch, singleInstance) {
    public override AppBuilder ConfigureAvalonia(AppBuilder builder) {
        AppKitMenus.SuppressFullScreenItem();
        return builder.With(new AvaloniaNativePlatformOptions {
            RenderingMode = MacRenderingOrder.Resolve(Environment.GetEnvironmentVariable(MacRenderingOrder.EnvVar)),
            AvaloniaNativeLibraryPath = PatchedAvaloniaNative.Path,
        });
    }

    /// Skia's subpixel edging turns on CoreText font smoothing, which thickens and fringes stems.
    public override TextRenderingMode TextRendering => TextRenderingMode.Antialias;

    public override void WaitUntilReady() =>
        ActiveDisplayGate.WaitAsync(CoreVideoDisplayLink.Probe, TimeProvider.System,
            onWaiting: () => Console.Error.WriteLine("Every display is asleep; the app starts once one wakes."))
            .GetAwaiter().GetResult();

    public override IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch) =>
        new MacOsDesktopNotificationSink(new MacNotificationCenter(), dispatch);

    /// `open -n` starts a second instance of the bundle.
    public override async Task<bool> StartSecondCopyAsync(IClassicDesktopStyleApplicationLifetime desktop, TimeProvider time, CancellationToken ct) {
        if (InstallLocation.BundleRoot(Environment.ProcessPath) is not { } bundle) return false;
        var result = await new ProcessRunner(time).RunAsync("/usr/bin/open", ["-n", bundle],
            new RunOptions(Timeout: TimeSpan.FromSeconds(10)), ct);
        return result is { TimedOut: false, ExitCode: 0 };
    }
}
