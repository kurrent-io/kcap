using Avalonia.Controls;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views;

public partial class SignInWindow : Window {
    public SignInWindow() {
        InitializeComponent();
        // Opening the window is the step's navigation and the user's Sign in click both, so it
        // starts the attempt: without the enter the status line stays blank.
        Opened += (_, _) => _ = (DataContext as SignInStepViewModel)?.EnterAndSignInAsync(CancellationToken.None);
    }
}
