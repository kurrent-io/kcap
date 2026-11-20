namespace Capacitor.Models.Transcripts;

/// A projection that pairs records across lines in the vendor's own step order, which the file does not
/// always keep. The caller stable-sorts each batch by this key before projecting it; a line without a key
/// gets <see cref="long.MaxValue"/> and so keeps its received place after the ordered ones.
public interface IStepOrderedProjection {
    long StepOrder(string line);
}
