namespace Capacitor.Cli.LocalEdition;

/// <summary>What a move of the local server between its Scheduled Task and <c>kcap-service</c> tells the user.</summary>
/// <param name="Failed">The server the move restarted did not come back ready, so the command must not exit 0.</param>
sealed record LocalServerMove(string Message, bool Failed = false);
