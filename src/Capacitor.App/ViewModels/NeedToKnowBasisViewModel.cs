using System.Reactive;
using Capacitor.App.NeedToKnow;
using Capacitor.App.Views;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The right bar's need-to-know basis controls. One instance, because the basis is process-wide.
/// Only-if-curious can never be set more visible than worth-knowing: raising worth-knowing past it
/// carries it along, and the options above worth-knowing's are disabled.
public sealed class NeedToKnowBasisViewModel : ReactiveObject {
    public static NeedToKnowBasisViewModel Shared { get; } = new();

    NeedToKnowBasis _basis = NeedToKnowBasis.Current;

    NeedToKnowBasisViewModel() {
        SetWorthKnowingCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display =>
            Apply(new(display, _basis.OnlyIfCurious < display ? display : _basis.OnlyIfCurious)));
        SetOnlyIfCuriousCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display =>
            Apply(_basis with { OnlyIfCurious = display < _basis.WorthKnowing ? _basis.WorthKnowing : display }));
    }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetWorthKnowingCommand { get; }

    // Percent of the way from the canvas to the text colour; the defaults match the palette's greys.
    double _worthGrey = 60;
    double _curiousGrey = 40;

    public double WorthGrey {
        get => _worthGrey;
        set {
            this.RaiseAndSetIfChanged(ref _worthGrey, Math.Round(value));
            NeedToKnowGreys.Set(NeedToKnowGreys.WorthKey, _worthGrey);
        }
    }

    public double CuriousGrey {
        get => _curiousGrey;
        set {
            this.RaiseAndSetIfChanged(ref _curiousGrey, Math.Round(value));
            NeedToKnowGreys.Set(NeedToKnowGreys.CuriousKey, _curiousGrey);
        }
    }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetOnlyIfCuriousCommand { get; }

    public bool WorthKnowingNormal => _basis.WorthKnowing == NeedToKnowDisplay.Normal;
    public bool WorthKnowingMarked => _basis.WorthKnowing == NeedToKnowDisplay.Marked;
    public bool WorthKnowingGreyed => _basis.WorthKnowing == NeedToKnowDisplay.Greyed;
    public bool WorthKnowingCollapsed => _basis.WorthKnowing == NeedToKnowDisplay.Collapsed;
    public bool WorthKnowingHidden => _basis.WorthKnowing == NeedToKnowDisplay.Hidden;
    public bool OnlyIfCuriousNormal => _basis.OnlyIfCurious == NeedToKnowDisplay.Normal;
    public bool OnlyIfCuriousMarked => _basis.OnlyIfCurious == NeedToKnowDisplay.Marked;
    public bool OnlyIfCuriousGreyed => _basis.OnlyIfCurious == NeedToKnowDisplay.Greyed;
    public bool OnlyIfCuriousCollapsed => _basis.OnlyIfCurious == NeedToKnowDisplay.Collapsed;
    public bool OnlyIfCuriousHidden => _basis.OnlyIfCurious == NeedToKnowDisplay.Hidden;
    public bool OnlyIfCuriousCanBeNormal => _basis.WorthKnowing <= NeedToKnowDisplay.Normal;
    public bool OnlyIfCuriousCanBeMarked => _basis.WorthKnowing <= NeedToKnowDisplay.Marked;
    public bool OnlyIfCuriousCanBeGreyed => _basis.WorthKnowing <= NeedToKnowDisplay.Greyed;
    public bool OnlyIfCuriousCanBeCollapsed => _basis.WorthKnowing <= NeedToKnowDisplay.Collapsed;

    void Apply(NeedToKnowBasis basis) {
        _basis = basis;
        NeedToKnowBasis.Set(basis);
        foreach (var name in new[] {
                     nameof(WorthKnowingNormal), nameof(WorthKnowingMarked), nameof(WorthKnowingGreyed),
                     nameof(WorthKnowingCollapsed), nameof(WorthKnowingHidden),
                     nameof(OnlyIfCuriousNormal), nameof(OnlyIfCuriousMarked), nameof(OnlyIfCuriousGreyed),
                     nameof(OnlyIfCuriousCollapsed), nameof(OnlyIfCuriousHidden),
                     nameof(OnlyIfCuriousCanBeNormal), nameof(OnlyIfCuriousCanBeMarked),
                     nameof(OnlyIfCuriousCanBeGreyed), nameof(OnlyIfCuriousCanBeCollapsed),
                 })
            this.RaisePropertyChanged(name);
    }
}
