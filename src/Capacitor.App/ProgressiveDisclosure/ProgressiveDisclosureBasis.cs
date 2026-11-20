using System.Reactive.Subjects;

namespace Capacitor.App.ProgressiveDisclosure;

/// How each level below must-know is shown. Must-know is always shown normally, and footnote is
/// never more visible than detail.
public sealed record ProgressiveDisclosureBasis(ProgressiveDisclosureDisplay Detail, ProgressiveDisclosureDisplay Footnote) {
    public static ProgressiveDisclosureBasis Default { get; } = new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Faded);

    static readonly BehaviorSubject<ProgressiveDisclosureBasis> Subject = new(Default);

    /// Process-wide because chat markdown is parsed through a shared pipeline; a change re-renders
    /// every chat view that holds a mark.
    public static ProgressiveDisclosureBasis Current => Subject.Value;

    /// The current basis, then each change.
    public static IObservable<ProgressiveDisclosureBasis> Changes => Subject;

    public static void Set(ProgressiveDisclosureBasis basis) {
        if (basis != Subject.Value) Subject.OnNext(basis);
    }

    public ProgressiveDisclosureDisplay For(ProgressiveDisclosureLevel level) => level switch {
        ProgressiveDisclosureLevel.Detail   => Detail,
        ProgressiveDisclosureLevel.Footnote => Footnote < Detail ? Detail : Footnote,
        _                        => ProgressiveDisclosureDisplay.Normal,
    };
}
