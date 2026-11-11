using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap update --check</c> from the Windows app's CLI in <c>current\bin</c> reports the running version and never
/// asks npm. npm's stable here is older than any build, so a check that reached it would offer a beta user a downgrade.
/// </summary>
// Bare: the command writes to Console.
[NotInParallel]
public class UpdateCommandAppInstallTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    ServiceProvider? _sp;

    public void Dispose() => _sp?.Dispose();

    [Test]
    public async Task The_windows_app_cli_reports_its_own_version_without_asking_npm() {
        string[] appFiles = ["/Local/kcap/Update.exe", "/kcap/current/kcap-desktop.exe"];
        var kind = InstallProvenance.Detect("C:/Users/u/AppData/Local/kcap/current/bin/kcap.exe",
            p => appFiles.Any(f => p.Replace('\\', '/').EndsWith(f, StringComparison.Ordinal)));
        var npm = new AuthHttpScript(r => AuthHttp.Json(r.RequestUri!.AbsolutePath.EndsWith("/beta", StringComparison.Ordinal)
            ? """{"version":"999.0.0-beta.1"}"""
            : """{"version":"0.0.1"}"""));

        var profiles = Resolutions.Of(new Profile(), "app-update", null);
        var services = new ServiceCollection();
        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer("", Config.Root, profiles));
        services.AddCapacitorHttp(ProfileOverrides.None, MachineAuth.None);
        _sp = services.BuildServiceProvider();

        var command = new UpdateCommand(
            Config.Root, profiles, new NpmRegistryClient(new HttpClient(npm) { BaseAddress = new Uri("http://registry.test/") }),
            _sp.GetRequiredService<CapacitorServer>(), _sp.GetRequiredService<ICapacitorHttpClient>(), TimeProvider.System, kind: kind);

        using var output = ConsoleOutput.StartFullCapture();
        var exit = await command.HandleAsync(["--check"]);

        var json = JsonNode.Parse(output.GetCapturedOutput())!;

        await Assert.That(kind).IsEqualTo(InstallKind.App);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(json["latest"]?.GetValue<string>()).IsEqualTo(json["current"]?.GetValue<string>());
        await Assert.That(json["newer"]!.GetValue<bool>()).IsFalse();
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("app");
        await Assert.That(npm.Seen).IsEmpty();
    }
}
