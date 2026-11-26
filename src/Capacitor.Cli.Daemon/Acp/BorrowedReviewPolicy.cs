using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// One vendor's borrowed-review capability, resolved for this platform. Every consumer (advertisement,
/// containment token, snapshot requirement, pre-spawn validation, argv) reads this, never the static
/// descriptor, so advertisement and spawn cannot disagree.
/// </summary>
/// <param name="ExtraBorrowedToolIds">Tool ids added to the exclusive <c>--available-tools</c>
/// allowlist for a borrowed-snapshot launch only.</param>
/// <param name="RequiresProcessSandbox">Set wherever the readable allowlist is granted: without an OS
/// read boundary, confidentiality would rest on the vendor asking permission for out-of-bounds paths.
/// See <see cref="BorrowedReviewSandbox"/>.</param>
internal sealed record ResolvedBorrowedReviewPolicy(
    bool                         Supported,
    AcpBorrowedReviewContainment Containment,
    IReadOnlyList<string>        ExtraBorrowedToolIds,
    bool                         RequiresProcessSandbox = false
) {
    /// <summary>A vendor with no platform-specific policy: whatever its descriptor declares, and no
    /// extra tool ids. This is every vendor except Copilot.</summary>
    public static ResolvedBorrowedReviewPolicy FromDescriptor(AcpVendorDescriptor descriptor) =>
        new(descriptor.SupportsBorrowedReviewFlow, descriptor.BorrowedReviewContainment, []);

    /// <summary>Not supported anywhere: no borrowed launch, no containment token, no extra tools.</summary>
    public static readonly ResolvedBorrowedReviewPolicy Unsupported =
        new(false, AcpBorrowedReviewContainment.None, []);
}

/// <summary>
/// The platforms where Copilot's borrowed-review tool surface is verified, and its read tools.
/// <para>An exclusive allowlist, not a deny-list: <c>--deny-tool=write</c> does not cover Copilot's
/// create/edit tool, whose out-of-tree write raises a permission request the unattended policy would
/// approve. With only read tools available, no write or exec tool exists to ask for. Read containment
/// is the sandbox's job (<see cref="BorrowedReviewSandbox"/>).</para>
/// <para>Keyed on OS and architecture, since tool ids and path-trust semantics were verified on one
/// platform. Never keyed on vendor version, which auto-updates under a running daemon.</para>
/// </summary>
internal static class CopilotBorrowedReviewPolicy {
    /// <summary>The read tools a borrowed Copilot reviewer may use besides the flow-result channel.
    /// Immutable because it is the allowlist: a writable static could hand every later reviewer a
    /// shell.</summary>
    internal static readonly ImmutableArray<string> ReadToolIds = ["glob", "grep", "view"];

    /// <summary>Pure: keyed on OS and architecture plus two host facts; anything unverified fails
    /// closed.</summary>
    /// <param name="sandboxAvailable">A host that cannot enforce the read boundary is unsupported; the
    /// readable allowlist and the sandbox ship together.</param>
    /// <param name="authBrokerAvailable">A thunk, because answering may run an operator-configured command,
    /// which short-circuiting skips on platforms that cannot support borrowed review. Checked at
    /// advertisement so the server can reject the start with the <c>context-only</c> remedy instead of the
    /// flow dying at spawn.</param>
    internal static ResolvedBorrowedReviewPolicy Resolve(
            OSPlatform os, Architecture arch, bool sandboxAvailable, Func<bool> authBrokerAvailable) =>
        os == OSPlatform.OSX && arch == Architecture.Arm64 && sandboxAvailable && authBrokerAvailable()
            ? new(true, AcpBorrowedReviewContainment.IndependentSnapshot, ReadToolIds,
                  RequiresProcessSandbox: true)
            : ResolvedBorrowedReviewPolicy.Unsupported;

    /// <summary>
    /// This machine's entry. The sandbox grants no read of <c>~/.copilot</c>, <c>~/Library/Keychains</c>,
    /// <c>/Library</c> or <c>/opt/homebrew</c>: the launch uses a per-launch <c>HOME</c>/<c>TMPDIR</c>,
    /// <see cref="BorrowedReviewAuthBroker">brokered authentication</see> and
    /// <see cref="BorrowedReviewRuntimeRoots">runtime roots derived from the binary</see>, each pinned by an
    /// enforcement test running a real process that never asks permission. When unsupported, the server
    /// answers <c>vendor_containment_unreadable</c> with the <c>context-only</c> remedy.
    /// </summary>
    internal static ResolvedBorrowedReviewPolicy Current { get; } =
        Resolve(CurrentOs(), RuntimeInformation.ProcessArchitecture,
                BorrowedReviewSandbox.Available, () => BorrowedReviewAuthBroker.Available);

    static OSPlatform CurrentOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? OSPlatform.OSX
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OSPlatform.Linux
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
        : OSPlatform.Create("UNKNOWN");
}
