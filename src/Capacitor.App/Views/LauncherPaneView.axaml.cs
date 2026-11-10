using System.Globalization;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.App.ViewModels;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

/// The launcher pane: DataContext is supplied externally (a plainly-constructed HomeViewModel);
/// this view never builds its own ViewModel.
public partial class LauncherPaneView : UserControl {
    AttachmentDropPaste? _attachments;
    Flyout? _openMenu;
    IDisposable? _nudgeEnd;

    static readonly TimeSpan NudgeFadeOutAfter = TimeSpan.FromMilliseconds(450);
    readonly Transitions _nudgeFadeIn = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250), Easing = new CubicEaseOut() }];
    readonly Transitions _nudgeFadeOut = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(2), Easing = new LinearEasing() }];

    public LauncherPaneView() {
        InitializeComponent();
        // Tunnel, not bubble: the TextBox marks Enter handled on the bubble route, so bare-Enter
        // submit must see the key on the way down. Shift+Enter falls through untouched, so the
        // TextBox inserts a newline (AcceptsReturn is true).
        GoalInput.AddHandler(KeyDownEvent, OnGoalKeyDown, RoutingStrategies.Tunnel);
    }

    public void FocusComposer() => GoalInput.Focus();

    /// New session pressed while this pane is already showing: focus the goal and ring the card
    /// briefly, so the press does not read as a dead button.
    public void Nudge() {
        FocusComposer();
        GoalCard.BringIntoView();
        _nudgeEnd?.Dispose();
        GoalNudgeRing.Transitions = _nudgeFadeIn;
        GoalNudgeRing.Classes.Set("on", true);
        _nudgeEnd = DispatcherTimer.RunOnce(() => {
            GoalNudgeRing.Transitions = _nudgeFadeOut;
            GoalNudgeRing.Classes.Set("on", false);
        }, NudgeFadeOutAfter);
    }

    internal bool IsNudgingForTesting => GoalNudgeRing.Classes.Contains("on");

    IDisposable? _fills;

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        _fills?.Dispose();
        _fills = (DataContext as HomeViewModel)?.ComposerFills.Subscribe(_ => Dispatcher.UIThread.Post(Nudge));
    }

    void OnClearNextWorkSource(object? sender, RoutedEventArgs e) {
        (DataContext as HomeViewModel)?.ClearNextWorkSource();
        FocusComposer();
    }

    internal Task? PendingIntakeForTesting => _attachments?.PendingIntakeForTesting;

    internal Flyout? OpenMenuForTesting => _openMenu;

    /// Paired with the visual tree rather than the constructor: a pane swap detaches and
    /// re-attaches the same view, and a behaviour disposed on the way out has to come back with it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attachments ??= AttachmentDropPaste.Attach(
            GoalCard, GoalInput, AttachButton,
            () => (DataContext as HomeViewModel)?.Attachments, TimeProvider.System);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _attachments?.Dispose();
        _attachments = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// Bare Enter starts a session when Start can run; otherwise the key is consumed so it does
    /// not leave a stray newline. Shift+Enter is left for the TextBox to insert a newline. Mirrors
    /// the Start button: connection readiness from CanExecute, repository from SelectedRepoPath.
    void OnGoalKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is not HomeViewModel vm) return;
        if (string.IsNullOrEmpty(vm.SelectedRepoPath)) return;
        if (!((ICommand)vm.StartCommand).CanExecute(null)) return;
        vm.StartCommand.Execute().Subscribe();
    }

    // Repository picker: one flyout item per ListRepositoriesAsync entry — leaf name over full
    // path, remembered-harness pill on the right. The scratch entry and
    // the add affordance sit last, each behind a separator; for a remote machine the add is a typed
    // path, since the folder picker only sees this machine. Built as a kcapPanel Flyout
    // (same shape as the agent picker) rather than MenuFlyout — Fluent's radio MenuItem chrome
    // fights the dark palette.
    async void OnRepositoryChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var muted = this.Brush("KcapMutedBrush");
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };

        var flyout = ChoiceMenus.PanelFlyout(rows, minWidth: 300);
        foreach (var option in await vm.ListRepositoriesAsync()) {
            // Scratch is only listed when there are no real repos — still separate it visually
            // if it ever shares the menu with another row.
            if (option.RepoPath.Length == 0 && rows.Children.Count > 0)
                rows.Children.Add(this.ChoiceSeparator());
            rows.Children.Add(RepositoryRow(vm, option, muted, flyout));
        }
        rows.Children.Add(this.ChoiceSeparator());
        if (vm.RemoteMachineSelected)
            rows.Children.Add(this.ChoiceRow($"Enter a path on {vm.SelectedMachine}…", selected: false, () => {
                flyout.Hide();
                ShowRemotePathEntry(vm, anchor);
            }));
        else
            rows.Children.Add(this.ChoiceRow("Add repository…", selected: false, () => {
                flyout.Hide();
                _ = AddRepositoryAsync(vm);
            }));

        ShowMenu(flyout, anchor);
    }

    Button RepositoryRow(HomeViewModel vm, RepositoryOption option, IBrush muted, Flyout flyout) {
        var isScratch = option.RepoPath.Length == 0;
        var title = new TextBlock {
            Text = isScratch ? "No repository" : RepoLabel.Leaf(option.RepoPath),
            FontSize = 13.5,
            Foreground = this.Brush("KcapTextBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        // Pill sits on the title row only (not vertically centered on title+path), so every
        // repo's badge lines up with "No repository"'s badge. No harness available, no badge.
        var pill = new StatusBadge {
            Text = HostedHarnessCatalog.LabelFor(vm.Harnesses, option.Vendor),
            Margin = new Thickness(12, 0, 0, 0),
            IsVisible = HomeViewModel.IsAvailable(vm.Harnesses, option.Vendor),
        };

        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            MinWidth = 260,
            // Stretch so the * column eats leftover width and the pill stays right-aligned
            // across one-line and two-line rows alike.
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        grid.Children.Add(title);
        var check = this.SelectionCheck(option.Selected);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        Grid.SetColumn(pill, 2);
        grid.Children.Add(pill);

        if (!isScratch) {
            var path = new TextBlock {
                Text = option.RepoPath, FontSize = 10.5, Foreground = muted,
                Margin = new Thickness(0, 2, 0, 0),
            };
            Grid.SetRow(path, 1);
            Grid.SetColumnSpan(path, 3);
            grid.Children.Add(path);
        }

        var repoPath = option.RepoPath;
        var row = ChoiceMenus.ChoiceButton(grid, () => {
            flyout.Hide();
            _ = SelectRepositoryObservedAsync(vm, repoPath);
        });
        AutomationProperties.SetName(row, ChoiceMenus.ChoiceName(title.Text, option.Selected));
        return row;
    }

    // ChoiceButton's click is sync; keep flyout-close immediate and observe the async load so a
    // failed state read is not an unobserved task.
    static async Task SelectRepositoryObservedAsync(HomeViewModel vm, string repoPath) {
        try {
            await vm.SelectRepositoryAsync(repoPath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: select repository failed: {ex}");
        }
    }

    // Machine picker: one flyout item per ListMachinesAsync entry — DaemonName with a checkmark
    // on the selected one, disabled (never pickable) when not Connected. Same kcapPanel Flyout
    // shape as the repository picker, one row layout instead of two-line paths.
    async void OnMachineChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var muted = this.Brush("KcapMutedBrush");
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = ChoiceMenus.PanelFlyout(rows, minWidth: 220);
        foreach (var option in await vm.ListMachinesAsync())
            rows.Children.Add(MachineRow(vm, option, muted, flyout));

        ShowMenu(flyout, anchor);
    }

    Button MachineRow(HomeViewModel vm, MachineOption option, IBrush muted, Flyout flyout) {
        var title = new TextBlock {
            Text = option.DaemonName,
            FontSize = 13.5,
            Foreground = option.Connected ? this.Brush("KcapTextBrush") : muted,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        var check = this.SelectionCheck(option.Selected);

        var subtitle = new TextBlock {
            Text = option.IsLocal ? "This machine" : option.Connected ? "Connected" : "Offline",
            FontSize = 10.5, Foreground = muted, Margin = new Thickness(0, 2, 0, 0),
        };

        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            MinWidth = 200,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        grid.Children.Add(title);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        Grid.SetRow(subtitle, 1);
        grid.Children.Add(subtitle);

        var daemonName = option.DaemonName;
        var isLocal = option.IsLocal;
        var row = ChoiceMenus.ChoiceButton(grid, () => {
            flyout.Hide();
            _ = SelectMachineObservedAsync(vm, daemonName, isLocal);
        });
        row.IsEnabled = option.Connected;
        AutomationProperties.SetName(row, ChoiceMenus.ChoiceName(option.DaemonName, option.Selected));
        return row;
    }

    // ChoiceButton's click is sync; keep flyout-close immediate and observe the async load so a
    // failed state read is not an unobserved task (SelectRepositoryObservedAsync's identical shape).
    // isLocal comes from the clicked option itself — an owned remote daemon can share the local
    // daemon's name (different servers), so routing by name alone would misselect it.
    static async Task SelectMachineObservedAsync(HomeViewModel vm, string daemonName, bool isLocal) {
        try {
            await vm.SelectMachineAsync(daemonName, isLocal);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: select machine failed: {ex}");
        }
    }

    // "Add one" via a native folder picker — SelectRepositoryAsync then treats the choice like
    // any other repository, so it shows up in the menu from the next open on.
    async Task AddRepositoryAsync(HomeViewModel vm) {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Choose a repository",
            AllowMultiple = false,
        });
        if (folders is not [{ } folder]) return;
        if (folder.TryGetLocalPath() is not { } path) return;

        await vm.SelectRepositoryAsync(path);
    }

    async void OnRequestRepositoryAccessClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        if (sender is Control button) button.IsEnabled = false;
        try {
            await vm.RequestRepositoryAccessAsync(async path => {
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions {
                    Title = "Allow access to " + RepoLabel.Leaf(path),
                    AllowMultiple = false,
                    SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(path),
                });
                if (folders is not [{ } folder]) return false;
                using (folder) {
                    return folder.TryGetLocalPath() is { } selected
                        && await Task.Run(() => IsSameRepositoryFolder(selected, path));
                }
            });
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: request repository access failed: {ex}");
        } finally {
            if (sender is Control control) control.IsEnabled = true;
        }
    }

    // NSOpenPanel can return /tmp for a repository selected through /private/tmp (or a
    // resolved target for another symlink). Compare physical folders without changing the draft.
    internal static bool IsSameRepositoryFolder(string selected, string repository) =>
        PlatformPaths.Comparer.Equals(CanonicalPath.Resolve(selected), CanonicalPath.Resolve(repository));

    void ShowRemotePathEntry(HomeViewModel vm, Control anchor) {
        var input = new TextBox { PlaceholderText = "/path/to/repository", MinWidth = 320 };
        input.Classes.Add("kcapField");
        AutomationProperties.SetName(input, $"Repository path on {vm.SelectedMachine}");
        var error = new TextBlock {
            FontSize = 11.5, Foreground = this.Brush("KcapDangerBrush"), IsVisible = false,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
        };
        var hint = new TextBlock {
            Text = "Checked on that machine when the session starts.",
            FontSize = 11.5, Foreground = this.Brush("KcapMutedBrush"),
        };

        var body = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
        body.Children.Add(input);
        body.Children.Add(error);
        body.Children.Add(hint);
        var flyout = ChoiceMenus.PanelFlyout(body, minWidth: 340);

        input.KeyDown += async (_, e) => {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                flyout.Hide();
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            try {
                var refusal = await vm.EnterRemoteRepositoryAsync(input.Text ?? "");
                error.Text = refusal;
                error.IsVisible = refusal is not null;
                if (refusal is null) flyout.Hide();
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: enter remote repository failed: {ex}");
            }
        };
        flyout.Opened += (_, _) => input.Focus();

        ShowMenu(flyout, anchor);
    }

    const int MaxBranchRows = 8;

    // Base-ref picker: a typed name, then Current HEAD, the refs sessions here recently started
    // from (each removable), and the repository's branches once they are read, narrowed by the field.
    async void OnBaseRefChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var input = new TextBox { PlaceholderText = "Branch, tag or commit", MinWidth = 280, Text = vm.SelectedBaseRef };
        input.Classes.Add("kcapField");
        AutomationProperties.SetName(input, "Branch, tag or commit to start from");
        // Typing while the lists load must still narrow them, so this is the text before any await.
        var initial = input.Text;
        var error = new TextBlock {
            FontSize = 11.5, Foreground = this.Brush("KcapDangerBrush"), IsVisible = false,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 280,
        };

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var field = new StackPanel { Spacing = 6, Margin = new Thickness(4, 4, 4, 6) };
        field.Children.Add(input);
        field.Children.Add(error);
        rows.Children.Add(field);
        var flyout = ChoiceMenus.PanelFlyout(rows, minWidth: 300);

        async void Choose(string? baseRef) {
            var problem = await vm.ChooseBaseRefAsync(baseRef);
            error.Text = problem;
            error.IsVisible = problem is not null;
            if (problem is null) flyout.Hide();
        }

        rows.Children.Add(this.ChoiceRow("Current HEAD", vm.SelectedBaseRef is null, () => Choose(null)));
        var recentSection = new StackPanel { Spacing = 2, IsVisible = false };
        var branchSection = new StackPanel { Spacing = 2, IsVisible = false };
        rows.Children.Add(recentSection);
        rows.Children.Add(branchSection);

        input.KeyDown += (_, args) => {
            if (args.Key == Key.Escape) {
                args.Handled = true;
                flyout.Hide();
                return;
            }
            if (args.Key != Key.Enter) return;
            args.Handled = true;
            Choose(input.Text);
        };
        flyout.Opened += (_, _) => {
            input.Focus();
            input.SelectAll();
        };

        var lookup = new CancellationTokenSource();
        flyout.Closed += (_, _) => lookup.Cancel();

        // Opened before either read, which go to disk or the network: the field is usable at once.
        ShowMenu(flyout, anchor);

        var branches = vm.ListBranchesAsync(lookup.Token);
        try {
            var recent = await vm.ListRecentBaseRefsAsync();
            if (recent.Count > 0) {
                recentSection.Children.Add(this.ChoiceSeparator());
                foreach (var baseRef in recent)
                    recentSection.Children.Add(RecentBaseRefRow(vm, baseRef, recentSection, () => Choose(baseRef)));
                recentSection.IsVisible = true;
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: list recent base refs failed: {ex}");
        }

        if (!branches.IsCompleted) {
            branchSection.Children.Add(this.ChoiceSeparator());
            branchSection.Children.Add(BranchNote("Looking up branches…"));
            branchSection.IsVisible = true;
        }
        BranchListing? listing;
        try { listing = await branches; }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) {
            Console.Error.WriteLine($"kcap: list branches failed: {ex}");
            listing = null;
        }

        void Render() {
            branchSection.Children.Clear();
            if (listing is null) {
                branchSection.IsVisible = false;
                return;
            }
            var filter = input.Text == initial ? "" : (input.Text ?? "").Trim();
            var matches = listing.Branches
                .Where(b => b.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b == listing.DefaultBranch ? 0 : 1)
                .ToList();
            branchSection.IsVisible = matches.Count > 0;
            if (matches.Count == 0) return;
            branchSection.Children.Add(this.ChoiceSeparator());
            foreach (var branch in matches.Take(MaxBranchRows))
                branchSection.Children.Add(BranchRow(vm, branch, branch == listing.DefaultBranch, () => Choose(branch)));
            if (matches.Count > MaxBranchRows)
                branchSection.Children.Add(BranchNote($"{matches.Count - MaxBranchRows} more — type to narrow"));
        }
        Render();
        input.TextChanged += (_, _) => Render();
    }

    TextBlock BranchNote(string text) => new() {
        Text = text, FontSize = 11.5, Foreground = this.Brush("KcapMutedBrush"), Margin = new Thickness(10, 4),
    };

    Control BranchRow(HomeViewModel vm, string branch, bool isDefault, Action choose) {
        var selected = vm.SelectedBaseRef == branch;
        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), MinWidth = 220,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        grid.Children.Add(new TextBlock {
            Text = branch, FontSize = 13.5, Foreground = this.Brush("KcapTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        if (isDefault) {
            var tag = new StatusBadge { Text = "Default", Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(tag, 1);
            grid.Children.Add(tag);
        }
        var check = this.SelectionCheck(selected);
        Grid.SetColumn(check, 2);
        grid.Children.Add(check);

        var pick = ChoiceMenus.ChoiceButton(grid, choose);
        AutomationProperties.SetName(pick, ChoiceMenus.ChoiceName(isDefault ? $"{branch}, default branch" : branch, selected));
        return pick;
    }

    Control RecentBaseRefRow(HomeViewModel vm, string baseRef, Panel section, Action choose) {
        var selected = vm.SelectedBaseRef == baseRef;
        var title = new TextBlock {
            Text = baseRef, FontSize = 13.5, Foreground = this.Brush("KcapTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinWidth = 220,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        grid.Children.Add(title);
        var check = this.SelectionCheck(selected);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        var pick = ChoiceMenus.ChoiceButton(grid, choose);
        AutomationProperties.SetName(pick, ChoiceMenus.ChoiceName(baseRef, selected));

        var remove = new Button {
            Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Content = new AppIcon { Kind = LucideIconKind.X, Size = 12, Foreground = this.Brush("KcapMutedBrush") },
        };
        remove.Classes.Add("kcapGhost");
        ToolTip.SetTip(remove, "Remove from this list");
        AutomationProperties.SetName(remove, $"Remove {baseRef} from recent branches");

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(pick);
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);

        remove.Click += async (_, _) => {
            section.Children.Remove(row);
            if (section.Children.Count == 1) {
                section.Children.Clear();
                section.IsVisible = false;
            }
            try { await vm.ForgetBaseRefAsync(baseRef); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: forget base ref failed: {ex}"); }
        };
        return row;
    }

    void ShowMenu(Flyout flyout, Control anchor) {
        _openMenu = flyout;
        flyout.ShowAt(anchor);
    }

}

/// Leaf under the fixed question; empty when nothing is selected (the subtitle is then hidden).
public sealed class LauncherRepoSubtitleConverter : IValueConverter {
    public static readonly LauncherRepoSubtitleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? RepoLabel.Leaf(path) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Repo chip tip: full path when selected, otherwise the control's job label.
public sealed class RepositoryChipTipConverter : IValueConverter {
    public static readonly RepositoryChipTipConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? path : "Repository for the new session";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// MachineChip's label: always "Machine · …" so the chip's job stays readable next to Repo.
public sealed class MachineLabelConverter : IValueConverter {
    public static readonly MachineLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } name ? $"Machine · {name}" : "Machine";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// BaseRefChip's label: "Branch from · …", HEAD when nothing is chosen.
public sealed class BaseRefLabelConverter : IValueConverter {
    public static readonly BaseRefLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        $"Branch from · {(value is string { Length: > 0 } name ? name : "HEAD")}";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// StartButton's tip: an upload in flight owns the button, so it says so rather than leaving the
/// repository-gate wording standing over a control the user cannot use.
public sealed class StartButtonTipConverter : IMultiValueConverter {
    public static readonly StartButtonTipConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [_, true] ? "Uploading…" : values is [string tip, ..] ? tip : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
