namespace Capacitor.App.Services;

/// What the app removes of itself once the CLI has removed everything else.
public interface IAppRemoval {
    /// The `kcap` links into this bundle that are still there afterwards.
    Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct);

    /// Logs what it cannot delete and returns false: nothing left here keeps kcap running.
    bool DeleteSupportFiles();

    /// False when the bundle is still in place.
    Task<bool> TrashBundleAsync(CancellationToken ct);

    void RevealBundle();

    /// What the user does when the app could not remove itself.
    string FinishByHand => "Drag kcap to the Trash";

    /// How a finished uninstall reports the app's own removal.
    string Trashed => "Moved the kcap app to the Trash";

    /// Where a finished uninstall says the app went.
    string RemovedSummary => "The app is in the Trash.";

    /// The progress line once the app's own removal has been done or set up.
    string RemovedLine => "Moved the app to the Trash.";

    /// Why the app is still in place, with no closing full stop.
    string NotRemoved => "The app could not be moved to the Trash";

    /// True when the app goes only after it exits, so a finished run cannot claim it is already gone.
    bool RemovedOnExit => false;
}
