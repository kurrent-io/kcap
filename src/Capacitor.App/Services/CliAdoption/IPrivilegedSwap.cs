namespace Capacitor.App.Services.CliAdoption;

/// <summary>One administrator prompt and the root helper behind it; see <see cref="PrivilegedSwap"/>.</summary>
internal interface IPrivilegedSwap {
    Task<PrivilegedResult> RunAsync(
        string installRoot, PrivilegedSpec spec, string prompt, Func<CancellationToken, Task<bool>> verify, CancellationToken ct);
}
