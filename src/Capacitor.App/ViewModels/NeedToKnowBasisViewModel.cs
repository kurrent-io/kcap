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

    // Opacity percent of the text colour; the defaults match the brushes App.axaml starts with.
    double _worthFade = 60;
    double _curiousFade = 40;

    public double WorthFade {
        get => _worthFade;
        set {
            this.RaiseAndSetIfChanged(ref _worthFade, Math.Round(value));
            NeedToKnowFades.Set(NeedToKnowFades.WorthKey, _worthFade);
        }
    }

    public double CuriousFade {
        get => _curiousFade;
        set {
            this.RaiseAndSetIfChanged(ref _curiousFade, Math.Round(value));
            NeedToKnowFades.Set(NeedToKnowFades.CuriousKey, _curiousFade);
        }
    }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetOnlyIfCuriousCommand { get; }

    public bool WorthKnowingNormal => _basis.WorthKnowing == NeedToKnowDisplay.Normal;
    public bool WorthKnowingMarked => _basis.WorthKnowing == NeedToKnowDisplay.Marked;
    public bool WorthKnowingFaded => _basis.WorthKnowing == NeedToKnowDisplay.Faded;
    public bool WorthKnowingCollapsed => _basis.WorthKnowing == NeedToKnowDisplay.Collapsed;
    public bool WorthKnowingHidden => _basis.WorthKnowing == NeedToKnowDisplay.Hidden;
    public bool OnlyIfCuriousNormal => _basis.OnlyIfCurious == NeedToKnowDisplay.Normal;
    public bool OnlyIfCuriousMarked => _basis.OnlyIfCurious == NeedToKnowDisplay.Marked;
    public bool OnlyIfCuriousFaded => _basis.OnlyIfCurious == NeedToKnowDisplay.Faded;
    public bool OnlyIfCuriousCollapsed => _basis.OnlyIfCurious == NeedToKnowDisplay.Collapsed;
    public bool OnlyIfCuriousHidden => _basis.OnlyIfCurious == NeedToKnowDisplay.Hidden;
    public bool OnlyIfCuriousCanBeNormal => _basis.WorthKnowing <= NeedToKnowDisplay.Normal;
    public bool OnlyIfCuriousCanBeMarked => _basis.WorthKnowing <= NeedToKnowDisplay.Marked;
    public bool OnlyIfCuriousCanBeFaded => _basis.WorthKnowing <= NeedToKnowDisplay.Faded;
    public bool OnlyIfCuriousCanBeCollapsed => _basis.WorthKnowing <= NeedToKnowDisplay.Collapsed;

    void Apply(NeedToKnowBasis basis) {
        _basis = basis;
        NeedToKnowBasis.Set(basis);
        foreach (var name in new[] {
                     nameof(WorthKnowingNormal), nameof(WorthKnowingMarked), nameof(WorthKnowingFaded),
                     nameof(WorthKnowingCollapsed), nameof(WorthKnowingHidden),
                     nameof(OnlyIfCuriousNormal), nameof(OnlyIfCuriousMarked), nameof(OnlyIfCuriousFaded),
                     nameof(OnlyIfCuriousCollapsed), nameof(OnlyIfCuriousHidden),
                     nameof(OnlyIfCuriousCanBeNormal), nameof(OnlyIfCuriousCanBeMarked),
                     nameof(OnlyIfCuriousCanBeFaded), nameof(OnlyIfCuriousCanBeCollapsed),
                 })
            this.RaisePropertyChanged(name);
    }
}
