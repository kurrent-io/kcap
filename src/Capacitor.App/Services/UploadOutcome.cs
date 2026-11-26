namespace Capacitor.App.Services;

public sealed record UploadOutcome(UploadKind Kind, IReadOnlyList<string> Ids, string? Reason) {
    internal const string TooLarge = "payload_too_large";
    internal const string UnsupportedType = "unsupported_media_type";
    internal const string Forbidden = "upload_forbidden";
    internal const string RateLimited = "upload_rate_limited";
    internal const string ServerFailed = "upload_server_failed";

    internal string FailureText => Reason switch {
        TooLarge => "The files are too large to upload together. Try fewer or smaller files.",
        UnsupportedType => "The server doesn’t accept one of these file types. Remove it or try a different format.",
        Forbidden => "The server didn’t allow this upload. Check your workspace permissions or ask an administrator.",
        RateLimited => "The server is limiting uploads. Wait a moment and try again.",
        ServerFailed => "The server couldn’t finish the upload. Wait a moment and try again.",
        ServerAttachmentUploader.UnexpectedResponse => "The server sent an unexpected reply to the upload. Try again, or contact support if it keeps happening.",
        _ when Kind == UploadKind.Rejected => "The server couldn’t accept the files. Check them and try again.",
        _ => "Couldn’t upload the files. Check your connection and try again.",
    };

    public static UploadOutcome Unauthorized(string reason) => new(UploadKind.Unauthorized, [], reason);
    public static UploadOutcome Rejected(string reason) => new(UploadKind.Rejected, [], reason);
    public static UploadOutcome Unreachable(string reason) => new(UploadKind.Unreachable, [], reason);
}
