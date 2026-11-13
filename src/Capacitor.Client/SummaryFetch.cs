using Capacitor.Remote.Models;

namespace Capacitor.Client;

public sealed record SummaryFetch(ServerReadOutcome Outcome, ActivitySessionDto? Session);
