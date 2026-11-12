using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Services.Notifications;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Linux;

/// <param name="stagingFailure">Set when the AppImage could not copy its CLI out, which then runs from the mount.</param>
public sealed class LinuxDesktopHost(
        string cliDirectory, bool updateRelaunch, AppImageInstall? appImage, CliStagingFailure? stagingFailure, SingleInstanceLock singleInstance)
    : DesktopHost(cliDirectory, updateRelaunch, singleInstance) {
    internal static LinuxDesktopHost Start(string appDirectory, bool updateRelaunch, SingleInstanceLock singleInstance) {
        var appImage = AppImageInstall.Detect(Environment.GetEnvironmentVariable);
        CliStagingFailure? failure = null;
        var cliDirectory = appImage?.TakeCliOutOfMount(Environment.GetEnvironmentVariable, UserHome.FromEnvironment(), appDirectory, out failure)
                           ?? BundleLayout.CliDirectory(appDirectory);
        return new LinuxDesktopHost(cliDirectory, updateRelaunch, appImage, failure, singleInstance);
    }

    public override AppBuilder ConfigureAvalonia(AppBuilder builder) {
        Window.WindowDecorationMarginProperty.Changed.AddClassHandler<Window>((window, _) => KeepInsideDrawnFrame(window));
        return builder.With(new X11PlatformOptions {
#pragma warning disable AVALONIA_X11_CSD
            EnableDrawnDecorations = true,
#pragma warning restore AVALONIA_X11_CSD
            RenderingMode = LinuxRenderingOrder.Resolve(Environment.GetEnvironmentVariable(LinuxRenderingOrder.EnvVar)),
        });
    }

    static void KeepInsideDrawnFrame(Window window) {
        var margin = window.WindowDecorationMargin;
        window.Padding = new Thickness(margin.Left, margin.Bottom, margin.Right, margin.Bottom);
    }

    public override bool TrayAnnouncesMenuOpen => false;

    /// A new copy starts from the image, which may have been moved or deleted since this one started.
    public override bool CanRelaunchNow => appImage is null || File.Exists(appImage.Image);

    /// A CLI the image could not copy out runs from its mount.
    public override bool CliOutlivesApp => stagingFailure is null;

    public override bool CliCopyFailed => stagingFailure is not null;

    /// A daemon on the copy this run could not replace is the app's own, but stays on its version until a copy succeeds,
    /// unless its binary was replaced before the failure.
    public override DaemonOnOtherInstall? OtherInstall(string version, string binary) {
        var other = base.OtherInstall(version, binary);
        if (other is null || stagingFailure is not { } failure || !CanonicalPath.IsWithin(binary, failure.Directory)) return other;
        return failure.DaemonCopied ? null : other with { CopyFailure = failure.Reason };
    }

    public override IDesktopNotificationSink CreateNotificationSink(Action<Action> dispatch) => new LinuxDesktopNotificationSink(dispatch);

    /// The AppImage process runs inside a mount that vanishes when it exits; the image does not.
    protected override string? Executable => appImage?.Image ?? Environment.ProcessPath;
}
