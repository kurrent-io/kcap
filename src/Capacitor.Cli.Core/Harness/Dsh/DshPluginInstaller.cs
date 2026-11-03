using System.Text;

namespace Capacitor.Cli.Core.Harness.Dsh;

/// <summary>
/// Installs kcap's recorder plugin for dsh. dsh has no shell hooks, so a Cordis plugin appends each
/// session event to <see cref="DshPaths.SessionJsonl"/> and runs <c>kcap hook --dsh</c> on session
/// start and disposal. It is registered in the home <c>cordis.patch.yml</c>, which applies to every
/// profile, inside a marker-delimited block so the user's own entries survive install and removal.
/// </summary>
public static class DshPluginInstaller {
    public const string MarkerFileName = ".kcap-extension-version";

    const string BeginMarker = "# --- kcap-dsh:begin (kcap plugin install --dsh) — do not edit inside ---";
    const string EndMarker   = "# --- kcap-dsh:end ---";

    /// <summary>No <c>engines</c> or dsh peer dependencies: dsh's compatibility check then has
    /// nothing to refuse.</summary>
    public const string ManifestContent =
        """
        {
          "name": "kcap-dsh",
          "version": "1.0.0",
          "private": true,
          "type": "module",
          "main": "index.mjs"
        }

        """;

    /// <summary>
    /// Dependency-free and fail-open: a <c>session/created</c> listener that throws vetoes the
    /// session, so every listener body is guarded. A resumed or forked session's earlier events are
    /// never re-emitted, so a session first seen mid-life backfills its own events from the log.
    /// The embedded token stream, the provider's replay state and per-step tool schemas are dropped
    /// before writing.
    /// </summary>
    public const string PluginContent =
        """
        // kcap recorder for dsh, installed by `kcap plugin install --dsh`.
        import { appendFileSync, existsSync, mkdirSync } from 'node:fs'
        import { join } from 'node:path'
        import { homedir } from 'node:os'
        import { spawn } from 'node:child_process'

        export const name = 'kcap'

        const DIR = join(homedir(), '.cache', 'kcap', 'dsh')
        const fileFor = id => join(DIR, `${id}.jsonl`)

        function append(id, records) {
          if (records.length === 0) return
          try {
            mkdirSync(DIR, { recursive: true })
            appendFileSync(fileFor(id), records.map(r => JSON.stringify(r) + '\n').join(''))
          } catch {}
        }

        function slim(event) {
          if (!event || event.type === 'assistant/attempt') return undefined
          if (event.type === 'assistant/message' && event.data) {
            const { stream, ...data } = event.data
            if (data.message?.source && 'replayState' in data.message.source) {
              const { replayState, ...source } = data.message.source
              data.message = { ...data.message, source }
            }
            return { ...event, data }
          }
          if (event.type === 'request/header' && event.data?.header?.tools) {
            const { tools, ...header } = event.data.header
            return { ...event, data: { ...event.data, header } }
          }
          return event
        }

        function hookArgs(header) {
          const args = []
          const cwd = header.cwd || process.cwd()
          if (cwd) args.push('--cwd', cwd)
          if (header.parentSession) args.push('--parent-session', header.parentSession)
          return args
        }

        // kcap is a .cmd shim on Windows, which only a shell can run.
        const quote = arg => `"${String(arg)}"`

        function runHook(id, event, extra) {
          try {
            const args = ['hook', '--dsh', '--event', event, '--session', id, '--file', fileFor(id),
              '--agent-pid', String(process.pid), ...extra]
            const child = process.platform === 'win32'
              ? spawn(['kcap', ...args].map(quote).join(' '),
                  { shell: true, stdio: 'ignore', detached: true, windowsHide: true })
              : spawn('kcap', args, { stdio: 'ignore', detached: true })
            child.on('error', () => {})
            child.unref()
          } catch {}
        }

        export function apply(ctx) {
          ctx.on('session/created', session => {
            try {
              const header = session.header ?? {}
              if (!existsSync(fileFor(session.id))) {
                const records = [{ $kcap: 'header', ...header }]
                try {
                  const from = session.inheritedEventCount ?? 0
                  const to = session.firstLiveSeq ?? 0
                  if (to > from && typeof session.snapshotEvents === 'function') {
                    for (const event of session.snapshotEvents(from, to)) {
                      const slimmed = slim(event)
                      if (slimmed) records.push(slimmed)
                    }
                  }
                } catch {}
                append(session.id, records)
              }
              runHook(session.id, 'session-start', hookArgs(header))
            } catch {}
          })
          ctx.on('session/event', (session, event) => {
            try {
              const slimmed = slim(event)
              if (slimmed) append(session.id, [slimmed])
            } catch {}
          })
          ctx.on('session/disposed', session => {
            try {
              append(session.id, [{ $kcap: 'disposed', id: session.id }])
              runHook(session.id, 'session-end', ['--reason', 'disposed', ...hookArgs(session.header ?? {})])
            } catch {}
          })
        }

        export default apply

        """;

