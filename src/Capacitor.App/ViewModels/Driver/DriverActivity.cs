namespace Capacitor.App.ViewModels.Driver;

/// <summary>What the live sessions on a part are doing right now. Never stored: it is read off the agent directory.
/// Ordered so the strongest wins when several sessions sit on one part.</summary>
public enum DriverActivity {
    None,
    Idle,
    Working,
    WaitingOnYou
}
