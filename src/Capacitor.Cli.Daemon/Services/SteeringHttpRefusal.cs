namespace Capacitor.Cli.Daemon.Services;

/// <summary>A request the steering socket answers with <see cref="Status"/> instead of routing.</summary>
internal sealed class SteeringHttpRefusal(int status, string code) : Exception(code) {
    public int Status { get; } = status;

    /// <summary>The <c>error</c> the refusal's JSON body names.</summary>
    public string Code { get; } = code;
}
