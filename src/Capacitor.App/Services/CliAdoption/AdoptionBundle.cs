namespace Capacitor.App.Services.CliAdoption;

/// <summary>The installed app bundle the takeover moves this Mac onto.</summary>
internal sealed record AdoptionBundle(string Root, string Cli, string Version, bool InSystemApplications) {
    public string Daemon => Path.Combine(Path.GetDirectoryName(Cli)!, "kcap-daemon");
}
