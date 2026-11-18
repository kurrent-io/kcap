using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Setup.Intelligence;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The background intelligence mode one installation's local server last applied, kept in
/// <c>local-intelligence-v1.json</c> in the config directory because the server cannot be asked for it. Bound to the
/// installation's id; a missing or unreadable file, or one for another installation, reads as no mode.
/// </summary>
/// <param name="Mode">One of <see cref="IntelligenceModes"/>; null when no answer was applied.</param>
public sealed partial record LocalIntelligenceRecord(string InstallationId, string? Mode = null) {
    public const string FileName = "local-intelligence-v1.json";

    /// <summary>The server runs a model for its sessions, so this CLI may call one on their behalf too.</summary>
    public bool RunsModels => Mode is IntelligenceModes.LocalAgent or IntelligenceModes.Provider;

    public static LocalIntelligenceRecord For(ConfigRoot config, string installationId) {
        try {
            var wire = JsonSerializer.Deserialize(File.ReadAllBytes(config.Path(FileName)), RecordJsonContext.Default.Wire);

            return wire?.InstallationId == installationId ? new(installationId, wire.Mode) : new(installationId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return new(installationId);
        }
    }

    /// <summary>Best-effort: a failed write leaves the installation reading as not set up, which only withholds model calls.</summary>
    public bool Save(ConfigRoot config) {
        try {
            Directory.CreateDirectory(config.Directory);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Wire { InstallationId = InstallationId, Mode = Mode }, RecordJsonContext.Default.Wire);
            AtomicFile.Replace(config.Path(FileName), bytes, OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
                followLink: false);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    internal sealed class Wire {
        public string? InstallationId { get; set; }
        public string? Mode           { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(Wire))]
    internal partial class RecordJsonContext : JsonSerializerContext;
}
