using Capacitor.App.ViewModels;

namespace Capacitor.App.Services;

public sealed record ChatReadingPosition(ChatMessageKey? Key, double Within, string? Proof = null, string? Source = null) {
    internal static readonly ChatReadingPosition Live = new(null, 0);

    internal bool IsValid => double.IsFinite(Within)
        && (Source is null or { Length: 32 })
        && (Key is null ? Within == 0 && Proof is null && Source is null : Key.Value.Line >= 0 && Proof is { Length: 32 });
}
