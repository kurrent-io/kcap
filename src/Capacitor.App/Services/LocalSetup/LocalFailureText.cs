namespace Capacitor.App.Services.LocalSetup;

internal static class LocalFailureText {
    public static string For(string? code, string fallback) => code switch {
        "unsupported_platform" or "musl_unsupported" => "The local edition isn't available on this system. Choose another edition.",
        "socket_path_too_long" => "The local server's folder path is too long. Choose a folder with a shorter path.",
        "unsafe_root" => "kcap can't use this folder for local data. Choose a folder you own.",
        "registry_unreadable" or "config_unreadable" => "kcap couldn't read the local server settings. Check access to the local data folder and try again.",
        "other_server" => "Another server is using the local server's address. Stop it and try again.",
        "leftover_state" => "kcap found files from an earlier local setup. Check the existing installation before continuing.",
        "invalid_arguments" => "Check your setup choices and try again.",
        "bundle_not_published" => "A local server download isn't available for this version of kcap. Update kcap when a newer version is available, then try setup again.",
        "download_failed" => "The download didn't finish. Check your connection and try again.",
        "verification_failed" => "The download didn't pass its signature check. Try downloading it again.",
        "not_our_server" => "kcap couldn't confirm that the server at this address belongs to this installation. Check the local server before continuing.",
        "commit_failed" or "recovery_failed" => "kcap couldn't finish saving the local setup. Try again.",
        "cancelled" => "The operation was cancelled.",
        "server_missing" => "The local server is missing. Reinstall it to continue.",
        "service_missing" => "The local server is stopped. Start it from kcap and try again.",
        _ => fallback,
    };
}
