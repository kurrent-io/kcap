using Avalonia.Input;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

public class AltTapTests {
    [Test]
    [Arguments(Key.LeftAlt)]
    [Arguments(Key.RightAlt)]
    public async Task Alt_pressed_and_released_alone_is_a_tap(Key alt) {
        var tap = new AltTap();
        tap.KeyDown(alt, KeyModifiers.Alt);

        await Assert.That(tap.KeyUp(alt)).IsTrue();
    }

    [Test]
    public async Task A_key_pressed_while_Alt_is_held_makes_a_chord() {
        var tap = new AltTap();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);
        tap.KeyDown(Key.Left, KeyModifiers.Alt);

        await Assert.That(tap.KeyUp(Key.LeftAlt)).IsFalse();
    }

    /// AltGr reaches the app as Ctrl then Alt.
    [Test]
    public async Task Alt_pressed_with_Ctrl_held_is_never_a_tap() {
        var tap = new AltTap();
        tap.KeyDown(Key.LeftCtrl, KeyModifiers.Control);
        tap.KeyDown(Key.RightAlt, KeyModifiers.Control | KeyModifiers.Alt);

        await Assert.That(tap.KeyUp(Key.RightAlt)).IsFalse();
    }

    [Test]
    public async Task A_cancel_between_press_and_release_drops_the_tap() {
        var tap = new AltTap();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);
        tap.Cancel();

        await Assert.That(tap.KeyUp(Key.LeftAlt)).IsFalse();
    }

    [Test]
    public async Task A_release_completes_at_most_one_tap() {
        var tap = new AltTap();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);
        tap.KeyUp(Key.LeftAlt);

        await Assert.That(tap.KeyUp(Key.LeftAlt)).IsFalse();
    }

    /// Auto-repeat while Alt is held keeps reporting Alt.
    [Test]
    public async Task Holding_Alt_still_ends_in_a_tap() {
        var tap = new AltTap();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);

        await Assert.That(tap.KeyUp(Key.LeftAlt)).IsTrue();
    }

    /// Auto-repeat after a chord or a cancel must not rearm the hold it belongs to.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_repeat_does_not_rearm_a_cancelled_hold(bool byChord) {
        var tap = new AltTap();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);
        if (byChord) tap.KeyDown(Key.Left, KeyModifiers.Alt);
        else tap.Cancel();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);

        await Assert.That(tap.KeyUp(Key.LeftAlt)).IsFalse();
    }

    /// A release that went to another window never arrives, so the next press starts a new hold.
    [Test]
    public async Task After_a_reset_the_next_press_can_tap() {
        var tap = new AltTap();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);
        tap.Reset();
        tap.KeyDown(Key.LeftAlt, KeyModifiers.Alt);

        await Assert.That(tap.KeyUp(Key.LeftAlt)).IsTrue();
    }
}
