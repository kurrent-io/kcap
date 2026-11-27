using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Capacitor.App.ViewModels.Inbox;
using Lucide.Avalonia;

namespace Capacitor.App.Views.Inbox;

public static class InboxTileConverters {
    public static readonly IValueConverter IsWarning = new FuncValueConverter<InboxTile, bool>(t => t == InboxTile.Warning);

    public static readonly IValueConverter Icon = new FuncValueConverter<InboxTile, LucideIconKind>(t => t switch {
        InboxTile.Warning => LucideIconKind.TriangleAlert,
        InboxTile.Update  => LucideIconKind.ArrowDownToLine,
        _                 => LucideIconKind.Info,
    });

    public static readonly IValueConverter Brush = new FuncValueConverter<InboxTile, IBrush?>(t =>
        Application.Current?.FindResource(t == InboxTile.Warning ? "KcapWarningBrush" : "KcapInfoBrush") as IBrush);

    public static readonly IValueConverter LinkIcon = new FuncValueConverter<bool, LucideIconKind>(external =>
        external ? LucideIconKind.ExternalLink : LucideIconKind.ArrowRight);

    // A read card recedes; its text stays legible.
    public static readonly IValueConverter ReadOpacity = new FuncValueConverter<bool, double>(unread => unread ? 1.0 : 0.75);
}
