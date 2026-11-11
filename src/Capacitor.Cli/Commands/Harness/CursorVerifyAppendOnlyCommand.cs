using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Recording.Harness.Cursor;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Sample live transcript lengths and prefix hashes over a bounded interval, then check every
/// sample pair for rewriting. Keep this diagnostic internal; runtime rewrite protection remains
/// necessary even when a sampled transcript appears append-only; <see cref="CursorRewriteGuard"/> enforces it.
/// </summary>
public static class CursorVerifyAppendOnlyCommand {
    public static async Task<int> RunAsync(string[] args, TimeProvider time, CancellationToken ct = default) {
        var path = GetArg(args, "--path");

        if (string.IsNullOrEmpty(path)) {
            await Console.Error.WriteLineAsync(
                "Usage: kcap cursor-verify-appendonly --path <file> [--duration-seconds 60] [--interval-seconds 1]");

            return 1;
        }

        if (!File.Exists(path)) {
            await Console.Error.WriteLineAsync($"File not found: {path}");

            return 1;
        }

        var durationSeconds = ParseIntArg(args, "--duration-seconds", 60);
        var intervalSeconds = ParseIntArg(args, "--interval-seconds", 1);
        var deadline         = time.GetUtcNow().AddSeconds(durationSeconds);

        var samples  = new List<CursorAppendOnlyProbe.Sample>();
        var failures = new List<string>();

        while (time.GetUtcNow() < deadline && !ct.IsCancellationRequested) {
            byte[] bytes;

            try {
                bytes = await File.ReadAllBytesAsync(path, ct);
            } catch (IOException) {
                // Racing a concurrent writer — skip this tick rather than fail the whole run.
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), time, ct);

                continue;
            }

            var sample = new CursorAppendOnlyProbe.Sample(bytes.LongLength, CursorAppendOnlyProbe.Sha256Hex(bytes));

            for (var i = 0; i < samples.Count; i++) {
                if (!CursorAppendOnlyProbe.PrefixStable(samples[i], sample, bytes)) {
                    failures.Add(
                        $"sample #{i} (length {samples[i].Length}) is not a stable prefix of sample #{samples.Count} (length {sample.Length})");
                }
            }

            samples.Add(sample);

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), time, ct);
        }

        await Console.Out.WriteLineAsync(
            $"cursor-verify-appendonly: {samples.Count} samples of '{path}' over {durationSeconds}s (interval {intervalSeconds}s)");

        if (failures.Count == 0) {
            await Console.Out.WriteLineAsync("PASS — append-only held across every sampled pair.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"FAIL — {failures.Count} prefix-stability violation(s):");

        foreach (var failure in failures) {
            await Console.Out.WriteLineAsync($"  {failure}");
        }

        return 1;
    }

    static int ParseIntArg(string[] args, string flag, int defaultValue) {
        var raw = GetArg(args, flag);

        return raw is not null && int.TryParse(raw, out var value) ? value : defaultValue;
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
