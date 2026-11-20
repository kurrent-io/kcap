namespace Capacitor.Cli.Core.Http;

public abstract record SharingRead<T> {
    public sealed record Found(T Value) : SharingRead<T>;

    /// <summary>No object under that key, or none this profile may see.</summary>
    public sealed record NotFound : SharingRead<T>;
}
