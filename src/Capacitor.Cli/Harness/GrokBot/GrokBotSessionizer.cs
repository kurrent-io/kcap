using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>
/// Cuts one Bot's endless thread into sessions: a session opens at the first entry after an idle gap and
/// ends once the gap has passed with no new entry and no turn running. Boundaries depend only on entry
/// timestamps, so a restarted poller derives the same sessions with the same ids.
///
/// <para>An entry is sent only once it has settled — not streaming, and a question or permission ask only
/// once answered, skipped, or left behind by a later message from the user — because the server keeps the
/// first copy of a line number and drops a resend. An entry older than the gap counts as settled whatever
/// its state, so an answer given after either of those is not recorded.</para>
///
/// <para>A chat first seen without its beginning is recorded from its next quiet gap on: its oldest entry
/// may sit mid-session, and a session opened there would not be the one a reader holding the whole chat
/// opens for the same messages.</para>
/// </summary>
public static class GrokBotSessionizer {
    public static IReadOnlyList<GrokBotAction> Plan(
            string                    agentId,
            GrokBotBotState           state,
            IEnumerable<GrokBotEntry> entries,
            long                      nowMs,
            bool                      isRunningTurn,
            TimeSpan                  gap,
            bool                      fromThreadStart = true,
            int                       maxBatch = 100
        ) {
        var gapMs   = (long)gap.TotalMilliseconds;
        var actions = new List<GrokBotAction>();
        var lines   = new List<string>();
        var seqs    = new List<int>();
        var pending = state.LastSeq;

        var fresh = entries.Where(e => e.Seq > state.LastSeq).DistinctBy(e => e.Seq).OrderBy(e => e.Seq).ToList();

        if (state.LastSeq < 0 && state.Open is { } opened) {
            // The start was accepted and no line yet: the retry passes over what the start passed over,
            // by position, since the source may have dropped the opening entry itself by now.
            fresh = [.. fresh.Where(e => e.Seq >= opened.FirstSeq)];
        } else if (state.LastSeq < 0 && !fromThreadStart) {
            var boundary = Enumerable.Range(1, Math.Max(fresh.Count - 1, 0)).FirstOrDefault(i => fresh[i].TimestampMs - fresh[i - 1].TimestampMs >= gapMs);

            fresh = boundary > 0 ? fresh[boundary..] : [];
        }

        GrokBotEntry? held = null;

        for (var i = 0; i < fresh.Count; i++) {
            var entry = fresh[i];
            var stale = nowMs - entry.TimestampMs >= gapMs;

            if (!stale && (entry.IsStreaming || (entry.IsAwaitingAnswer && !fresh.Skip(i + 1).Any(later => later.StartsTurn)))) {
                held = entry;
                break;
            }

            if (state.Open is { } open && entry.TimestampMs - open.LastEntryMs >= gapMs) {
                Flush();
                state = state with { Open = null };
                actions.Add(new GrokBotEndSession(open.SessionId, open.LastEntryMs, state));
            }

            if (state.Open is null) {
                state = state with { Open = new GrokBotOpenSession(SessionId(agentId, entry.Id), entry.Id, entry.TimestampMs, entry.Seq, entry.TimestampMs) };
                actions.Add(new GrokBotStartSession(state.Open.SessionId, entry.TimestampMs, state));
            }

            lines.Add(entry.Line);
            seqs.Add(entry.Seq);
            pending = entry.Seq;
            state   = state with { Open = state.Open with { LastEntryMs = entry.TimestampMs } };

            if (lines.Count >= maxBatch) Flush();
        }

        Flush();

        // An entry held back inside the gap still belongs to the open session, which must be there to take it.
        var awaited = state.Open is { } current && held is not null && held.TimestampMs - current.LastEntryMs < gapMs;

        if (!isRunningTurn && !awaited && state.Open is { } idle && nowMs - idle.LastEntryMs >= gapMs) {
            state = state with { Open = null };
            actions.Add(new GrokBotEndSession(idle.SessionId, idle.LastEntryMs, state));
        }

        return actions;

        void Flush() {
            if (lines.Count == 0) return;

            state = state with { LastSeq = pending };
            actions.Add(new GrokBotSendLines(state.Open!.SessionId, [.. lines], [.. seqs], state));
            lines.Clear();
            seqs.Clear();
        }
    }

    /// <summary>Whether a chat must be read this cycle. An unchanged chat is not, except when its open
    /// session has sat past the gap: closing it on what was read earlier would miss an entry still held
    /// back inside it, and a session reopened for that entry would split one conversation in two.</summary>
    public static bool NeedsRead(GrokBotBotState state, bool changed, long nowMs, TimeSpan gap) =>
        changed || state.Open is { } open && nowMs - open.LastEntryMs >= (long)gap.TotalMilliseconds;

    /// <summary>32 lowercase hex: a session id the server accepts, derived from data a restarted poller
    /// re-reads. Persisted as the session's identity, so the derivation never changes.</summary>
    public static string SessionId(string agentId, string firstEntryId) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"grok-bot:{agentId}:{firstEntryId}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
