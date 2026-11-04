using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Persists <see cref="AdoptionRecord"/> under the config directory. A file that cannot be read as a
/// record loads as <see cref="AdoptionRecord.Empty"/> and stays on disk until the next <see cref="Update"/>
/// replaces it.</summary>
internal sealed class AdoptionRecordStore(ConfigRoot config) {
    internal const string FileName = "cli-adoption.json";

    public AdoptionRecord Load() {
        using var guard = config.AcquireLock(FileName);
        return Read();
    }

    public AdoptionRecord Update(Func<AdoptionRecord, AdoptionRecord> change) {
        Directory.CreateDirectory(config.Directory);
        using var guard = config.AcquireLock(FileName);
        var updated = change(Read());
        AtomicFile.Replace(
            config.Path(FileName),
            JsonSerializer.Serialize(updated, AdoptionRecordJsonContext.Default.AdoptionRecord),
            mode: UnixFileMode.UserRead | UnixFileMode.UserWrite, followLink: false, durable: true);
        return updated;
    }

    AdoptionRecord Read() {
        try {
            var text = File.ReadAllText(config.Path(FileName));
            return JsonSerializer.Deserialize(text, AdoptionRecordJsonContext.Default.AdoptionRecord) ?? AdoptionRecord.Empty;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            return AdoptionRecord.Empty;
        }
    }
}
