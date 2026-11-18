using System.Reactive;
using Capacitor.App.NeedToKnow;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The right bar's need-to-know basis controls. One instance, because the basis is process-wide.
public sealed class NeedToKnowBasisViewModel : ReactiveObject {
    public static NeedToKnowBasisViewModel Shared { get; } = new();

    NeedToKnowBasis _basis = NeedToKnowBasis.Current;

    NeedToKnowBasisViewModel() {
        SetWorthKnowingCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display => Apply(_basis with { WorthKnowing = display }));
        SetOnlyIfCuriousCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display => Apply(_basis with { OnlyIfCurious = display }));
    }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetWorthKnowingCommand { get; }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetOnlyIfCuriousCommand { get; }

    public bool WorthKnowingNormal => _basis.WorthKnowing == NeedToKnowDisplay.Normal;
    public bool WorthKnowingMarked => _basis.WorthKnowing == NeedToKnowDisplay.Marked;
    public bool WorthKnowingGreyed => _basis.WorthKnowing == NeedToKnowDisplay.Greyed;
    public bool WorthKnowingHidden => _basis.WorthKnowing == NeedToKnowDisplay.Hidden;
    public bool OnlyIfCuriousNormal => _basis.OnlyIfCurious == NeedToKnowDisplay.Normal;
    public bool OnlyIfCuriousMarked => _basis.OnlyIfCurious == NeedToKnowDisplay.Marked;
    public bool OnlyIfCuriousGreyed => _basis.OnlyIfCurious == NeedToKnowDisplay.Greyed;
    public bool OnlyIfCuriousHidden => _basis.OnlyIfCurious == NeedToKnowDisplay.Hidden;

    void Apply(NeedToKnowBasis basis) {
        _basis = basis;
        NeedToKnowBasis.Set(basis);
        foreach (var name in new[] {
                     nameof(WorthKnowingNormal), nameof(WorthKnowingMarked), nameof(WorthKnowingGreyed), nameof(WorthKnowingHidden),
                     nameof(OnlyIfCuriousNormal), nameof(OnlyIfCuriousMarked), nameof(OnlyIfCuriousGreyed), nameof(OnlyIfCuriousHidden),
                 })
            this.RaisePropertyChanged(name);
    }
}
