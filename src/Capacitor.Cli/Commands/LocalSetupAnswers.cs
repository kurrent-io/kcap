using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Commands;

/// <summary>
/// What <c>kcap setup --local</c> was answered for one installation, kept in <c>local-setup-v1.json</c> in the config
/// directory so a later run that cannot ask repeats those answers instead of taking every default. Bound to the
/// installation's id, so another installation starts with none; a missing or unreadable file is none too, which leaves
/// a run with the defaults it always had.
/// </summary>
/// <param name="Harnesses">Whether kcap was to be installed into the detected harnesses; null when nobody answered.</param>
/// <param name="LeftOut">What the last import left out; null when no import ran.</param>
/// <param name="Visibility">A <c>--default-visibility</c> a run was given; null when none was.</param>
/// <param name="Import">Whether past sessions were to be imported; null when nobody answered.</param>
sealed partial record LocalSetupAnswers(
    string InstallationId, bool? Harnesses = null, ImportSkip? LeftOut = null, string? Visibility = null, bool? Import = null) {
    public const string FileName = "local-setup-v1.json";

    /// <summary>The answers recorded for <paramref name="installationId"/>, or none.</summary>
    public static LocalSetupAnswers For(ConfigRoot config, string installationId) {
        try {
            var wire = JsonSerializer.Deserialize(File.ReadAllBytes(config.Path(FileName)), AnswersJsonContext.Default.Wire);

            return wire?.InstallationId == installationId ? FromWire(installationId, wire) : new(installationId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return new(installationId);
        }
    }

    /// <summary>Best-effort: a failed write costs the next unattended run these answers, never this setup.</summary>
    public bool Save(ConfigRoot config) {
        try {
            Directory.CreateDirectory(config.Directory);

            var bytes = JsonSerializer.SerializeToUtf8Bytes(ToWire(), AnswersJsonContext.Default.Wire);
            AtomicFile.Replace(config.Path(FileName), bytes, OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite,
                followLink: false);

            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static LocalSetupAnswers FromWire(string installationId, Wire wire) {
        var repos = new List<(string Owner, string Name)>();

        foreach (var repo in wire.LeftOut?.Repos ?? []) {
            if (repo?.Split('/', 2) is [{ Length: > 0 } owner, { Length: > 0 } name]) repos.Add((owner, name));
        }

        var leftOut = wire.LeftOut is null ? null : new ImportSkip(repos, [.. (wire.LeftOut.Folders ?? []).OfType<string>().Where(f => f.Length > 0)]);

        var visibility = wire.Visibility?.ToLowerInvariant();

        return new(installationId, wire.Harnesses, leftOut, visibility is not null && AppConfig.ValidVisibilities.Contains(visibility) ? visibility : null,
            wire.Import);
    }

    Wire ToWire() =>
        new() {
            InstallationId = InstallationId,
            Harnesses      = Harnesses,
            LeftOut        = LeftOut is null ? null : new() { Repos = [.. LeftOut.Repos.Select(r => $"{r.Owner}/{r.Name}")], Folders = [.. LeftOut.Folders] },
            Visibility     = Visibility,
            Import         = Import,
        };

    internal sealed class Wire {
        public string? InstallationId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Harnesses { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LeftOutWire? LeftOut { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Visibility { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Import { get; set; }
    }

    internal sealed class LeftOutWire {
        public List<string?>? Repos   { get; set; }
        public List<string?>? Folders { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(Wire))]
    internal partial class AnswersJsonContext : JsonSerializerContext;
}
