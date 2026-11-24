using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Platform.Linux;
using Tmds.DBus.Protocol;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Services;

[NotInParallel("AvaloniaSession")]
public class ReducedMotionTests {
    [Test]
    public async Task Portal_values_support_both_reply_shapes_and_an_unavailable_signal() {
        VariantValue reduce = 1u;
        VariantValue noPreference = 0u;
        await Assert.That(PortalReducedMotion.Decode(reduce)).IsTrue();
        await Assert.That(PortalReducedMotion.Decode(VariantValue.Variant(reduce))).IsTrue();
        await Assert.That(PortalReducedMotion.Decode(noPreference)).IsFalse();
        await Assert.That(PortalReducedMotion.Decode("unexpected")).IsNull();
    }

    [Test]
    public Task Manual_reduce_overrides_system_and_rolls_back_with_the_preview() => AvaloniaSession.RunOnUiAsync(async () => {
        try {
            ReducedMotion.OverrideForTesting(false);
            ReducedMotion.SetPreviewMotion(DesktopMotion.System);
            await Assert.That(ReducedMotion.IsOn).IsFalse();
            ReducedMotion.SetPreviewMotion(DesktopMotion.Reduce);
            await Assert.That(ReducedMotion.IsOn).IsTrue();
            ReducedMotion.SetPreviewMotion(null);
            await Assert.That(ReducedMotion.IsOn).IsFalse();
            ReducedMotion.OverrideForTesting(true);
            await Assert.That(ReducedMotion.IsOn).IsTrue();
            ReducedMotion.SetPreviewMotion(DesktopMotion.System);
            await Assert.That(ReducedMotion.IsOn).IsTrue();
        } finally {
            ReducedMotion.SetPreviewMotion(null);
            ReducedMotion.OverrideForTesting(null);
        }
    });

    [Test]
    public Task Visible_activity_indicators_hold_still_while_motion_is_reduced() => AvaloniaSession.RunOnUiAsync(async () => {
        var mascot = new OuroMascot { IsPlaying = true, Width = 32, Height = 32 };
        var dot = new Border { Width = 8, Height = 8 };
        var progress = new ProgressBar();
        BusyIndicator.SetIsBusy(progress, true);
        PulseClock.SetIsActive(dot, true);
        var window = new Window { Content = new StackPanel { Children = { mascot, dot, progress } } };
        try {
            ReducedMotion.OverrideForTesting(false);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mascot.IsStepping).IsTrue();
            await Assert.That(PulseClock.IsRunning).IsTrue();
            await Assert.That(progress.IsIndeterminate).IsTrue();

            ReducedMotion.OverrideForTesting(true);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mascot.IsStepping).IsFalse();
            await Assert.That(PulseClock.IsRunning).IsFalse();
            await Assert.That(dot.Opacity).IsEqualTo(1);
            await Assert.That(progress.IsIndeterminate).IsFalse();

            ReducedMotion.OverrideForTesting(false);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mascot.IsStepping).IsTrue();
            await Assert.That(progress.IsIndeterminate).IsTrue();
        } finally {
            ReducedMotion.OverrideForTesting(null);
            window.Close();
        }
    });
}
