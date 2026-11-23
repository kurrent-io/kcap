using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Services;

/// With the OS set to reduce motion, the mascot and the running-status pulse hold still.
[NotInParallel("AvaloniaSession")]
public class ReducedMotionTests {
    [Test]
    public Task The_mascot_and_the_pulse_hold_still_while_motion_is_reduced() => AvaloniaSession.RunOnUiAsync(async () => {
        var mascot = new OuroMascot { IsPlaying = true, Width = 32, Height = 32 };
        var dot = new Border { Width = 8, Height = 8 };
        PulseClock.SetIsActive(dot, true);
        var window = new Window { Content = new StackPanel { Children = { mascot, dot } } };
        try {
            ReducedMotion.OverrideForTesting(false);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mascot.IsStepping).IsTrue();
            await Assert.That(PulseClock.IsRunning).IsTrue();

            ReducedMotion.OverrideForTesting(true);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mascot.IsStepping).IsFalse();
            await Assert.That(PulseClock.IsRunning).IsFalse();
            await Assert.That(dot.Opacity).IsEqualTo(1);

            ReducedMotion.OverrideForTesting(false);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mascot.IsStepping).IsTrue();
        } finally {
            ReducedMotion.OverrideForTesting(null);
            window.Close();
        }
    });
}
