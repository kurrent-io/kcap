using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
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

/// The launch composer over a HomeViewModel: the machine, repository and branch chips, the goal and Start.
public partial class LaunchComposerView : UserControl {
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<LaunchComposerView, bool>(nameof(Compact));

    /// The next-work key the surrounding page already stands for, whose "Picked up" chip would only repeat it.
    public static readonly StyledProperty<string?> ImpliedSourceKeyProperty =
        AvaloniaProperty.Register<LaunchComposerView, string?>(nameof(ImpliedSourceKey));

    public bool Compact {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public string? ImpliedSourceKey {
        get => GetValue(ImpliedSourceKeyProperty);
        set => SetValue(ImpliedSourceKeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == CompactProperty) Classes.Set("compact", Compact);
    }

    readonly Composer _composer;
    Flyout? _openMenu;
    IDisposable? _nudgeEnd;
    IDisposable? _fills;

    static readonly TimeSpan NudgeFadeOutAfter = TimeSpan.FromMilliseconds(450);
    readonly Transitions _nudgeFadeIn = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(250), Easing = new CubicEaseOut() }];
    readonly Transitions _nudgeFadeOut = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(2), Easing = new LinearEasing() }];

    public LaunchComposerView() {
        InitializeComponent();
        _composer = Composer.Attach(GoalCard, GoalInput, AttachButton);
        // Tunnel, not bubble: the TextBox marks Enter handled on the bubble route, so bare-Enter
        // submit must see the key on the way down.
        GoalInput.AddHandler(KeyDownEvent, OnGoalKeyDown, RoutingStrategies.Tunnel);
    }

    public void FocusComposer() => GoalInput.Focus();

    /// Focuses the goal and rings the card briefly, so a press that lands here does not read as a dead button.
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

    internal Task? PendingIntakeForTesting => _composer.PendingIntakeForTesting;

    internal Flyout? OpenMenuForTesting => _openMenu;

    // A fill reaches only the composer on screen. A compact one sits in a page that fills it on opening, so it takes
    // focus without the ring.
    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        _fills?.Dispose();
        _fills = (DataContext as HomeViewModel)?.ComposerFills.Subscribe(_ => Dispatcher.UIThread.Post(OnFilled));
    }

    void OnFilled() {
        if (!IsEffectivelyVisible) return;
        if (Compact) FocusComposer();
        else Nudge();
    }

    void OnClearNextWorkSource(object? sender, RoutedEventArgs e) {
        (DataContext as HomeViewModel)?.ClearNextWorkSource();
        FocusComposer();
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

    // Repository picker: the known repositories narrowed by the field, then the add row. A remote machine's add is a
    // typed path, since the folder picker only sees this machine.
    async void OnRepositoryChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel vm || sender is not Control anchor) return;

        var options = await vm.ListRepositoriesAsync();
        var add = vm.RemoteMachineSelected
            ? this.ChoiceRow($"Enter a path on {vm.SelectedMachine}…", selected: false, () => ShowRemotePathEntry(vm, anchor))
            : this.ChoiceRow("Add repository…", selected: false, () => _ = AddRepositoryAsync(vm));
        SearchMenu(anchor, "Find a repository", "No matching repositories", query => [.. options
            .Where(option => option.RepoPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(option => RepositoryRow(vm, option))], this.ChoiceSeparator(), add);
    }

    Button RepositoryRow(HomeViewModel vm, RepositoryOption option) {
        var isScratch = option.RepoPath.Length == 0;
        var title = new TextBlock {
            Text = isScratch ? "No repository" : RepoLabel.Leaf(option.RepoPath),
            FontSize = 13.5, Foreground = this.Brush("KcapTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel { Spacing = 1, Children = { title } };
        if (!isScratch)
            text.Children.Add(new TextBlock {
                Text = option.RepoPath, FontSize = 11, Foreground = this.Brush("KcapMutedBrush"),
                TextTrimming = TextTrimming.PrefixCharacterEllipsis,
            });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        grid.Children.Add(text);
        var check = this.SelectionCheck(option.Selected);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        // Only a repository that would switch the harness says which one it remembers.
        if (option.Vendor != vm.SelectedVendor && HomeViewModel.IsAvailable(vm.Harnesses, option.Vendor)) {
            var glyph = LaunchChipsView.BuildGlyph(option.Vendor, 13);
            glyph.Margin = new Thickness(10, 0, 0, 0);
            glyph.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
            ToolTip.SetTip(glyph, HostedHarnessCatalog.LabelFor(vm.Harnesses, option.Vendor));
            Grid.SetColumn(glyph, 2);
            grid.Children.Add(glyph);
        }

        var repoPath = option.RepoPath;
        var row = ChoiceMenus.ChoiceButton(grid, () => _ = SelectRepositoryObservedAsync(vm, repoPath));
        row.Padding = new Thickness(10, 6);
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

    // Work-item picker: the project's work items narrowed by the field; a pick fills the launcher the way a next-work
    // row does, so the session it starts is attached to the item.
    void OnWorkItemChipClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not HomeViewModel { NextWork: { } band } vm || sender is not Control anchor) return;
        var picked = vm.NextWorkSource?.Key;
        SearchMenu(anchor, "Find a work item", "No matching work items", query => [.. vm.WorkItemChoices
            .Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => this.ChoiceRow(item.Title, picked == ViewModels.NextWork.NextWorkLaneLogic.WorkItemPrefix + item.Id,
                () => _ = band.PickWorkItemAsync(item.Id, item.Title)))]);
    }

    // A find field over a capped, scrolling list, with any footer rows below it. Enter picks the first match, Down
    // moves into the list, and any row's click closes the menu.
    void SearchMenu(Control anchor, string placeholder, string empty, Func<string, IReadOnlyList<Button>> rowsFor, params Control[] footer) {
        var search = new TextBox { PlaceholderText = placeholder, Margin = new Thickness(4, 4, 4, 6) };
        search.Classes.Add("kcapField");
        AutomationProperties.SetName(search, placeholder);
        var list = new StackPanel { Spacing = 2 };
        var body = new StackPanel { Spacing = 2, Margin = new Thickness(6), Width = 380 };
        body.Children.Add(search);
        body.Children.Add(new ScrollViewer { MaxHeight = 340, Content = list });
        body.Children.AddRange(footer);
        var flyout = ChoiceMenus.PanelFlyout(body, minWidth: 380);
        body.AddHandler(Button.ClickEvent, (_, _) => flyout.Hide());

        IReadOnlyList<Button> rows = [];
        void Narrow() {
            rows = rowsFor(search.Text?.Trim() ?? "");
            list.Children.Clear();
            list.Children.AddRange(rows);
            if (rows.Count == 0) list.Children.Add(BranchNote(empty));
        }
        search.TextChanged += (_, _) => Narrow();
        search.KeyDown += (_, args) => {
            if (args.Key == Key.Enter && rows.Count > 0) {
                args.Handled = true;
                rows[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            } else if (args.Key == Key.Down) {
                args.Handled = true;
                FlyoutKeyboard.FocusFirstItem(list, NavigationMethod.Directional);
            }
        };
        body.KeyDown += (_, args) => FlyoutKeyboard.DismissOnEscape(flyout, anchor, args, NavigationMethod.Directional);
        flyout.Opened += (_, _) => search.Focus();
        Narrow();
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
