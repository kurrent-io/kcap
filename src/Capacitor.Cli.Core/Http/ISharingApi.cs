namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/sharing</c>, keyed by the opaque object key an entity's own response carries — see
/// <see cref="ISessionsApi"/> for the contract every API interface in this namespace follows. Each write changes one grant.</summary>
public interface ISharingApi {
    Task<SharingRead<SharingDto>> GetAsync(string objectKey, CancellationToken ct = default);

    /// <summary>Audiences holding no grant yet that match <paramref name="query"/>; an empty one gives the server's suggestions.</summary>
    Task<SharingRead<SharingCandidatesDto>> CandidatesAsync(string objectKey, string? query, CancellationToken ct = default);

    /// <summary>Adds the audience's grant, or moves the one it holds.</summary>
    Task<SharingWriteResult> ShareAsync(string objectKey, string audienceKey, string relation, CancellationToken ct = default);

    /// <summary>Removing a grant the audience does not hold succeeds.</summary>
    Task<SharingWriteResult> UnshareAsync(string objectKey, string audienceKey, CancellationToken ct = default);
}
