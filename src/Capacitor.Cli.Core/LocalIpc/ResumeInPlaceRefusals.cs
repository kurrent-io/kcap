namespace Capacitor.Cli.Core.LocalIpc;

/// Why the daemon will not resume a session in place; also the LaunchFailed reasons of a refused launch.
public static class ResumeInPlaceRefusals {
    public const string Invalid             = "resume_invalid";
    public const string UnsupportedVendor   = "resume_unsupported_vendor";
    public const string SourceActive        = "resume_source_active";
    public const string DirectoryMissing    = "resume_directory_missing";
    public const string DirectoryNotAllowed = "resume_directory_not_allowed";
    public const string StoreChanged        = "resume_store_changed";
    public const string Failed              = "resume_failed";
}
