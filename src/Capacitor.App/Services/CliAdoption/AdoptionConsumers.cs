using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Processes;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>What still uses or names an install, from evidence read just before its files are deleted.</summary>
internal static class AdoptionConsumers {
    /// <summary>Null when nothing holds the install, otherwise <c>pid:&lt;n&gt;</c> for a process or
    /// <c>site:&lt;name&gt;</c> for a reference site. A harness session that started by the cutover read its plugin,
    /// hooks and MCP commands from the old install and may still load them, with no kcap process of its own running.</summary>
    internal static string? Holder(OtherCliInstall install, DateTime cutoverUtc, IReadOnlyList<ProcessFact> processes,
            IReadOnlyList<ReferenceSite> sites, HarnessRegistry harnesses, string bundleRoot) {
        var folders = install.OwnedEntries.Prepend(install.Root).ToList();
        var cutover = Utc(cutoverUtc);

        foreach (var process in processes) {
            if (RunsFrom(process, folders) || (Utc(process.StartedUtc) <= cutover && IsHarness(process, harnesses)))
                return $"pid:{process.Pid}";
        }

        var needles = Needles(folders);
        foreach (var site in sites) {
            if (Holds(site, needles, bundleRoot)) return $"site:{site.Name}";
        }

        return null;
    }

    // A time left in local time compares by its wall clock against a UTC one; an unspecified kind is taken as UTC.
    static DateTime Utc(DateTime time) => time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time;

    static bool RunsFrom(ProcessFact process, List<string> folders) =>
        Within(process.Executable, folders)
     || (process.Args is { } args && ProcessArgs.Script(args) is { } script && Path.IsPathRooted(script) && Within(script, folders));

    static bool Within(string? path, List<string> folders) =>
        path is not null && folders.Exists(folder => CanonicalPath.IsWithin(path, folder));

    // Both names: the vendor's CLI and the vendor itself, which is what its IDE or a retitled interpreter shows.
    static bool IsHarness(ProcessFact process, HarnessRegistry harnesses) {
        var names = Names(process).Where(n => n.Length > 0).ToList();

        return harnesses.Any(h => names.Exists(n =>
            (h.CliBinary.Length > 0 && AgentProcessNames.Matches(n, h.CliBinary)) || AgentProcessNames.Matches(n, h.VendorId)));
    }

    static IEnumerable<string> Names(ProcessFact process) {
        foreach (var name in AgentProcessNames.Candidates(process.Executable)) yield return name;
        if (process.Args is not { Count: > 0 } args) yield break;

        foreach (var name in AgentProcessNames.Candidates(args[0])) yield return name;
        if (ProcessArgs.Script(args) is { } script)
            foreach (var name in AgentProcessNames.Candidates(script)) yield return name;
        foreach (var name in ProcessArgs.ScriptNameCandidates(args)) yield return name;
    }

    // kcap's JSON writers escape non-ASCII, so a path holding any appears in a file only in its escaped form.
    static List<string> Needles(List<string> folders) =>
        [.. folders.SelectMany(f => new[] { f, CanonicalPath.Resolve(f) })
            .SelectMany(p => new[] { p, JsonEncodedText.Encode(p).Value })
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)];

    static bool Holds(ReferenceSite site, List<string> needles, string bundleRoot) {
        if (!site.Readable) return true;
        if (site.Name.StartsWith(AdoptionReferenceSites.ClaudePluginSite, StringComparison.Ordinal)
         && !(site.Text is { } loads && CanonicalPath.IsWithin(loads, bundleRoot))) return true;

        return site.Text is { } text && needles.Exists(n => text.Contains(n, StringComparison.Ordinal));
    }
}
