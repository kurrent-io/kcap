namespace Capacitor.App.Services;

public sealed record UploadOutcome(UploadKind Kind, IReadOnlyList<string> Ids, string? Reason) {
    internal string FailureText => Kind == UploadKind.Rejected
        ? "The server couldn’t accept the files. Check them and try again."
        : "Couldn’t upload the files. Check your connection and try again.";

    public static UploadOutcome Unauthorized(string reason) => new(UploadKind.Unauthorized, [], reason);
    public static UploadOutcome Rejected(string reason) => new(UploadKind.Rejected, [], reason);
    public static UploadOutcome Unreachable(string reason) => new(UploadKind.Unreachable, [], reason);
}
