using System.Reactive;
using Capacitor.App.NeedToKnow;
using Capacitor.App.Services;
using Capacitor.App.Views;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The right bar's need-to-know basis controls. One instance, because the basis is process-wide.
/// Footnote can never be set more visible than detail: raising detail past it
/// carries it along, and the options above detail's are disabled.
public sealed class NeedToKnowBasisViewModel : ReactiveObject {
    public static NeedToKnowBasisViewModel Shared { get; } = new();

    // Opacity percent of the text colour; the defaults match the brushes App.axaml starts with.
    const double DefaultDetailFade = 60;
    const double DefaultFootnoteFade = 30;
    const double MinFade = 5;
    const double MaxFade = 95;

    NeedToKnowBasis _basis = NeedToKnowBasis.Current;
    double _detailFade = DefaultDetailFade;
    double _footnoteFade = DefaultFootnoteFade;
    IAppStateStore? _store;

    NeedToKnowBasisViewModel() {
        SetDetailCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display =>
            Apply(new(display, _basis.Footnote < display ? display : _basis.Footnote)));
        SetFootnoteCommand = ReactiveCommand.Create<NeedToKnowDisplay>(display =>
            Apply(_basis with { Footnote = display < _basis.Detail ? _basis.Detail : display }));
    }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetDetailCommand { get; }

    public ReactiveCommand<NeedToKnowDisplay, Unit> SetFootnoteCommand { get; }

    public double DetailFade {
        get => _detailFade;
        set => SetFade(NeedToKnowLevel.Detail, value);
    }

    public double FootnoteFade {
        get => _footnoteFade;
        set => SetFade(NeedToKnowLevel.Footnote, value);
    }

    public bool DetailNormal => _basis.Detail == NeedToKnowDisplay.Normal;
    public bool DetailFaded => _basis.Detail == NeedToKnowDisplay.Faded;
    public bool DetailHidden => _basis.Detail == NeedToKnowDisplay.Hidden;
    public bool FootnoteNormal => _basis.Footnote == NeedToKnowDisplay.Normal;
    public bool FootnoteFaded => _basis.Footnote == NeedToKnowDisplay.Faded;
    public bool FootnoteHidden => _basis.Footnote == NeedToKnowDisplay.Hidden;
    public bool FootnoteCanBeNormal => _basis.Detail <= NeedToKnowDisplay.Normal;
    public bool FootnoteCanBeFaded => _basis.Detail <= NeedToKnowDisplay.Faded;

    /// Takes the saved basis, then saves every later change to the store. Run once the app's resources
    /// are loaded, since the fades replace brushes there.
    public void Restore(NeedToKnowPreferences? saved, IAppStateStore? store) {
        _store = null;
        var detail = Parse(saved?.Detail, NeedToKnowBasis.Default.Detail);
        Apply(new(detail, Max(detail, Parse(saved?.Footnote, NeedToKnowBasis.Default.Footnote))));
        SetFade(NeedToKnowLevel.Detail, saved?.DetailFade ?? DefaultDetailFade);
        SetFade(NeedToKnowLevel.Footnote, saved?.FootnoteFade ?? DefaultFootnoteFade);
        _store = store;
    }

    static NeedToKnowDisplay Parse(string? name, NeedToKnowDisplay fallback) =>
        Enum.TryParse<NeedToKnowDisplay>(name, ignoreCase: true, out var display) && Enum.IsDefined(display) ? display : fallback;

    static NeedToKnowDisplay Max(NeedToKnowDisplay a, NeedToKnowDisplay b) => a > b ? a : b;

    void SetFade(NeedToKnowLevel level, double value) {
        var percent = Math.Clamp(Math.Round(value), MinFade, MaxFade);
        if (percent == (level == NeedToKnowLevel.Footnote ? _footnoteFade : _detailFade)) return;
        if (level == NeedToKnowLevel.Footnote) this.RaiseAndSetIfChanged(ref _footnoteFade, percent, nameof(FootnoteFade));
        else this.RaiseAndSetIfChanged(ref _detailFade, percent, nameof(DetailFade));
        NeedToKnowFades.Set(level, percent);
        Save();
    }

    void Apply(NeedToKnowBasis basis) {
        _basis = basis;
        NeedToKnowBasis.Set(basis);
        foreach (var name in new[] {
                     nameof(DetailNormal), nameof(DetailFaded), nameof(DetailHidden),
                     nameof(FootnoteNormal), nameof(FootnoteFaded), nameof(FootnoteHidden),
                     nameof(FootnoteCanBeNormal), nameof(FootnoteCanBeFaded),
                 })
            this.RaisePropertyChanged(name);
        Save();
    }

    /// Each write stores what is current when it runs, so overlapping changes cannot leave an older one on disk.
    void Save() {
        if (_store is not { } store) return;
        Volatile.Write(ref _saved, new(_basis.Detail.ToString(), _basis.Footnote.ToString(), _detailFade, _footnoteFade));
        _ = store.UpdateAsync(state => state with { NeedToKnow = Volatile.Read(ref _saved) });
    }

    NeedToKnowPreferences? _saved;
}
