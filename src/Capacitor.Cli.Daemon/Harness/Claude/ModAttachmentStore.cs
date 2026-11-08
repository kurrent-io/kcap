using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>The steering state file: owner-only, in the daemon's owner-only state directory, replaced whole and flushed
/// before the rename, because a report it holds may be the only record left that a dispatch landed.</summary>
internal sealed partial class ModAttachmentStore(string path, ILogger logger) {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The saved state; empty when there is none, and when it can't be read, which is logged.</summary>
    public ModAttachmentFile Load() {
        try {
            if (!File.Exists(path)) return ModAttachmentFile.Empty;

            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), ModAttachmentJsonContext.Default.ModAttachmentFile);

            return file is { Attachments: not null, HandedOver: not null, Reports: not null, Replaced: not null, Stopped: not null }
                ? file
                : Unreadable("it is incomplete");
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            return Unreadable(ex.Message);
        }
    }

    ModAttachmentFile Unreadable(string why) {
        LogUnreadable(path, why);

        return ModAttachmentFile.Empty;
    }

    public bool TrySave(ModAttachmentFile file) {
        try {
            OwnerOnlyDirectory.Ensure(Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, JsonSerializer.SerializeToUtf8Bytes(file, ModAttachmentJsonContext.Default.ModAttachmentFile),
                OwnerOnly, followLink: false, durable: true);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            LogUnwritable(path, ex.Message);

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering state at {Path} can't be read, so it starts empty: {Why}")]
    partial void LogUnreadable(string path, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Steering state at {Path} could not be saved: {Why}")]
    partial void LogUnwritable(string path, string why);
}
