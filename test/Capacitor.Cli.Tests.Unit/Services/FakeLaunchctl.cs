using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>launchd for one user's jobs, as launchctl shows it: bootstrap loads the plist on disk and runs it,
/// as kcap's units run at load, and refuses a label that is still loaded; bootout unloads; kickstart runs a
/// loaded job. Every call is recorded.</summary>
sealed class FakeLaunchctl(UserHome home) {
    readonly Dictionary<string, (string Program, bool Running)> _jobs = new(StringComparer.Ordinal);

    public List<string[]> Calls { get; } = [];

    /// <summary>The program each successful bootstrap loaded, in order.</summary>
    public List<string> Bootstrapped { get; } = [];

    /// <summary>How many bootstraps fail before the next one succeeds.</summary>
    public int FailingBootstraps { get; set; }

    /// <summary>Every bootout fails and leaves the job loaded.</summary>
    public bool BootoutFails { get; set; }

    public void Load(string id, string program, bool running = true) => _jobs[LaunchdUnit.Label(id)] = (program, running);

    /// <summary>The daemon exits, as it does once it accepts a stop; the job stays loaded.</summary>
    public void Exit(string id) => _jobs[LaunchdUnit.Label(id)] = _jobs[LaunchdUnit.Label(id)] with { Running = false };

    public bool IsLoaded(string id) => _jobs.ContainsKey(LaunchdUnit.Label(id));
    public bool IsRunning(string id) => _jobs.TryGetValue(LaunchdUnit.Label(id), out var job) && job.Running;
    public string? Program(string id) => _jobs.TryGetValue(LaunchdUnit.Label(id), out var job) ? job.Program : null;

    public string[] Verbs => [.. Calls.Select(c => c[0])];

    public LaunchdServiceManager Manager() => new(home, TimeProvider.System,
        writeUnit: (path, content, _) => File.WriteAllText(path, content),
        runProcess: (tool, args) => {
            var (exit, stdout, stderr, _) = Run(tool, args, TimeSpan.Zero);
            return (exit, stdout, stderr);
        },
        runBounded: Run);

    (int, string, string, bool) Run(string tool, string[] args, TimeSpan timeout) {
        Calls.Add(args);

        switch (args) {
            case ["print", var target]:
                return _jobs.TryGetValue(LabelOf(target), out var job)
                    ? (0, $"{target} = {{\n\tstate = {(job.Running ? "running" : "not running")}\n\tprogram = {job.Program}\n\tspawn type = daemon (3)\n}}\n", "", false)
                    : (113, "", $"Could not find service \"{LabelOf(target)}\" in domain for user gui: 501", false);
            case ["bootout", _] when BootoutFails:
                return (5, "", "Boot-out failed: 5: Input/output error", false);
            case ["bootout", var target]:
                return _jobs.Remove(LabelOf(target)) ? (0, "", "", false) : (3, "", "Boot-out failed: 3: No such process", false);
            case ["bootstrap", _, var plist]:
                var label = Path.GetFileNameWithoutExtension(plist);
                if (_jobs.ContainsKey(label)) return (5, "", "Bootstrap failed: 5: Input/output error", false);
                if (FailingBootstraps > 0) {
                    FailingBootstraps--;
                    return (5, "", "Bootstrap failed: 5: Input/output error", false);
                }

                var program = LaunchdPrint.Binary(File.ReadAllText(plist))!;
                Bootstrapped.Add(program);
                _jobs[label] = (program, true);
                return (0, "", "", false);
            case ["kickstart", var target]:
                if (!_jobs.TryGetValue(LabelOf(target), out var stopped)) return (113, "", "Could not find service", false);
                _jobs[LabelOf(target)] = stopped with { Running = true };
                return (0, "", "", false);
            default:
                return (0, "", "", false);
        }
    }

    static string LabelOf(string target) => target[(target.LastIndexOf('/') + 1)..];
}
