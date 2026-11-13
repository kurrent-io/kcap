using System.Reactive.Disposables;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// Unsent composer text per session, held for the app's lifetime: a workspace is torn down on
/// every navigation away, so the draft has to outlive its chat tab.
public sealed class ComposerDrafts {
    readonly Dictionary<string, (string Text, long Revision)> _drafts = new(StringComparer.Ordinal);
    // A draft a recall replaced while the stored draft is another text the user typed; the next
    // composer for the session takes it back. A nonzero revision ties it to a send still carrying it.
    readonly Dictionary<string, (string Text, long Revision)> _stashes = new(StringComparer.Ordinal);
    readonly List<(string Key, Action<string> OnChanged)> _watchers = [];
    long _revision;

    // The origin is part of the key: a local and a remote agent can share an id and are different agents.
    static string Key(AgentOrigin origin, string agentId) => $"{origin}:{AgentIds.Normalize(agentId) ?? agentId}";

    public string Get(AgentOrigin origin, string agentId) => TextAt(Key(origin, agentId));

    /// Identifies one draft wherever it moves, until anyone types: a send that settles after its
    /// tab is gone clears its draft by this, never over a later edit. Zero for no draft.
    public long Revision(AgentOrigin origin, string agentId) =>
        _drafts.TryGetValue(Key(origin, agentId), out var draft) ? draft.Revision : 0;

    /// The composer's own write, so its watcher is not told of it.
    public void Set(AgentOrigin origin, string agentId, string text) {
        if (text.Length == 0) _drafts.Remove(Key(origin, agentId));
        else _drafts[Key(origin, agentId)] = (text, ++_revision);
    }

    /// A draft a closing composer hands back. `sendRevision` belongs to a send still carrying this
    /// same text: the draft keeps it so that send's delivery still clears it. Zero takes a new one.
    public void Restore(AgentOrigin origin, string agentId, string text, long sendRevision) {
        if (text.Length == 0) _drafts.Remove(Key(origin, agentId));
        else _drafts[Key(origin, agentId)] = (text, sendRevision != 0 ? sendRevision : ++_revision);
    }

    /// `sendRevision` as for <see cref="Restore"/>.
    public void SetStash(AgentOrigin origin, string agentId, string text, long sendRevision) {
        if (text.Length > 0) _stashes[Key(origin, agentId)] = (text, sendRevision);
    }

    public string TakeStash(AgentOrigin origin, string agentId) =>
        _stashes.Remove(Key(origin, agentId), out var stash) ? stash.Text : "";

    /// Hands one session's draft to its other lane, keeping its revision.
    public void Move(AgentOrigin from, AgentOrigin to, string agentId) {
        var source = Key(from, agentId);
        var target = Key(to, agentId);
        if (_drafts.Remove(source, out var draft)) _drafts[target] = draft;
        else _drafts.Remove(target);
        if (_stashes.Remove(source, out var stash)) _stashes[target] = stash;
        else _stashes.Remove(target);
        Notify(source);
        Notify(target);
    }

    public void ClearDelivered(long revision) {
        if (revision == 0) return;
        foreach (var (key, stash) in _stashes) {
            if (stash.Revision != revision) continue;
            _stashes.Remove(key);
            break;
        }
        foreach (var (key, draft) in _drafts) {
            if (draft.Revision != revision) continue;
            _drafts.Remove(key);
            Notify(key);
            return;
        }
    }

    /// Tells a live composer when its draft changes under it.
    public IDisposable Watch(AgentOrigin origin, string agentId, Action<string> onChanged) {
        var watcher = (Key(origin, agentId), onChanged);
        _watchers.Add(watcher);
        return Disposable.Create(() => _watchers.Remove(watcher));
    }

    string TextAt(string key) => _drafts.TryGetValue(key, out var draft) ? draft.Text : "";

    void Notify(string key) {
        foreach (var (watched, onChanged) in _watchers.ToList())
            if (watched == key) onChanged(TextAt(key));
    }
}
