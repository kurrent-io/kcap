using Capacitor.App.NeedToKnow;
using Capacitor.App.Services;
using Capacitor.App.Views;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The chat's progressive disclosure toggle. One instance, because the basis is process-wide.
/// On fades detail and footnote; off shows both normally.
public sealed class NeedToKnowBasisViewModel : ReactiveObject {
    public static NeedToKnowBasisViewModel Shared { get; } = new();

    static readonly NeedToKnowBasis On = new(NeedToKnowDisplay.Faded, NeedToKnowDisplay.Faded);
    static readonly NeedToKnowBasis Off = new(NeedToKnowDisplay.Normal, NeedToKnowDisplay.Normal);

    // Opacity percent of the text colour; the defaults match the brushes App.axaml starts with.
    const double DefaultDetailFade = 60;
    const double DefaultFootnoteFade = 30;
    const double MinFade = 5;
    const double MaxFade = 95;

    NeedToKnowBasis _basis = NeedToKnowBasis.Current;
    double _detailFade = DefaultDetailFade;
    double _footnoteFade = DefaultFootnoteFade;
    IAppStateStore? _store;

    NeedToKnowBasisViewModel() => ToggleCommand = ReactiveCommand.Create(() => { Enabled = !Enabled; });

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleCommand { get; }

    public bool Enabled {
        get => _basis != Off;
        set => Apply(value ? On : Off);
    }

    public double DetailFade {
        get => _detailFade;
        set => SetFade(NeedToKnowLevel.Detail, value);
    }

    public double FootnoteFade {
        get => _footnoteFade;
        set => SetFade(NeedToKnowLevel.Footnote, value);
    }

    /// Takes the saved basis, then saves every later change to the store. Run once the app's resources
    /// are loaded, since the fades replace brushes there. A saved basis that is not all normal reads as on.
    public void Restore(NeedToKnowPreferences? saved, IAppStateStore? store) {
        _store = null;
        var detail = Parse(saved?.Detail, NeedToKnowBasis.Default.Detail);
        var footnote = Parse(saved?.Footnote, NeedToKnowBasis.Default.Footnote);
        Apply(detail == NeedToKnowDisplay.Normal && footnote == NeedToKnowDisplay.Normal ? Off : On);
        SetFade(NeedToKnowLevel.Detail, saved?.DetailFade ?? DefaultDetailFade);
        SetFade(NeedToKnowLevel.Footnote, saved?.FootnoteFade ?? DefaultFootnoteFade);
        _store = store;
    }

    static NeedToKnowDisplay Parse(string? name, NeedToKnowDisplay fallback) =>
        Enum.TryParse<NeedToKnowDisplay>(name, ignoreCase: true, out var display) && Enum.IsDefined(display) ? display : fallback;

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
        this.RaisePropertyChanged(nameof(Enabled));
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
