using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core.Http;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel("AvaloniaSession")]
public class ProfilesSettingsViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string TokensDir => Config.PathTo("tokens");

    static StoredTokens Token(string provider, DateTimeOffset expires, string? server = null, string? refresh = null) => new() {
        AccessToken = "at", ExpiresAt = expires, Provider = provider, ServerUrl = server, RefreshToken = refresh, ClientId = refresh is null ? null : "cid",
        GitHubUsername = "user"
    };

    async Task<TokenStore> Seed() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "work",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new(),
                ["work"]    = new() { ServerUrl = "https://work.example" },
                ["other"]   = new() { ServerUrl = "https://other.example" },
                ["stale"]   = new() { ServerUrl = "https://stale.example" },
                ["moved"]   = new() { ServerUrl = "https://moved.example" },
                ["open"]    = new() { ServerUrl = "https://open.example", AuthProvider = new AuthProviderStamp(AuthProvider.None, "https://open.example") },
                ["bad"]     = new() { ServerUrl = "https://bad.example" }
            }
        });
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        await tokens.SaveAsync("work",  Token(AuthProvider.GitHubApp, DateTimeOffset.UtcNow.AddHours(1), "https://work.example"));
        await tokens.SaveAsync("stale", Token(AuthProvider.WorkOS, DateTimeOffset.UtcNow.AddHours(-1), "https://stale.example"));
        await tokens.SaveAsync("moved", Token(AuthProvider.GitHubApp, DateTimeOffset.UtcNow.AddHours(1), "https://elsewhere.example"));
        Directory.CreateDirectory(Path.Combine(TokensDir, "bad.json")); // a directory where the file should be: the read throws
        return tokens;
    }

    ProfilesSettingsViewModel Make(TokenStore tokens, string bound = "work",
            Func<string, string, CancellationToken, Task>? openSignIn = null,
            Func<string, CancellationToken, Task<bool>>? confirm = null) =>
        new(Config.Root, tokens, new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System), bound,
            openSignIn ?? ((_, _, _) => Task.CompletedTask), confirm ?? ((_, _) => Task.FromResult(true)));

    [Test]
    public Task Rows_reflect_config_marks_and_credential_status() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows.Count).IsEqualTo(7);
        await Assert.That(rows["work"].Status).IsEqualTo(ProfileCredentialStatus.SignedIn);
        await Assert.That(rows["work"].IsActive).IsTrue();
        await Assert.That(rows["work"].IsBound).IsTrue();
        await Assert.That(rows["work"].CanRemove).IsFalse();
        await Assert.That(rows["other"].Status).IsEqualTo(ProfileCredentialStatus.SignedOut);
        await Assert.That(rows["other"].CanRemove).IsTrue();
        await Assert.That(rows["other"].CanSignIn).IsTrue();
        await Assert.That(rows["stale"].Status).IsEqualTo(ProfileCredentialStatus.Expired);
        await Assert.That(rows["moved"].Status).IsEqualTo(ProfileCredentialStatus.OtherServer);
        await Assert.That(rows["open"].Status).IsEqualTo(ProfileCredentialStatus.NoSignInNeeded);
        await Assert.That(rows["open"].CanSignIn).IsFalse();
        await Assert.That(rows["default"].Status).IsEqualTo(ProfileCredentialStatus.NoServer);
        await Assert.That(rows["default"].CanSignIn).IsFalse();
        await Assert.That(rows["default"].CanRemove).IsFalse();
        await Assert.That(rows["bad"].Status).IsEqualTo(ProfileCredentialStatus.Unreadable);
        await Assert.That(rows["bad"].CanSignIn).IsTrue();
    });

    [Test]
    public Task Active_and_bound_are_marked_separately() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed(), bound: "other");
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows["work"].IsActive).IsTrue();
        await Assert.That(rows["work"].IsBound).IsFalse();
        await Assert.That(rows["work"].CanRemove).IsFalse();
        await Assert.That(rows["other"].IsBound).IsTrue();
        await Assert.That(rows["other"].CanRemove).IsFalse();
    });

    [Test]
    public Task Remove_deletes_the_profile_and_its_token_after_confirmation() => AvaloniaSession.RunOnUiAsync(async () => {
        var tokens = await Seed();
        string? asked = null;
        var vm = Make(tokens, confirm: (name, _) => { asked = name; return Task.FromResult(true); });
        await vm.RefreshAsync();

        await vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "stale")).ToTask();

        await Assert.That(asked).IsEqualTo("stale");
        await Assert.That(vm.Rows.Any(r => r.Name == "stale")).IsFalse();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "stale.json"))).IsFalse();
        await Assert.That(vm.Message!).Contains("removed");
    });

    [Test]
    public Task Remove_does_nothing_when_declined() => AvaloniaSession.RunOnUiAsync(async () => {
        var tokens = await Seed();
        var vm = Make(tokens, confirm: (_, _) => Task.FromResult(false));
        await vm.RefreshAsync();

        await vm.RemoveCommand.Execute(vm.Rows.Single(r => r.Name == "stale")).ToTask();

        await Assert.That(vm.Rows.Any(r => r.Name == "stale")).IsTrue();
        await Assert.That(File.Exists(Path.Combine(TokensDir, "stale.json"))).IsTrue();
    });

    [Test]
    public Task Remove_of_a_row_changed_since_the_last_read_is_refused() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();
        var row = vm.Rows.Single(r => r.Name == "other");
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            Profiles = new Dictionary<string, Profile>(c.Profiles) { ["other"] = new() { ServerUrl = "https://repointed.example" } }
        });

        await vm.RemoveCommand.Execute(row).ToTask();

        await Assert.That(vm.Message).IsEqualTo("This profile changed. The list was refreshed.");
        await Assert.That(vm.Rows.Single(r => r.Name == "other").ServerUrl).IsEqualTo("https://repointed.example");
    });

    [Test]
    public Task SignIn_opens_the_dialog_for_the_row() => AvaloniaSession.RunOnUiAsync(async () => {
        (string Profile, string Server)? opened = null;
        var vm = Make(await Seed(), openSignIn: (p, s, _) => { opened = (p, s); return Task.CompletedTask; });
        await vm.RefreshAsync();

        await vm.SignInCommand.Execute(vm.Rows.Single(r => r.Name == "other")).ToTask();

        await Assert.That(opened).IsEqualTo(("other", "https://other.example"));
    });

    [Test]
    public Task SignIn_of_a_row_with_no_server_is_refused() => AvaloniaSession.RunOnUiAsync(async () => {
        var opened = false;
        var vm = Make(await Seed(), openSignIn: (_, _, _) => { opened = true; return Task.CompletedTask; });
        await vm.RefreshAsync();

        await vm.SignInCommand.Execute(vm.Rows.Single(r => r.Name == "default")).ToTask();

        await Assert.That(opened).IsFalse();
        await Assert.That(vm.Message).IsEqualTo("This profile has no server to sign in to.");
    });

    [Test]
    public Task Refresh_reports_an_unreadable_config_and_keeps_the_rows() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();
        var before = vm.Rows.Count;
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), "{ not json");

        await Assert.That(await vm.RefreshAsync()).IsFalse();

        await Assert.That(vm.Message).IsEqualTo("Could not read the profile configuration.");
        await Assert.That(vm.Rows.Count).IsEqualTo(before);
    });

    /// The rows kept after a failed read are not a match: an action on one of them stops at the read.
    [Test]
    public Task Actions_are_refused_while_the_config_is_unreadable() => AvaloniaSession.RunOnUiAsync(async () => {
        var opened = false;
        var vm = Make(await Seed(), openSignIn: (_, _, _) => { opened = true; return Task.CompletedTask; });
        await vm.RefreshAsync();
        var row = vm.Rows.Single(r => r.Name == "other");
        await File.WriteAllTextAsync(AppConfig.GetConfigPath(Config.Root), "{ not json");

        await vm.SignInCommand.Execute(row).ToTask();

        await Assert.That(opened).IsFalse();
        await Assert.That(vm.Message).IsEqualTo("Could not read the profile configuration.");
    });

    /// A signed-in row keeps "Sign in again" out of the way, in its menu: the status is graded
    /// offline, so it stays reachable for a credential the server has since rejected. Rows that
    /// cannot reach their server lead with the button.
    [Test]
    public Task Sign_in_leads_only_on_rows_that_need_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();

        var rows = vm.Rows.ToDictionary(r => r.Name);
        await Assert.That(rows["work"].NeedsSignIn).IsFalse();
        await Assert.That(rows["work"].CanSignInAgain).IsTrue();
        await Assert.That(rows["work"].HasMenu).IsTrue();
        await Assert.That(rows["stale"].NeedsSignIn).IsTrue();
        await Assert.That(rows["stale"].SignInLabel).IsEqualTo("Sign in again");
        await Assert.That(rows["other"].NeedsSignIn).IsTrue();
        await Assert.That(rows["other"].SignInLabel).IsEqualTo("Sign in");
        await Assert.That(rows["moved"].NeedsSignIn).IsTrue();
        await Assert.That(rows["bad"].NeedsSignIn).IsTrue();
        await Assert.That(rows["open"].NeedsSignIn).IsFalse();
    });

    /// The blank default profile is listed only once it is in use or has a server.
    [Test]
    public Task The_unused_blank_default_profile_is_not_listed() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed());
        await vm.RefreshAsync();
        await Assert.That(vm.ListedRows.Any(r => r.Name == "default")).IsFalse();
        await Assert.That(vm.ListedRows.Count).IsEqualTo(vm.Rows.Count - 1);

        var bound = Make(AuthFixtures.NewTokenStore(Config.Root), bound: "default");
        await bound.RefreshAsync();
        await Assert.That(bound.ListedRows.Any(r => r.Name == "default")).IsTrue();
    });

    /// In use is not connected and a stored credential is not either: the bound row carries the app's own live
    /// connection, the daemon's row its own, and a row neither uses carries none.
    [Test]
    public Task The_rows_in_use_carry_live_connection_state_apart_from_their_credential() => AvaloniaSession.RunOnUiAsync(async () => {
        using var lane = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connected));
        var daemon = new FakeDaemonClientService();
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "reconnecting", profile: "work"));
        var tokens = await Seed();
        using var vm = new ProfilesSettingsViewModel(Config.Root, tokens,
            new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System), "work",
            (_, _, _) => Task.CompletedTask, (_, _) => Task.FromResult(true), appLane: lane, daemon: daemon);
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var work = vm.Rows.Single(r => r.Name == "work");
        await Assert.That(work.UsageLabel).IsEqualTo("In use");
        await Assert.That(work.Connection).IsEqualTo("This app: connected · Daemon: reconnecting");
        await Assert.That(work.ConnectionNeedsAttention).IsTrue();
        await Assert.That(vm.Rows.Single(r => r.Name == "other").Connection).IsNull();

        lane.OnNext(new(ServerLaneState.Retrying, "Connection refused", Failure: ConnectionFailure.Refused));
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected", profile: "work"));
        Dispatcher.UIThread.RunJobs();

        work = vm.Rows.Single(r => r.Name == "work");
        await Assert.That(work.Connection).IsEqualTo("This app: can't reach the server (connection refused) · Daemon: connected");
        await Assert.That(work.ConnectionNeedsAttention).IsTrue();
        await Assert.That(work.Status).IsEqualTo(ProfileCredentialStatus.SignedIn).Because("an outage is not a credential problem");
    });

    /// Only the bound row takes the app's connection, and the daemon's only the row it reports running on.
    [Test]
    [Arguments(ServerLaneState.Connected, "This app: connected", false)]
    [Arguments(ServerLaneState.Connecting, "This app: connecting", false)]
    [Arguments(ServerLaneState.Retrying, "This app: reconnecting", false)]
    [Arguments(ServerLaneState.SignedOut, "This app: sign-in rejected", true)]
    [Arguments(ServerLaneState.Dormant, null, false)]
    public async Task Connection_text_follows_whoever_uses_the_row(ServerLaneState state, string? text, bool attention) {
        var unbound = ProfilesSettingsViewModel.ConnectionFor("other", bound: false, (state, null), ("work", "signed out"));
        await Assert.That(unbound.Text).IsNull();

        var mine = ProfilesSettingsViewModel.ConnectionFor("work", bound: true, (state, null), ("other", "signed out"));
        await Assert.That(mine.Text).IsEqualTo(text);
        await Assert.That(mine.Attention).IsEqualTo(attention);
    }

    /// A no-auth local server's row says it needs no sign-in, plainly and without warning colour.
    [Test]
    public Task A_server_without_sign_in_says_so_without_attention() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = Make(await Seed(), bound: "open");
        await vm.RefreshAsync();

        var open = vm.Rows.Single(r => r.Name == "open");
        await Assert.That(open.ShowsStatus).IsTrue();
        await Assert.That(open.StatusLabel).IsEqualTo("No sign-in needed");
        await Assert.That(open.StatusNeedsAttention).IsFalse();
        await Assert.That(open.NeedsSignIn).IsFalse();
    });

    // --- Switch ---

    sealed class SwitchFake {
        public readonly List<MutationRequest> Requests = [];
        public readonly List<string> Prompts = [];
        public Func<MutationRequest, MutationOutcome> Outcome = _ => new MutationOutcome.Succeeded { Activation = ActivationEvidence.Activated };
        public HashSet<string> Replying = [];
        public ServiceSnapshot? BoundService = new("daemon-a", true, "running", null, null, 111, 111, false, false);
        public bool CanActivate = true;
        public Action? DuringProbe;
        public bool Confirms = true;
        public bool RestartRequired;
        public int Relaunches;
        public TaskCompletionSource Settled = CompletedSource();

        static TaskCompletionSource CompletedSource() {
            var tcs = new TaskCompletionSource();
            tcs.SetResult();
            return tcs;
        }

        public Func<Task<bool>>? RelaunchBehavior;

        public ProfileSwitchServices Services(bool profileOverridden = false, bool platformSupported = true) => new(
            "daemon-a",
            (request, _) => { Requests.Add(request); return Task.FromResult(Outcome(request)); },
            (text, _) => { Prompts.Add(text); return Task.FromResult(Confirms); },
            _ => { Relaunches++; return RelaunchBehavior?.Invoke() ?? Task.FromResult(true); },
            Settled.Task,
            (name, _) => Task.FromResult(Replying.Contains(name)),
            (_, _) => Task.FromResult(BoundService),
            (_, _) => { DuringProbe?.Invoke(); return Task.FromResult(CanActivate); },
            () => RestartRequired,
            profileOverridden, platformSupported);
    }

    async Task<TokenStore> SeedSwitch() {
        await ConfigMutator.MutateAsync(Config.Root, c => c with {
            ActiveProfile = "work",
            Profiles = new Dictionary<string, Profile> {
                ["work"]   = new() { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a" } },
                ["target"] = new() { ServerUrl = "https://target.example", Daemon = new DaemonSettings { Name = "daemon-b" } },
                ["same"]   = new() { ServerUrl = "https://same.example", Daemon = new DaemonSettings { Name = "daemon-a" } },
                ["out"]    = new() { ServerUrl = "https://out.example", Daemon = new DaemonSettings { Name = "daemon-c" } },
            }
        });
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        foreach (var (name, server) in new[] { ("work", "https://work.example"), ("target", "https://target.example"), ("same", "https://same.example") })
            await tokens.SaveAsync(name, Token(AuthProvider.GitHubApp, DateTimeOffset.UtcNow.AddHours(1), server));
        return tokens;
    }

    ProfilesSettingsViewModel MakeSwitching(TokenStore tokens, ProfileSwitchServices services, FakeDaemonClientService? daemon = null) {
        daemon ??= IdleDaemon();
        return new(Config.Root, tokens, new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System),
            "work", (_, _, _) => Task.CompletedTask, (_, _) => Task.FromResult(true), daemon: daemon, switching: services);
    }

    static FakeDaemonClientService IdleDaemon(int active = 0) {
        var daemon = new FakeDaemonClientService();
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: active, profile: "work"));
        return daemon;
    }

    static async Task<ProfileRow> Row(ProfilesSettingsViewModel vm, string name) {
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        return vm.Rows.Single(r => r.Name == name);
    }

    string? ActiveProfile() => ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(Config.Root), out var c) ? c.ActiveName : null;

    [Test]
    public Task Switch_is_offered_on_every_other_row_with_a_server_and_enabled_when_every_gate_holds() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake();
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        var target = await Row(vm, "target");
        await Assert.That(target.CanSwitch).IsTrue();
        await Assert.That(target.ShowsSwitchHint).IsFalse();
        await Assert.That(vm.Rows.Single(r => r.Name == "work").OffersSwitch).IsFalse().Because("this app already runs on it");
    });

    [Test]
    [Arguments("signed_out", "Sign in to out before switching to it.")]
    [Arguments("profile_overridden", "KCAP_PROFILE is set")]
    [Arguments("platform", "not available on this platform")]
    [Arguments("restart", "Restart this app before switching again.")]
    [Arguments("startup", "Waiting for daemon startup to finish")]
    [Arguments("agents", "Wait for the 2 active agents to finish before switching.")]
    [Arguments("count_unknown", "Waiting for the daemon’s current agent count")]
    public Task Each_switch_gate_disables_the_row_and_says_why(string gate, string hint) => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake { RestartRequired = gate == "restart" };
        if (gate == "startup") fake.Settled = new TaskCompletionSource();
        var daemon = gate switch {
            "agents"        => IdleDaemon(active: 2),
            "count_unknown" => new FakeDaemonClientService(),
            _               => IdleDaemon(),
        };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services(gate == "profile_overridden", gate != "platform"), daemon);

        var row = await Row(vm, gate == "signed_out" ? "out" : "target");

        await Assert.That(row.CanSwitch).IsFalse();
        await Assert.That(row.SwitchHint!).Contains(hint);
        await vm.SwitchCommand.Execute(row).ToTask();
        await Assert.That(fake.Requests).IsEmpty();
    });

    [Test]
    public Task An_unreachable_daemon_counts_as_idle() => AvaloniaSession.RunOnUiAsync(async () => {
        var daemon = new FakeDaemonClientService();
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, null, null));
        using var vm = MakeSwitching(await SeedSwitch(), new SwitchFake().Services(), daemon);

        await Assert.That((await Row(vm, "target")).CanSwitch).IsTrue();
    });

    [Test]
    public Task A_same_id_switch_sends_one_activating_replace_and_relaunches() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake();
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        await vm.SwitchCommand.Execute(await Row(vm, "same")).ToTask();

        var request = fake.Requests.Single();
        await Assert.That(request.Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(request.Profile).IsEqualTo("same");
        await Assert.That(request.DaemonName).IsEqualTo("daemon-a");
        await Assert.That(request.Activate).IsTrue();
        await Assert.That(request.RetireServiceId).IsNull();
        await Assert.That(request.RetireProfile).IsNull();
        await Assert.That(fake.Prompts.Single()).Contains("Switch to profile same?");
        await Assert.That(fake.Relaunches).IsEqualTo(1);
        await Assert.That(vm.Switched).IsTrue();
    });

    [Test]
    public Task A_cross_id_switch_retires_the_bound_daemon_pinned_to_the_bound_profile() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake();
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        var request = fake.Requests.Single();
        await Assert.That(request.DaemonName).IsEqualTo("daemon-b");
        await Assert.That(request.RetireServiceId).IsEqualTo("daemon-a");
        await Assert.That(request.RetireProfile).IsEqualTo("work");
        await Assert.That(request.Activate).IsTrue();
    });

    /// The courtesy check before the prompt: a target name already answering is a collision, and an old daemon
    /// with no unit cannot be retired; an installed unit, running or stopped, or an absent one that is silent, goes ahead.
    [Test]
    [Arguments("running_unit", true, "")]
    [Arguments("stopped_unit", true, "")]
    [Arguments("absent_silent", true, "")]
    [Arguments("absent_replying", false, "Stop the daemon daemon-a first")]
    [Arguments("unreadable", false, "Could not read the state of the daemon service daemon-a")]
    [Arguments("target_replying", false, "A daemon named daemon-b is already running")]
    public Task The_source_daemon_is_classified_before_a_cross_id_switch(string source, bool proceeds, string refusal) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var fake = new SwitchFake {
                BoundService = source switch {
                    "running_unit" or "target_replying" => new("daemon-a", true, "running", null, null, 111, 111, false, false),
                    "stopped_unit"                      => new("daemon-a", true, "installed", null, null, null, null, false, false),
                    "unreadable"                        => null,
                    _                                   => new("daemon-a", false, "not_installed", null, null, null, null, false, false),
                },
                Replying = source switch {
                    "absent_replying" => ["daemon-a"],
                    "target_replying" => ["daemon-b"],
                    _                 => [],
                },
            };
            using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

            await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

            await Assert.That(fake.Requests.Count).IsEqualTo(proceeds ? 1 : 0);
            if (!proceeds) {
                await Assert.That(vm.Message!).Contains(refusal);
                await Assert.That(fake.Prompts).IsEmpty();
            }
        });

    [Test]
    public Task A_cli_without_activate_is_refused_with_an_update_hint() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake { CanActivate = false };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        await Assert.That(fake.Requests).IsEmpty();
        await Assert.That(vm.Message!).Contains("Update kcap");
        await Assert.That(vm.Switched).IsFalse();
    });

    [Test]
    public Task A_declined_prompt_changes_nothing() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake { Confirms = false };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        await Assert.That(fake.Requests).IsEmpty();
        await Assert.That(vm.Switched).IsFalse();
    });

    /// The report names the step that stopped the switch and what config.json names now, read fresh, and claims
    /// the selection moved only on the CLI's own word.
    [Test]
    [Arguments(ActivationEvidence.Activated, "kcap made target the active profile")]
    [Arguments(ActivationEvidence.Unknown, "")]
    [Arguments(ActivationEvidence.NotActivated, "kcap was not run, so nothing changed.")]
    public Task A_switch_that_does_not_succeed_reports_its_token_its_evidence_and_the_active_profile(
            ActivationEvidence evidence, string claim) => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake {
            Outcome = _ => new MutationOutcome.Failed(VerifyExitCodes.ReadinessTimeout, null, RecoverySurface.Attention) { Activation = evidence },
        };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());
        if (evidence == ActivationEvidence.Activated) await ConfigMutator.MutateAsync(Config.Root, c => c with { ActiveProfile = "target" });

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        await Assert.That(vm.Message!).StartsWith("The switch to target did not finish (verify_readiness_timeout).");
        if (claim.Length > 0) await Assert.That(vm.Message!).Contains(claim);
        else await Assert.That(vm.Message!).DoesNotContain("kcap made").And.DoesNotContain("nothing changed");
        await Assert.That(vm.Message!).Contains($"The kcap command line now uses profile {ActiveProfile()}.");
        await Assert.That(fake.Relaunches).IsEqualTo(0);
        await Assert.That(vm.Switched).IsEqualTo(evidence != ActivationEvidence.NotActivated);
    });

    [Test]
    public Task After_a_switch_every_row_waits_for_a_restart() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake { Outcome = _ => new MutationOutcome.UnconfirmedNoAttach { Activation = ActivationEvidence.Unknown } };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        await Assert.That((await Row(vm, "same")).SwitchHint).IsEqualTo("Restart this app before switching again.");
    });

    [Test]
    public Task The_active_row_left_behind_by_kcap_use_offers_the_switch() => AvaloniaSession.RunOnUiAsync(async () => {
        var tokens = await SeedSwitch();
        await ConfigMutator.MutateAsync(Config.Root, c => c with { ActiveProfile = "target" });
        using var vm = MakeSwitching(tokens, new SwitchFake().Services());

        var target = await Row(vm, "target");

        await Assert.That(target.UsageLabel).IsEqualTo("Command line");
        await Assert.That(target.CanSwitch).IsTrue();
    });

    [Test]
    public Task A_relaunch_that_fails_after_a_successful_switch_still_reports_the_switch() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake { RelaunchBehavior = () => Task.FromException<bool>(new InvalidOperationException("no relaunch")) };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        await Assert.That(vm.Message).IsEqualTo("Switched to target. Restart this app to use it.");
    });

    [Test]
    public Task A_failed_switch_keeps_its_report_when_the_config_has_become_unreadable() => AvaloniaSession.RunOnUiAsync(async () => {
        var fake = new SwitchFake();
        fake.Outcome = _ => {
            File.WriteAllText(AppConfig.GetConfigPath(Config.Root), "{ not json");
            return new MutationOutcome.Failed(VerifyExitCodes.ReadinessTimeout, null, RecoverySurface.Attention) { Activation = ActivationEvidence.Unknown };
        };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services());

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        await Assert.That(vm.Message!).StartsWith("The switch to target did not finish");
        await Assert.That(vm.Message!).Contains("The profile configuration could not be read.");
    });

    [Test]
    public Task An_agent_that_starts_during_the_capability_probe_stops_the_switch() => AvaloniaSession.RunOnUiAsync(async () => {
        var daemon = IdleDaemon();
        var fake = new SwitchFake();
        fake.DuringProbe = () => {
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 1, profile: "work"));
            Dispatcher.UIThread.RunJobs();
        };
        using var vm = MakeSwitching(await SeedSwitch(), fake.Services(), daemon);

        await vm.SwitchCommand.Execute(await Row(vm, "target")).ToTask();

        await Assert.That(fake.Requests).IsEmpty();
        await Assert.That(vm.Message).IsEqualTo("Wait for the active agent to finish before switching.");
    });
}
