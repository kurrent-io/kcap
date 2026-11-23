using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

/// <summary>Builds a throwaway OpenCode-shaped SQLite db for import tests: v1's tables, v2's as OpenCode 2.3.2 creates
/// them, or both, as a v1 db that v2 has opened holds.</summary>
internal sealed class OpenCodeDbFixture : IDisposable {
    readonly TempDir _tmp = new();
    public string Dir        => _tmp.Path;
    public string DbPath     => _tmp.PathTo("opencode.db");
    public string LedgerPath => _tmp.PathTo("ledger.json"); // isolates each test from the real ~/.cache ledger

    public OpenCodeDbFixture(bool v1 = true, bool v2 = false) {
        using var c = Open();
        if (v2) Exec(c, V2Tables);
        if (!v1) return;
        Exec(c, """
            CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT,
                title TEXT NOT NULL, version TEXT NOT NULL DEFAULT '', model TEXT,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            """);
    }

    const string V2Tables = """
        CREATE TABLE `session_v2` (
          `id` text PRIMARY KEY, `project_id` text NOT NULL, `workspace_id` text, `parent_id` text,
          `fork_session_id` text, `fork_boundary` text, `slug` text NOT NULL, `directory` text NOT NULL, `path` text,
          `title` text, `version` text NOT NULL, `share_url` text, `summary_additions` integer, `summary_deletions` integer,
          `summary_files` integer, `summary_diffs` text, `metadata` text, `cost` real DEFAULT 0 NOT NULL,
          `tokens_input` integer DEFAULT 0 NOT NULL, `tokens_output` integer DEFAULT 0 NOT NULL,
          `tokens_reasoning` integer DEFAULT 0 NOT NULL, `tokens_cache_read` integer DEFAULT 0 NOT NULL,
          `tokens_cache_write` integer DEFAULT 0 NOT NULL, `revert` text, `permission` text, `agent` text, `model` text,
          `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `time_busy` integer, `time_idle` integer,
          `time_viewed` integer, `idle_outcome` text, `time_compacting` integer, `time_archived` integer,
          `time_suspended` integer, `resume_attempts` integer DEFAULT 0 NOT NULL);
        CREATE TABLE `session_message` (
          `id` text PRIMARY KEY, `session_id` text NOT NULL, `type` text NOT NULL, `seq` integer NOT NULL,
          `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `data` text NOT NULL);
        CREATE UNIQUE INDEX `session_message_session_seq_idx` ON `session_message` (`session_id`,`seq`);
        CREATE TABLE `session_part` (
          `message_id` text NOT NULL, `session_id` text NOT NULL, `ordinal` integer NOT NULL, `type` text NOT NULL,
          `call_id` text, `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `data` text NOT NULL,
          CONSTRAINT `session_part_pk` PRIMARY KEY(`message_id`, `ordinal`));
        """;

    public void AddV2Session(string id, string? parent, string dir, string? title, long t) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session_v2(id,project_id,parent_id,slug,directory,title,version,time_created,time_updated) " +
                          "VALUES($i,'global',$p,$i,$d,$t,'2.3.2',$tc,$tc)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", dir);
        cmd.Parameters.AddWithValue("$t", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tc", t);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A v2 message row: its data is the message without its id and type, which are columns.</summary>
    public void AddV2Message(string sid, string id, string type, int seq, long t, JsonObject data) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session_message(id,session_id,type,seq,time_created,time_updated,data) VALUES($i,$s,$ty,$q,$t,$t,$d)";
        cmd.Parameters.AddWithValue("$i", id); cmd.Parameters.AddWithValue("$s", sid);
        cmd.Parameters.AddWithValue("$ty", type); cmd.Parameters.AddWithValue("$q", seq);
        cmd.Parameters.AddWithValue("$t", t); cmd.Parameters.AddWithValue("$d", data.ToJsonString());
        cmd.ExecuteNonQuery();
    }

    public void AddV2Part(string sid, string messageId, int ordinal, JsonObject part) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session_part(message_id,session_id,ordinal,type,call_id,time_created,time_updated,data) VALUES($m,$s,$o,$ty,$ci,0,0,$d)";
        cmd.Parameters.AddWithValue("$m", messageId); cmd.Parameters.AddWithValue("$s", sid);
        cmd.Parameters.AddWithValue("$o", ordinal); cmd.Parameters.AddWithValue("$ty", (string)part["type"]!);
        cmd.Parameters.AddWithValue("$ci", (object?)(string?)part["id"] ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", part.ToJsonString());
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() { var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); return c; }
    static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    // dir is nullable so tests can cover a session with no directory (DBNull).
    public void AddSession(string id, string? parent, string? dir, string title, long t) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) VALUES($i,$p,$d,$t,'1.17',$tc,$tc)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)dir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$tc", t);
        cmd.ExecuteNonQuery();
    }

    // Build JSON with JsonObject so quoting/escaping is correct and readable.
    public void AddMessageWithText(string sid, string msgId, string text, long t, string? agent = null) {
        var info = new JsonObject {
            ["role"] = "user",
            ["time"] = new JsonObject { ["created"] = t },
        };
        if (agent is not null) info["agent"] = agent;
        var part = new JsonObject { ["id"] = "prt_" + msgId, ["type"] = "text", ["text"] = text };
        InsertRaw(sid, msgId, t, info.ToJsonString(), "prt_" + msgId, part.ToJsonString());
    }

    public void AddMessageWithTextAndAgent(string sid, string msgId, string text, long t, string agent) =>
        AddMessageWithText(sid, msgId, text, t, agent);

    public void AddStructuralMessage(string sid, string msgId, long t) {
        var info = new JsonObject { ["role"] = "assistant" };
        var part = new JsonObject { ["type"] = "step-finish" };
        InsertRaw(sid, msgId, t, info.ToJsonString(), "prt_" + msgId, part.ToJsonString());
    }

    void InsertRaw(string sid, string msgId, long t, string msgData, string partId, string partData) {
        using var c = Open();
        using (var m = c.CreateCommand()) {
            m.CommandText = "INSERT INTO message(id,session_id,time_created,data) VALUES($i,$s,$t,$d)";
            m.Parameters.AddWithValue("$i", msgId); m.Parameters.AddWithValue("$s", sid);
            m.Parameters.AddWithValue("$t", t); m.Parameters.AddWithValue("$d", msgData);
            m.ExecuteNonQuery();
        }
        using (var p = c.CreateCommand()) {
            p.CommandText = "INSERT INTO part(id,message_id,session_id,time_created,data) VALUES($i,$m,$s,$t,$d)";
            p.Parameters.AddWithValue("$i", partId); p.Parameters.AddWithValue("$m", msgId);
            p.Parameters.AddWithValue("$s", sid); p.Parameters.AddWithValue("$t", t);
            p.Parameters.AddWithValue("$d", partData);
            p.ExecuteNonQuery();
        }
    }

    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}
