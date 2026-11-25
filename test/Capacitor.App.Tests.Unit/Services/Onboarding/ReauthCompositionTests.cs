using Capacitor.Cli.Core.Telemetry;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.App.Tests.Unit.Services.Onboarding;

/// The steady-state re-auth graph is the wizard's sign-in step alone, pinned to the server the
/// profile already targets — a re-auth must never repoint server_url at a different origin.
public class ReauthCompositionTests {
    const string ServerUrl = "https://acme.example";

    static AuthResult.Committed Committed() =>
        new("default", ServerUrl, AuthProvider.GitHubApp, "alice", []);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sign_in_runs_a_paste_intent_pinned_to_the_configured_server() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            ConnectIntent? seen = null;

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => (intent, _) => {
                    seen = intent;
                    return Task.FromResult<AuthResult>(Committed());
                },
                refreshAppState: true);

            await graph.SignIn.SignInCommand.Execute().ToTask();

            await Assert.That(seen).IsEqualTo(new ConnectIntent.Paste(ServerUrl));
            await Assert.That(graph.SignIn.Satisfied).IsTrue();
            // The dialog's own promise: it refreshes the app and closes, which the wizard does not.
            await Assert.That(graph.SignIn.StatusDetail).IsEqualTo("You're signed in. Refreshing…");
        });
    }

    /// A dialog for a profile the app does not run on closes without a refresh, so its success
    /// line must not promise one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_sign_in_that_does_not_refresh_the_app_promises_no_refresh() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => (_, _) => Task.FromResult<AuthResult>(Committed()),
                refreshAppState: false);

            await graph.SignIn.SignInCommand.Execute().ToTask();

            await Assert.That(graph.SignIn.Satisfied).IsTrue();
            await Assert.That(graph.SignIn.StatusDetail).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Closing_mid_attempt_cancels_it_and_quiesces() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => async (_, ct) => {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return Committed();
                },
                refreshAppState: true);

            var run = graph.SignIn.SignInCommand.Execute().ToTask();
            await started.Task;

            await graph.CloseAsync(CancellationToken.None);
            await run;

            await Assert.That(graph.SignIn.Satisfied).IsFalse();
            await graph.Auth.QuiescedAsync(); // completes only once no attempt is live
        });
    }

    /// The precondition is an optional null-defaulted parameter at every hop between here and
    /// LoginAsync, so a dropped one refuses nothing and no other test notices.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_commit_precondition_reaches_the_facade_spec() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            CommitPrecondition? captured = null;

            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                spec => {
                    captured = spec.Precondition;
                    return (_, _) => Task.FromResult<AuthResult>(new AuthResult.Cancelled());
                },
                refreshAppState: true,
                new CommitPrecondition.ExpectServer(ServerUrl));

            await graph.SignIn.SignInCommand.Execute().ToTask();

            await Assert.That(captured).IsTypeOf<CommitPrecondition.ExpectServer>();
            await Assert.That(((CommitPrecondition.ExpectServer)captured!).Url).IsEqualTo(ServerUrl);
        });
    }

    /// The dialog renders the SAME sign-in view the wizard shows, and opening it starts the attempt:
    /// the Sign in click that opened it is the only one the user makes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_dialog_hosts_the_wizard_sign_in_view_and_starts_signing_in_on_open() {
        var (buttonFound, status, runs) = await AvaloniaSession.DispatchAsync(() => {
            var runs = 0;
            var browser = new TaskCompletionSource<AuthResult>();
            using var config = new TempConfigRoot();
            var graph = ReauthComposition.Build(
                config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
                new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
                "default", ServerUrl,
                WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults),
                new ConsentFlipClaims(config.Root),
                new AppStateStore(config.PathTo("app-state.json")),
                new RecordingOpener(),
                TimeProvider.System,
                _ => (_, _) => { runs++; return browser.Task; },
                refreshAppState: true);

            var window = new SignInWindow { DataContext = graph.SignIn };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "SignInButton");
            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "SignInStatusText")?.Text;

            browser.SetResult(new AuthResult.Cancelled());
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (button is not null, text, runs);
        });

        await Assert.That(buttonFound).IsTrue();
        await Assert.That(runs).IsEqualTo(1);
        await Assert.That(status).IsEqualTo("Waiting for your browser…");
    }

    static ReauthGraph Build(TempConfigRoot config, Func<ConnectIntent, CancellationToken, Task<AuthResult>> operation) =>
        ReauthComposition.Build(
            config.Root, AuthFixtures.NewTokenStore(config.Root), new PlainHttpClientFactory(),
            new AuthProxyClient(new HttpClient(), TimeProvider.System), new(new PlainHttpClientFactory()), new(new PlainHttpClientFactory(), TimeProvider.System),
            "default", ServerUrl,
            WizardComposition.BuildBridges(action => action(), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults),
            new ConsentFlipClaims(config.Root),
            new AppStateStore(config.PathTo("app-state.json")),
            new RecordingOpener(),
            TimeProvider.System,
            _ => operation,
            refreshAppState: true);

    /// A Sign in click over an open dialog whose attempt was cancelled or failed goes straight back
    /// to the browser, including for the same server spelled with its default port; the dialog's
    /// own Sign in is never a second click.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, ServerUrl)]
    [Arguments(true, ServerUrl)]
    [Arguments(false, "https://ACME.example:443/")]
    public async Task Signing_in_again_over_an_unfinished_attempt_starts_a_new_one(bool failed, string requested) {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            var runs = 0;
            var graph = Build(config, (_, _) => Task.FromResult<AuthResult>(
                ++runs == 1 ? failed ? new AuthResult.Failed("nope") : new AuthResult.Cancelled() : Committed()));
            await graph.SignIn.EnterAndSignInAsync(CancellationToken.None);

            await graph.SignInAgainAsync("default", requested);

            await Assert.That(runs).IsEqualTo(2);
            await Assert.That(graph.SignIn.Satisfied).IsTrue();
        });
    }

    /// The open dialog belongs to one profile and server; a Sign in for another must not run its flow.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("other", ServerUrl)]
    [Arguments("default", "https://other.example")]
    public async Task Signing_in_again_for_another_target_starts_nothing_in_the_open_dialog(string profile, string serverUrl) {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            var runs = 0;
            var graph = Build(config, (_, _) => {
                runs++;
                return Task.FromResult<AuthResult>(new AuthResult.Cancelled());
            });
            await graph.SignIn.EnterAndSignInAsync(CancellationToken.None);

            await graph.SignInAgainAsync(profile, serverUrl);

            await Assert.That(runs).IsEqualTo(1);
        });
    }

    /// A repeat click while the browser is still open leaves that attempt running rather than
    /// starting a second one beside it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Signing_in_again_during_an_attempt_does_not_start_another() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var config = new TempConfigRoot();
            var runs = 0;
            var browser = new TaskCompletionSource<AuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var graph = Build(config, (_, _) => {
                runs++;
                return browser.Task;
            });

            var first = graph.SignIn.EnterAndSignInAsync(CancellationToken.None);
            var again = graph.SignInAgainAsync("default", ServerUrl);
            var runsWhileWaiting = runs;

            browser.SetResult(Committed());
            await Task.WhenAll(first, again);

            await Assert.That(runsWhileWaiting).IsEqualTo(1);
            await Assert.That(runs).IsEqualTo(1);
            await Assert.That(graph.SignIn.Satisfied).IsTrue();
        });
    }
}