    /// <summary>The plugin file is present and the home patch loads it.</summary>
    public static bool IsInstalled(DshPaths paths) =>
        File.Exists(paths.PluginFile) && IsRegistered(paths.CordisPatch);

    public static string? ReadMarker(DshPaths paths) {
        var marker = Path.Combine(paths.PluginDir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static bool Install(DshPaths paths) {
        try {
            Directory.CreateDirectory(paths.PluginDir);
            File.WriteAllText(paths.PluginFile, PluginContent);
            File.WriteAllText(paths.PluginManifest, ManifestContent);
            File.WriteAllText(Path.Combine(paths.PluginDir, MarkerFileName), CapacitorVersion.Current());

            if (!Register(paths.CordisPatch, paths.PluginFile)) return false;

            RemoveLegacy(paths);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Removes the plugin and every registration of it. True when anything was there.</summary>
    public static bool Remove(DshPaths paths) {
        var existed = Directory.Exists(paths.PluginDir) || IsRegistered(paths.CordisPatch) || File.Exists(paths.LegacyPluginFile);

        try { if (Directory.Exists(paths.PluginDir)) Directory.Delete(paths.PluginDir, recursive: true); }
        catch { /* best effort */ }

        Unregister(paths.CordisPatch);
        existed |= RemoveLegacy(paths);
        return existed;
    }

    /// <summary>The managed block that loads the plugin from an absolute <c>file://</c> URL.</summary>
    public static string BuildBlock(string pluginFile) =>
        BeginMarker + "\n"
      + "- insert:\n"
      + "    - id: kcap\n"
      + $"      name: '{new Uri(pluginFile).AbsoluteUri}'\n"
      + EndMarker;

    public static bool IsRegistered(string cordisPatch) {
        try { return File.Exists(cordisPatch) && File.ReadAllText(cordisPatch).Contains("kcap-dsh:begin", StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>Writes the managed block, replacing an earlier one and keeping the user's entries.</summary>
    public static bool Register(string cordisPatch, string pluginFile) {
        try {
            var lines = SplitLines(StripBlock(File.Exists(cordisPatch) ? File.ReadAllText(cordisPatch) : ""));
            var block = BuildBlock(pluginFile);

            // A block-style sequence cannot follow a flow `[]` in one document, so an empty list
            // keeps only its comments.
            var kept   = HasEntries(lines) ? lines : lines.Where(l => l.Trim() != "[]").ToArray();
            var prefix = string.Join("\n", kept).TrimEnd();

            Directory.CreateDirectory(Path.GetDirectoryName(cordisPatch)!);
            File.WriteAllText(cordisPatch, (prefix.Length == 0 ? block : prefix + "\n" + block) + "\n");
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Strips the managed block, leaving <c>[]</c> when no entry of the user's remains.</summary>
    public static bool Unregister(string cordisPatch) {
        try {
            if (!IsRegistered(cordisPatch)) return false;

            var lines = SplitLines(StripBlock(File.ReadAllText(cordisPatch)));
            string result;
            if (HasEntries(lines)) {
                result = string.Join("\n", lines).TrimEnd() + "\n";
            } else {
                var comments = string.Join("\n", lines.Where(l => l.Trim() != "[]")).TrimEnd();
                result = (comments.Length == 0 ? "[]" : comments + "\n[]") + "\n";
            }

            File.WriteAllText(cordisPatch, result);
            return true;
        } catch {
            return false;
        }
    }

    static bool RemoveLegacy(DshPaths paths) {
        var removed = false;
        try {
            if (File.Exists(paths.LegacyPluginFile)) {
                File.Delete(paths.LegacyPluginFile);
                removed = true;
            }
            var legacyMarker = Path.Combine(paths.Home, MarkerFileName);
            if (File.Exists(legacyMarker)) File.Delete(legacyMarker);

            if (Directory.Exists(paths.ProfilesDir)) {
                foreach (var profile in Directory.EnumerateDirectories(paths.ProfilesDir)) {
                    removed |= Unregister(Path.Combine(profile, "cordis.patch.yml"));
                }
            }
        } catch {
            // best effort
        }
        return removed;
    }

    static string[] SplitLines(string content) => content.Replace("\r\n", "\n").Split('\n');

    static bool HasEntries(IEnumerable<string> lines) =>
        lines.Any(l => {
            var t = l.Trim();
            return t.Length > 0 && t != "[]" && !t.StartsWith('#');
        });

    static string StripBlock(string content) {
        var sb      = new StringBuilder();
        var inBlock = false;
        foreach (var line in SplitLines(content)) {
            if (!inBlock && line.Contains("kcap-dsh:begin", StringComparison.Ordinal)) { inBlock = true; continue; }
            if (inBlock) {
                if (line.Contains("kcap-dsh:end", StringComparison.Ordinal)) inBlock = false;
                continue;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
