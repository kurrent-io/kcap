namespace Capacitor.Cli.Core;

/// <summary>The values of <see cref="ConfirmInputHandoverResponse.Outcome"/>, as the server spells them.</summary>
public static class InputHandoverOutcome {
    /// <summary>Pass it to the mod now.</summary>
    public const string HandedOver = "handed_over";

    /// <summary>Forget it: it is settled, or the server just rejected it.</summary>
    public const string Drop = "drop";

    /// <summary>Keep holding it and ask again the next time the mod is idle.</summary>
    public const string Hold = "hold";
}
