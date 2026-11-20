namespace Capacitor.Models.Transcripts;

public interface ILineSignalReader {
    /// <param name="agentId">Null for a root transcript. Titles and turn ends are read only from a root transcript.</param>
    LineSignals Read(string line, string sessionId, string? agentId);
}
