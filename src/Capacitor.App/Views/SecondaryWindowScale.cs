using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

/// The text-size setting for every window without a scale host of its own (About, Feedback, Sign in,
/// the update and consent prompts…). Installed once per application: each such window's content is
/// wrapped in a scale host before its first layout, and a fixed-size window grows with the scale,
/// up to its screen's work area.
public static class SecondaryWindowScale {
    static bool _installed;

    public static void Install(UiScaleSetting setting) {
        if (_installed) return;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<Window>((window, _) => Attach(window, setting, installed: true));
    }

    // MainWindow and SettingsWindow apply the scale through their own ScaleHost; these have none.
    static readonly HashSet<Type> Scaled = [
        typeof(AboutWindow), typeof(ConsentPromptWindow), typeof(FeedbackWindow), typeof(InstallLocationWindow),
        typeof(LaunchApprovalsWindow), typeof(LifecyclePromptWindow), typeof(SignInWindow), typeof(UninstallWindow),
        typeof(UpdateCheckWindow), typeof(UpdateFinishWindow),
    ];

    static void Attach(Window window, UiScaleSetting setting, bool installed) {
        if (installed && !Scaled.Contains(window.GetType())) return;
        Attach(window, setting);
    }

    public static void Attach(Window window, UiScaleSetting setting) {
        if (window.Content is not Control content || content is LayoutTransformControl { Name: HostName }) return;

        window.Content = null;
        var host = new LayoutTransformControl { Name = HostName, Child = content };
        window.Content = host;

        // Sizes at 100 %, so a scale capped by the screen does not shrink the window when it steps back.
        var unscaled = new Frame(window.Width, window.Height, window.MinWidth, window.MinHeight, window.MaxHeight);
        var subscription = setting.Changes
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(scale => {
                host.LayoutTransform = UiScale.IsDefault(scale) ? null : new ScaleTransform(scale, scale);
                Resize(window, unscaled, scale);
            });
        window.Closed += (_, _) => subscription.Dispose();
    }

    const string HostName = "SecondaryScaleHost";

    readonly record struct Frame(double Width, double Height, double MinWidth, double MinHeight, double MaxHeight);

    static void Resize(Window window, Frame unscaled, double scale) {
        var area = (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } screen
            ? screen.WorkingArea.Size.ToSize(screen.Scaling)
            : (Size?)null;
        double Fit(double size, double? limit) => Math.Min(size * scale, limit ?? double.MaxValue);
        if (window.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight) && double.IsFinite(unscaled.Width))
            window.Width = Fit(unscaled.Width, area?.Width);
        if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight) {
            window.MaxHeight = Math.Min(unscaled.MaxHeight * scale, area?.Height ?? double.PositiveInfinity);
        } else if (double.IsFinite(unscaled.Height)) {
            window.Height = Fit(unscaled.Height, area?.Height);
        }
        if (unscaled.MinWidth > 0) window.MinWidth = Fit(unscaled.MinWidth, area?.Width);
        if (unscaled.MinHeight > 0) window.MinHeight = Fit(unscaled.MinHeight, area?.Height);
    }
}
