using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Pages;
using Capacitor.App.Views.Pages;

namespace Capacitor.App.Tests.Unit.Views.Pages;

public class FilterPickerSmokeTests {
    /// Opening the picker focuses its search, typing narrows the rows, and Enter picks the first match.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Search_narrows_the_choices_and_enter_picks_the_first_match() => AvaloniaSession.RunOnUiAsync(async () => {
        SessionsPageChoice any = new(null, "Any work item"), flaky = new("w1", "Investigate the flaky test"), icons = new("w2", "Refresh icons");
        var picker = new FilterPicker { Choices = [any, icons, flaky], Selected = any };
        var window = new Window { Content = picker, Width = 400, Height = 400 };
        try {
            window.Show();
            picker.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var search = TopLevel.GetTopLevel(picker)!.FocusManager!.GetFocusedElement() as TextBox;
            await Assert.That(search?.FindAncestorOfType<FlyoutPresenter>()).IsNotNull();
            search!.Text = "FLAKY";
            Dispatcher.UIThread.RunJobs();
            var rows = search.FindAncestorOfType<FlyoutPresenter>()!.GetVisualDescendants().OfType<Button>().ToList();
            await Assert.That(rows.Count).IsEqualTo(1);

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(picker.Selected).IsEqualTo(flaky);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });
}
