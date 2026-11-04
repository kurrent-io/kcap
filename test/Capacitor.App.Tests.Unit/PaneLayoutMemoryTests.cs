using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.Services;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class PaneLayoutMemoryTests {
    [Test]
    public async Task Missing_or_invalid_width_uses_the_minimum() {
        await Assert.That(PaneLayoutMemory.Resolve(null, 330)).IsEqualTo(330);
        await Assert.That(PaneLayoutMemory.Resolve(double.NaN, 330)).IsEqualTo(330);
    }

    [Test]
    public async Task Saved_width_is_raised_to_the_minimum_and_otherwise_kept() {
        await Assert.That(PaneLayoutMemory.Resolve(212, 330)).IsEqualTo(330);
        await Assert.That(PaneLayoutMemory.Resolve(5000, 330)).IsEqualTo(5000);
    }

    [Test]
    public async Task A_pane_rounded_just_below_its_minimum_does_not_hide() {
        await Assert.That(PaneLayout.HidesAt(329.6, 330)).IsFalse();
        await Assert.That(PaneLayout.HidesAt(300, 330)).IsTrue();
    }

    [Test]
    public async Task Every_change_is_persisted_and_restored() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        var panes = new PaneLayout();
        PaneLayoutMemory.Attach(panes, store);

        panes.RailWidth = 412;
        panes.ContextWidth = 500;
        panes.ContextCollapsed = true;
        panes.RailCollapsed = true;

        var state = await WaitForAsync(store, s => s.RailCollapsed);
        var restored = new PaneLayout();
        PaneLayoutMemory.Apply(restored, state);
        await Assert.That(restored.RailWidth).IsEqualTo(412);
        await Assert.That(restored.RailCollapsed).IsTrue();
        await Assert.That(restored.ContextWidth).IsEqualTo(500);
        await Assert.That(restored.ContextCollapsed).IsTrue();
    }

    /// A width saved on a larger screen, or a long drag, must leave the content beside it usable.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_rail_never_lays_out_wider_than_the_content_beside_it_can_spare() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.RailWidth = 5000;
            window.Show();
            window.UpdateLayout();
            await Assert.That(window.SessionRail.Bounds.Width).IsEqualTo(1400 - PaneLayout.ContentMinWidth);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_finished_rail_drag_updates_the_layout() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Show();
            DragRailTo(window, 412);
            await Assert.That(window.Panes.RailWidth).IsEqualTo(412);
            await Assert.That(window.Panes.RailCollapsed).IsFalse();
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rail_released_below_its_minimum_hides_and_reopens_at_the_minimum() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.RailWidth = 400;
            window.Show();
            DragRailTo(window, 200);
            await Assert.That(window.Panes.RailCollapsed).IsTrue();
            await Assert.That(window.SessionRail.Opacity).IsEqualTo(1);

            window.RailExpandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(window.SessionRail.Bounds.Width).IsEqualTo(PaneLayout.RailMinWidth);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Collapsing_the_rail_gives_its_whole_width_to_the_content_and_its_button_brings_it_back() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.RailWidth = 400;
            window.Show();

            window.SessionRail.FindControl<Button>("CollapseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(window.Panes.RailCollapsed).IsTrue();
            await Assert.That(window.SessionRail.IsVisible).IsFalse();
            await Assert.That(window.RailSplitter.IsVisible).IsFalse();
            await Assert.That(window.ContentPane.Bounds.X).IsEqualTo(0);
            await Assert.That(window.ContentPane.Bounds.Width).IsEqualTo(1400);
            await Assert.That(window.RailExpandButton.IsEffectivelyVisible).IsTrue();
            // In the title-bar band, past the macOS window buttons.
            var button = window.RailExpandButton.Bounds;
            await Assert.That(button.Left).IsEqualTo(RailClearance.ButtonOrigin(1).X);
            await Assert.That(button.Bottom).IsLessThanOrEqualTo(44);
            await Assert.That(button.Right).IsLessThanOrEqualTo(RailClearance.Leading(1));

            window.RailExpandButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(window.SessionRail.Bounds.Width).IsEqualTo(400);
            await Assert.That(window.RailExpandButton.IsVisible).IsFalse();
            window.Close();
        });
    }

    /// The header must clear the floating button only while it shows, and give the space back after.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_header_clears_the_show_sidebar_button_while_the_rail_is_collapsed() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            var view = new WorkspaceView();
            window.ContentPane.Children.Insert(0, view);
            window.Show();
            window.UpdateLayout();
            var title = view.FindControl<SessionTitleEditor>("WorkspaceTitle")!;
            // Measured from the content pane, which the rail moves.
            double TitleLeft() => title.TranslatePoint(default, window.ContentPane)!.Value.X;
            var expandedLeft = TitleLeft();

            window.Panes.RailCollapsed = true;
            window.UpdateLayout();
            var button = window.RailExpandButton;
            await Assert.That(TitleLeft()).IsGreaterThanOrEqualTo(button.Bounds.Right);

            window.Panes.RailCollapsed = false;
            window.UpdateLayout();
            await Assert.That(TitleLeft()).IsEqualTo(expandedLeft);
            window.Close();
        });
    }

    /// A wide saved width or a long drag must leave the session beside the details pane usable.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_details_pane_never_lays_out_wider_than_the_session_beside_it_can_spare() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.RailCollapsed = true;
            window.Panes.ContextWidth = 5000;
            var view = new WorkspaceView();
            window.ContentPane.Children.Add(view);
            window.Show();
            window.UpdateLayout();
            var pane = view.FindControl<WorkContextView>("WorkContextHost")!;
            await Assert.That(pane.Bounds.Width).IsEqualTo(view.Bounds.Width - PaneLayout.SessionMinWidth);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_details_pane_hides_from_its_own_header_and_shows_from_the_session_header() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.ContextWidth = 450;
            var view = new WorkspaceView();
            window.ContentPane.Children.Add(view);
            window.Show();
            window.UpdateLayout();
            var pane = view.FindControl<WorkContextView>("WorkContextHost")!;
            var show = view.FindControl<Button>("ContextShowButton")!;
            await Assert.That(pane.Bounds.Width).IsEqualTo(450);
            await Assert.That(show.IsVisible).IsFalse();

            pane.HideButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(window.Panes.ContextCollapsed).IsTrue();
            await Assert.That(pane.IsVisible).IsFalse();
            await Assert.That(show.IsVisible).IsTrue();

            show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(pane.Bounds.Width).IsEqualTo(450);
            window.Close();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_details_pane_released_below_its_minimum_hides_and_reopens_at_the_minimum() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { Width = 1400, Height = 800 };
            window.Panes.ContextWidth = 450;
            var view = new WorkspaceView();
            window.ContentPane.Children.Add(view);
            window.Show();
            view.SplitGrid.ColumnDefinitions[1].Width = new GridLength(150);
            window.UpdateLayout();
            view.ContextSplitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(300, 0) });
            await Assert.That(window.Panes.ContextCollapsed).IsTrue();

            view.FindControl<Button>("ContextShowButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            await Assert.That(view.FindControl<WorkContextView>("WorkContextHost")!.Bounds.Width).IsEqualTo(PaneLayout.ContextMinWidth);
            window.Close();
        });
    }

    static void DragRailTo(MainWindow window, double width) {
        window.SessionsSurface.ColumnDefinitions[0].Width = new GridLength(width);
        window.UpdateLayout();
        window.RailSplitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = default });
    }

    // Saves are fire-and-forget.
    static async Task<AppState> WaitForAsync(AppStateStore store, Func<AppState, bool> done) {
        for (var i = 0; i < 100; i++) {
            var state = await store.LoadAsync();
            if (done(state)) return state;
            await Task.Delay(20);
        }

        return await store.LoadAsync();
    }
}
