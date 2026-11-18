using System.Reactive.Subjects;

namespace Capacitor.App.NeedToKnow;

/// How each level below must-know is shown. Must-know is always shown normally, and only-if-curious is
/// never more visible than worth-knowing.
public sealed record NeedToKnowBasis(NeedToKnowDisplay WorthKnowing, NeedToKnowDisplay OnlyIfCurious) {
    public static NeedToKnowBasis Default { get; } = new(NeedToKnowDisplay.Normal, NeedToKnowDisplay.Faded);

    static readonly BehaviorSubject<NeedToKnowBasis> Subject = new(Default);

    /// Process-wide because chat markdown is parsed through a shared pipeline; a change re-renders
    /// every chat view that holds a mark.
    public static NeedToKnowBasis Current => Subject.Value;

    /// The current basis, then each change.
    public static IObservable<NeedToKnowBasis> Changes => Subject;

    public static void Set(NeedToKnowBasis basis) {
        if (basis != Subject.Value) Subject.OnNext(basis);
    }

    public NeedToKnowDisplay For(NeedToKnowLevel level) => level switch {
        NeedToKnowLevel.WorthKnowing  => WorthKnowing,
        NeedToKnowLevel.OnlyIfCurious => OnlyIfCurious < WorthKnowing ? WorthKnowing : OnlyIfCurious,
        _                             => NeedToKnowDisplay.Normal,
    };
}
