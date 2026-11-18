namespace Capacitor.Cli.Core;

/// <summary>What <see cref="SessionEndRecord.TryClaim"/> leaves the caller to do about one run's session end.</summary>
public enum SessionEndTurn {
    /// <summary>Post it, then mark it posted.</summary>
    Yours,

    /// <summary>It is posted, or that run owes none.</summary>
    Done,

    /// <summary>Someone claimed it moments ago and may still be posting it. Ask again later.</summary>
    Pending,
}
