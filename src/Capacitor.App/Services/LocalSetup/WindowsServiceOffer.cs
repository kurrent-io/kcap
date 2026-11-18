namespace Capacitor.App.Services.LocalSetup;

/// How the install page offers running kcap as a Windows service.
public enum WindowsServiceOffer {
    /// Not Windows, or a current service already runs kcap: the install uses it without asking.
    Hidden,
    /// Offered ticked.
    On,
    /// Offered unticked, so the default install raises no administrator prompt.
    Off,
}
