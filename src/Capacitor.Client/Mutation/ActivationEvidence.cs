namespace Capacitor.Client.Mutation;

/// What an activating request's outcome proves about <c>active_profile</c>. Only <see cref="Activated"/> is proof:
/// the CLI prints its line after the write, so a missing line never proves the write did not happen.
public enum ActivationEvidence {
    /// The CLI's stdout carried <c>activated=&lt;profile&gt;</c>.
    Activated,

    /// The request never reached the CLI.
    NotActivated,

    /// The CLI ran and the outcome does not say: a timeout, a kill, or an exit without the line.
    Unknown,
}
