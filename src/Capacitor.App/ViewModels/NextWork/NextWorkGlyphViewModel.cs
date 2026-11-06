using Lucide.Avalonia;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>A glyph as the view draws it: the icon, the tint it takes and the badge dot, if any.</summary>
public sealed record NextWorkGlyphViewModel(LucideIconKind Icon, NextWorkGroup Tint, string? Badge) {
    public static NextWorkGlyphViewModel From(NextWorkGlyph g) => new(NextWorkIcons.For(g.Icon), g.Tint, g.Badge);

    public bool HasBadge   => Badge is not null;
    public bool BadgeWait  => Badge == "wait";
    public bool BadgeOk    => Badge == "ok";
    public bool BadgeStop  => Badge == "stop";
    public bool TintWaiting => Tint == NextWorkGroup.Waiting;
    public bool TintDone    => Tint == NextWorkGroup.Done;
    public bool TintFinish  => Tint == NextWorkGroup.Finish;
}
