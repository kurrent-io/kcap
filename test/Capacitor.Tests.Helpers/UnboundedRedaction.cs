using System.Text.RegularExpressions;
using Capacitor.Cli.Recording;

namespace Capacitor.Tests.Helpers;

public static class UnboundedRedaction {
    /// <summary>The watcher patterns with no match deadline, so a loaded runner cannot trip the real regex timeout.</summary>
    public static readonly SecretPatterns Patterns = SecretRedactor.WatcherPatterns.WithMatchTimeout(Regex.InfiniteMatchTimeout);
}
