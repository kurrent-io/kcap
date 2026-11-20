namespace Capacitor.Cli.Core.Http;

public abstract record SharingWriteResult {
    public sealed record Applied : SharingWriteResult;

    /// <summary>Refused for a reason the server named.</summary>
    public sealed record Refused(SharingErrorDto Error) : SharingWriteResult;

    /// <summary>No object under that key, or none this profile may see: the server does not distinguish the two.</summary>
    public sealed record NotFound : SharingWriteResult;
}
