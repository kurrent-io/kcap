namespace Capacitor.Cli.Commands;

static class PassItemKinds {
    /// <summary>The wire's item kind for a command-line item flag.</summary>
    public static string FromFlag(string flag) => flag switch {
        "--session"   => "session",
        "--work-item" => "work_item",
        "--page"      => "page",
        "--agent"     => "hosted_agent",
        _             => throw new ArgumentOutOfRangeException(nameof(flag), flag, null)
    };
}
