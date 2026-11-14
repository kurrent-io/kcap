namespace Capacitor.Cli.Commands;

/// <summary>
/// Where a routed session stands once the server's watermark is known: its status, the file line an import
/// resumes from, and the SourceMeta the import reads back.
/// </summary>
internal readonly record struct WatermarkResolution(
    ImportCommand.ClassificationStatus    Status,
    int                                   ResumeFromLine,
    IReadOnlyDictionary<string, object?>? SourceMeta);
