namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// Whether this CLI may call a model itself for a session recorded to <c>serverUrl</c>: a generated title, a what's-done
/// summary. Any other server is answered as before. A local installation promises that nothing leaves the machine
/// unless its owner set up background intelligence, so its sessions get model calls only once its server applied
/// <c>local_agent</c> or <c>provider</c>; an origin that was or may be an installation gets none.
/// </summary>
public sealed class LocalIntelligenceGate(ConfigRoot config, Func<LocalEndpoints> endpoints) {
    public const string NotSetUp = "background intelligence is not set up for this local installation";

    public static LocalIntelligenceGate For(ConfigRoot config, LocalRoot root) => new(config, () => LocalEndpoints.Read(root));

    public bool Allows(string? serverUrl) =>
        endpoints().Classify(serverUrl) switch {
            LocalEndpointClass.Ordinary                => true,
            LocalEndpointClass.Local(var installation) => LocalIntelligenceRecord.For(config, installation.Id).RunsModels,
            _                                          => false,
        };
}
