using System.Text.Json;
using Capacitor.Cli.Core.Workspace;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Workspace;

/// <summary>A real repository with one commit, a config root, and one server's span store and spool
/// over them. The clock is fake, so no budget runs out unless a test moves it.</summary>
sealed class WorkspaceRig : IDisposable {
    public const string Session = "0123456789abcdef0123456789abcdef";
    public const string Server  = "https://kcap.example";

    readonly List<IDisposable> _owned = [];

    public WorkspaceRig(WorkspaceBounds? bounds = null, string server = Server, long spoolCap = WorkspaceSpool.DefaultCapBytes) {
        Repo   = Own(GitRepo.CreateWithCommit("ws"));
        Config = Own(new TempConfigRoot("ws-config"));
        Bounds = bounds ?? WorkspaceBounds.Default;
        Spool  = new WorkspaceSpool(SpoolPartition.Server(Config.Root.Path(WorkspaceSpool.DirectoryName), server), Time, Warnings.Add, spoolCap);
        Store  = StoreFor(server, Spool);
    }

    T Own<T>(T item) where T : IDisposable {
        _owned.Add(item);
        return item;
    }

    public GitRepo Repo { get; }

    public TempConfigRoot Config { get; }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public WorkspaceBounds Bounds { get; }

    public WorkspaceSpool Spool { get; }

    public WorkspaceSpanStore Store { get; }

    public List<string> Warnings { get; } = [];

    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public WorkspaceSpanStore StoreFor(string server, WorkspaceSpool spool) =>
        new(Config.Root, server, spool, new WorkspaceSnapshotter(new WorkspaceGit(Time), Time, Bounds), Time);

    public Task<bool> OpenAsync(string? dir = null, WorkspaceOpen mode = WorkspaceOpen.Turn) =>
        Store.OpenAsync(Session, dir ?? Repo.Path, mode, Budget);

    public Task<bool> CloseAsync() => Store.CloseAllAsync(Session, Budget);

    /// <summary>The span opened over the rig's repository, closed after <paramref name="turn"/> ran.</summary>
    public async Task<List<WorkspaceChangesRequest>> SpanAsync(Action turn) {
        await Assert.That(await OpenAsync()).IsTrue();
        turn();
        await CloseAsync();
        return Spooled();
    }

    public async Task<Dictionary<string, string>> ChangesAsync(Action turn) {
        var spooled = await SpanAsync(turn);
        return spooled.SelectMany(r => r.Changes).ToDictionary(c => c.Path, c => c.ChangeType);
    }

    public List<WorkspaceChangesRequest> Spooled(WorkspaceSpool? spool = null) => SpoolFiles(spool)
        .Select(f => JsonSerializer.Deserialize(File.ReadAllText(f), WorkspaceJsonContext.Default.WorkspaceChangesRequest)!)
        .ToList();

    public List<string> SpoolFiles(WorkspaceSpool? spool = null) {
        var dir = (spool ?? Spool).DirectoryPath;
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public string SessionSpanDir => Path.Combine(Store.DirectoryPath, Session);

    public List<string> Baselines() =>
        Directory.Exists(SessionSpanDir) ? Directory.EnumerateFiles(SessionSpanDir, "*.json").ToList() : [];

    public void Write(string path, string content) => Repo.CreateFile(path, content);

    public void Delete(string path) => File.Delete(Repo.PathTo(path));

    public void Dispose() {
        foreach (var item in Enumerable.Reverse(_owned)) item.Dispose();
    }
}
