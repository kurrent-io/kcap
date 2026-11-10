namespace Capacitor.Cli.Recording;

/// <summary>How a synthesised session end went.</summary>
public enum SessionEndDelivery {
    /// <summary>The server took it, or it is spooled for the drain.</summary>
    Delivered,

    /// <summary>The server refused the payload, which drops as any refused hook post does: sent again, it is refused again.</summary>
    Rejected,

    /// <summary>Neither sent nor kept, so the end is still owed.</summary>
    Lost,
}
