using System.Reactive;
using Capacitor.App.NeedToKnow;
using Capacitor.App.Views;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The right bar's need-to-know basis controls. One instance, because the basis is process-wide.
/// Footnote can never be set more visible than detail: raising detail past it
/// carries it along, and the options above detail's are disabled.
public sealed class NeedToKnowBasisViewModel : ReactiveObject {
    public static NeedToKnowBasisViewModel Shared { get; } = new();

    NeedToKnowBasis _basis = NeedToKnowBasis.Current;

    NeedToKnowBasisViewModel() {
        SetDetailCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display =>
            Apply(new(display, _basis.Footnote < display ? display : _basis.Footnote)));
        SetFootnoteCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display =>
            Apply(_basis with { Footnote = display < _basis.Detail ? _basis.Detail : display }));
    }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetDetailCommand { get; }

    // Opacity percent of the text colour; the defaults match the brushes App.axaml starts with.
    double _detailFade = 60;
    double _footnoteFade = 40;

    public double DetailFade {
        get => _detailFade;
        set {
            this.RaiseAndSetIfChanged(ref _detailFade, Math.Round(value));
            NeedToKnowFades.Set(NeedToKnowFades.DetailKey, _detailFade);
        }
    }

    public double FootnoteFade {
        get => _footnoteFade;
        set {
            this.RaiseAndSetIfChanged(ref _footnoteFade, Math.Round(value));
            NeedToKnowFades.Set(NeedToKnowFades.FootnoteKey, _footnoteFade);
        }
    }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetFootnoteCommand { get; }

    public bool DetailNormal => _basis.Detail == NeedToKnowDisplay.Normal;
    public bool DetailFaded => _basis.Detail == NeedToKnowDisplay.Faded;
    public bool DetailHidden => _basis.Detail == NeedToKnowDisplay.Hidden;
    public bool FootnoteNormal => _basis.Footnote == NeedToKnowDisplay.Normal;
    public bool FootnoteFaded => _basis.Footnote == NeedToKnowDisplay.Faded;
    public bool FootnoteHidden => _basis.Footnote == NeedToKnowDisplay.Hidden;
    public bool FootnoteCanBeNormal => _basis.Detail <= NeedToKnowDisplay.Normal;
    public bool FootnoteCanBeFaded => _basis.Detail <= NeedToKnowDisplay.Faded;

    void Apply(NeedToKnowBasis basis) {
        _basis = basis;
        NeedToKnowBasis.Set(basis);
        foreach (var name in new[] {
                     nameof(DetailNormal), nameof(DetailFaded),
                     nameof(DetailHidden),
                     nameof(FootnoteNormal), nameof(FootnoteFaded),
                     nameof(FootnoteHidden),
                     nameof(FootnoteCanBeNormal),
                     nameof(FootnoteCanBeFaded),
                 })
            this.RaisePropertyChanged(name);
    }
}
