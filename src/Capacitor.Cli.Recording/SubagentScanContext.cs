using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Recording;

/// <summary>What a vendor's subagent scan draws on from the recorder running it.</summary>
public sealed record SubagentScanContext(
        IRecorderHost Host, ConfigRoot Config, ProfileContext Profiles, ICapacitorHttpClient Http, TimeProvider Time, Action<string> Log) {
    public string Url => Profiles.Resolution.ServerUrl!;
}
