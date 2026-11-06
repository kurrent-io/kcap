namespace Capacitor.App.ViewModels.NextWork;

/// <summary>One thing to do: the engine's row, what it asks for, and its loose ends. <see cref="Folded"/> are loose-end
/// rows that named this item or repeated one of its ends, listed under it rather than as rows of their own;
/// <see cref="Same"/> are rows for a loose end the item's own receipts already list, so they are shown once.</summary>
public sealed record NextWorkFocusItem(
    NextWorkRow                     Row,
    NextWorkFocusKind               Kind,
    IReadOnlyList<NextWorkFocusEnd> Ends,
    IReadOnlyList<NextWorkRow>      Folded,
    IReadOnlyList<NextWorkRow>      Same);
