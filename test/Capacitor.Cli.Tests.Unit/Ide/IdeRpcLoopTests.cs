using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Ide;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>The stdio loop answers each request under its own id, one at a time, and every write is a whole line.</summary>
public class IdeRpcLoopTests {
    /// Takes each message in two halves with a yield between them, so writes that overlap interleave and show up
    /// both in <see cref="Overlapped"/> and as lines that no longer parse.
    sealed class Lines : TextWriter {
        readonly StringBuilder _text = new();
        int _active;
        int _writes;
        int _flushes;

        public override Encoding Encoding => Encoding.UTF8;

        public Func<Exception?>? FailWrite { get; set; }

        public bool Overlapped { get; private set; }

        public int Writes => Volatile.Read(ref _writes);

        public int Flushes => Volatile.Read(ref _flushes);

        public string Text { get { lock (_text) return _text.ToString(); } }

        public IReadOnlyList<string> Raw => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        public IReadOnlyList<JsonObject> All => [.. Raw.Select(l => JsonNode.Parse(l)!.AsObject())];

        public override async Task WriteAsync(string? value) {
            Interlocked.Increment(ref _writes);
            if (Interlocked.Increment(ref _active) > 1) Overlapped = true;
            try {
                if (FailWrite?.Invoke() is { } failure) throw failure;

                var half = value!.Length / 2;
                lock (_text) _text.Append(value, 0, half);
                await Task.Yield();
                lock (_text) _text.Append(value, half, value.Length - half);
            } finally {
                Interlocked.Decrement(ref _active);
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken) {
            Interlocked.Increment(ref _flushes);
            return Task.CompletedTask;
        }
    }

    static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-fixtures", "ide-protocol", name)))!.AsObject();

    static string Request(string id, string method, string? parameters = null) =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"{{(parameters is null ? "" : $",\"params\":{parameters}")}}}""";

    static async Task<Lines> RunAsync(string input, Action<IdeRpcLoop> register) {
        var output = new Lines();
        var loop   = new IdeRpcLoop(new StringReader(input), output);
        register(loop);
        await loop.RunAsync(CancellationToken.None);

        return output;
    }

    [Test]
    public async Task Request_gets_the_handler_result_under_its_id() {
        JsonObject? seen = null;
        var output = await RunAsync(
            string.Join('\n', Request("7", "echo", """{"a":1}"""), Request("\"x-1\"", "echo"), Request("8", "nothing")),
            loop => {
                loop.Handle("echo", (p, _) => { seen ??= p; return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true }); });
                loop.Handle("nothing", (_, _) => Task.FromResult<JsonNode?>(null));
            });

        var all = output.All;
        await Assert.That(all.Count).IsEqualTo(3);
        await Assert.That(all[0].ToJsonString()).IsEqualTo("""{"jsonrpc":"2.0","id":7,"result":{"ok":true}}""");
        await Assert.That(all[1]["id"]!.GetValue<string>()).IsEqualTo("x-1");
        await Assert.That(all[2].ToJsonString()).IsEqualTo("""{"jsonrpc":"2.0","id":8,"result":{}}""");
        await Assert.That(seen!["a"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Unknown_method_answers_32601() {
        var expected = Fixture("error.method_not_found.json");

        var output = await RunAsync(Request(expected["id"]!.ToJsonString(), "no/such"), _ => { });

        await Assert.That(JsonNode.DeepEquals(output.All.Single(), expected)).IsTrue();
    }

    [Test]
    public async Task Handler_exception_answers_32603_and_the_loop_continues() {
        var output = await RunAsync(
            string.Join('\n', Request("1", "boom"), Request("2", "fine")),
            loop => {
                loop.Handle("boom", (_, _) => throw new InvalidOperationException("secret detail"));
                loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));
            });

        var all = output.All;
        await Assert.That(all[0]["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.InternalError);
        await Assert.That(output.Raw[0]).DoesNotContain("secret detail");
        await Assert.That(all[1]["result"]).IsNotNull();
    }

    [Test]
    public async Task IdeRpcException_answers_with_its_code_message_and_data() {
        var output = await RunAsync(
            Request("5", "pick"),
            loop => loop.Handle("pick", (_, _) =>
                throw new IdeRpcException(IdeErrorCodes.InvalidParams, "bad", new JsonObject { ["field"] = "scope" })));

        await Assert.That(output.All.Single().ToJsonString()).IsEqualTo(
            """{"jsonrpc":"2.0","id":5,"error":{"code":-32602,"message":"bad","data":{"field":"scope"}}}""");
    }

    [Test]
    public async Task A_blank_line_is_skipped() {
        var output = await RunAsync(
            string.Join('\n', "", "   ", Request("1", "fine")),
            loop => loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null)));

        await Assert.That(output.Text).IsEqualTo("""{"jsonrpc":"2.0","id":1,"result":{}}""" + "\n");
    }

    [Test]
    public async Task An_unparseable_line_answers_32700_with_a_null_id() {
        const string parseError = """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"parse_error"}}""";
        var output = await RunAsync(
            string.Join('\n', "{not json", "[1,2]", "\"text\"", """{"id":1,"id":2,"method":"fine"}""", Request("3", "fine")),
            loop => loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null)));

        await Assert.That(output.Raw).IsEquivalentTo(
            [parseError, parseError, parseError, parseError, """{"jsonrpc":"2.0","id":3,"result":{}}"""], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_request_without_a_method_answers_32600() {
        var output = await RunAsync(
            string.Join('\n', """{"jsonrpc":"2.0","id":4}""", """{"jsonrpc":"2.0","id":"x","method":5,"params":{}}""", Request("6", "fine")),
            loop => loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null)));

        await Assert.That(output.Raw).IsEquivalentTo([
            """{"jsonrpc":"2.0","id":4,"error":{"code":-32600,"message":"invalid_request"}}""",
            """{"jsonrpc":"2.0","id":"x","error":{"code":-32600,"message":"invalid_request"}}""",
            """{"jsonrpc":"2.0","id":6,"result":{}}""",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_response_from_the_peer_is_ignored() {
        var output = await RunAsync(
            string.Join('\n',
                """{"jsonrpc":"2.0","id":4,"result":{}}""",
                """{"jsonrpc":"2.0","id":5,"result":null}""",
                """{"jsonrpc":"2.0","id":6,"error":{"code":-32601,"message":"method_not_found"}}""",
                Request("7", "fine")),
            loop => loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null)));

        await Assert.That(output.Text).IsEqualTo("""{"jsonrpc":"2.0","id":7,"result":{}}""" + "\n");
    }

    [Test]
    public async Task A_notification_from_the_peer_runs_no_handler() {
        var output = await RunAsync(
            string.Join('\n', """{"jsonrpc":"2.0","method":"note"}""", Request("1", "fine")),
            loop => {
                loop.Handle("note", (_, _) => throw new InvalidOperationException("a notification must not run a request handler"));
                loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));
            });

        await Assert.That(output.Text).IsEqualTo("""{"jsonrpc":"2.0","id":1,"result":{}}""" + "\n");
    }

    [Test]
    public async Task Notifications_written_during_a_request_are_whole_lines() {
        var output = new Lines();
        var loop   = new IdeRpcLoop(new StringReader(Request("1", "slow")), output);
        loop.Handle("slow", async (_, ct) => {
            var burst = Enumerable.Range(0, 200).Select(i => Task.Run(() => loop.NotifyAsync("tick", new JsonObject { ["n"] = i, ["pad"] = new string('x', 500) })));
            await Task.WhenAll(burst);
            await Task.Delay(10, ct);

            return new JsonObject { ["done"] = true };
        });

        await loop.RunAsync(CancellationToken.None);

        var all = output.All;
        await Assert.That(output.Overlapped).IsFalse();
        await Assert.That(all.Count).IsEqualTo(201);
        await Assert.That(all.Count(l => l["method"]?.GetValue<string>() == "tick" && !l.ContainsKey("id"))).IsEqualTo(200);
        await Assert.That(all.Count(l => l["id"] is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task Run_returns_at_end_of_input() {
        var loop = new IdeRpcLoop(new StringReader(""), new Lines());

        await loop.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Each_message_ends_with_a_single_line_feed() {
        var output = new Lines { NewLine = "\r\n" };
        var loop   = new IdeRpcLoop(new StringReader(Request("1", "fine")), output);
        loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));

        await loop.RunAsync(CancellationToken.None);
        await loop.NotifyAsync("tick", new JsonObject());

        await Assert.That(output.Text).DoesNotContain("\r");
        await Assert.That(output.Text.Count(c => c == '\n')).IsEqualTo(2);
        await Assert.That(output.Text).EndsWith("\n");
    }

    [Test]
    public async Task Each_message_is_flushed() {
        var output = new Lines();
        var loop   = new IdeRpcLoop(new StringReader(string.Join('\n', Request("1", "fine"), Request("2", "fine"))), output);
        loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));

        await loop.RunAsync(CancellationToken.None);
        await loop.NotifyAsync("tick", new JsonObject());

        await Assert.That(output.Flushes).IsEqualTo(3);
    }

    [Test]
    public async Task Run_returns_when_the_peer_stops_reading() {
        var output  = new Lines { FailWrite = () => new IOException("broken pipe") };
        var handled = 0;
        var loop    = new IdeRpcLoop(new StringReader(string.Join('\n', Request("1", "fine"), Request("2", "fine"), Request("3", "fine"))), output);
        loop.Handle("fine", (_, _) => { handled++; return Task.FromResult<JsonNode?>(null); });

        await loop.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(handled).IsEqualTo(1);
    }

    [Test]
    public async Task Notify_after_the_peer_is_gone_does_nothing() {
        var output = new Lines { FailWrite = () => new ObjectDisposedException("pipe") };
        var loop   = new IdeRpcLoop(new StringReader(Request("1", "fine")), output);
        loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));
        await loop.RunAsync(CancellationToken.None);
        var writes = output.Writes;

        await loop.NotifyAsync("tick", new JsonObject());

        await Assert.That(output.Writes).IsEqualTo(writes);
    }

    [Test]
    public async Task Notify_after_dispose_does_nothing() {
        var output = new Lines();
        var loop   = new IdeRpcLoop(new StringReader(""), output);
        loop.Dispose();

        await loop.NotifyAsync("tick", new JsonObject());

        await Assert.That(output.Writes).IsEqualTo(0);
    }

    [Test]
    public async Task A_write_failure_other_than_a_closed_peer_propagates() {
        var output = new Lines { FailWrite = () => new InvalidOperationException("bug") };
        var loop   = new IdeRpcLoop(new StringReader(Request("1", "fine")), output);
        loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));

        await Assert.That(() => loop.RunAsync(CancellationToken.None)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Registering_a_method_twice_throws() {
        var loop = new IdeRpcLoop(new StringReader(""), new Lines());
        loop.Handle("once", (_, _) => Task.FromResult<JsonNode?>(null));

        var ex = Assert.Throws<InvalidOperationException>(() => loop.Handle("once", (_, _) => Task.FromResult<JsonNode?>(null)));

        await Assert.That(ex.Message).Contains("once");
    }

    [Test]
    public async Task Registering_a_method_through_both_forms_throws() {
        var loop = new IdeRpcLoop(new StringReader(""), new Lines());
        loop.Handle("once", (_, _) => Task.FromResult<JsonNode?>(null));

        var ex = Assert.Throws<InvalidOperationException>(() => loop.HandleThen("once", (_, _) => Task.FromResult(new IdeReply(null))));

        await Assert.That(ex.Message).Contains("once");
    }

    [Test]
    public async Task Then_runs_once_its_reply_is_written() {
        var output      = new Lines();
        var loop        = new IdeRpcLoop(new StringReader(Request("1", "start")), output);
        var linesAtThen = -1;
        loop.HandleThen("start", (_, _) => Task.FromResult(new IdeReply(new JsonObject { ["ok"] = true }, async () => {
            linesAtThen = output.Raw.Count;
            await loop.NotifyAsync("after", new JsonObject());
        })));

        await loop.RunAsync(CancellationToken.None);

        var all = output.All;
        await Assert.That(linesAtThen).IsEqualTo(1);
        await Assert.That(all.Count).IsEqualTo(2);
        await Assert.That(all[0].ToJsonString()).IsEqualTo("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");
        await Assert.That(all[1]["method"]!.GetValue<string>()).IsEqualTo("after");
    }

    [Test]
    public async Task Then_comes_between_its_reply_and_the_next_reply_on_the_wire() {
        var output = new Lines();
        var loop   = new IdeRpcLoop(new StringReader(string.Join('\n', Request("1", "start"), Request("2", "fine"))), output);
        loop.HandleThen("start", (_, _) => Task.FromResult(new IdeReply(null, async () => {
            // A yield lets the next request overtake if the loop does not wait for the follow-up.
            await Task.Yield();
            await loop.NotifyAsync("after", new JsonObject());
        })));
        loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));

        await loop.RunAsync(CancellationToken.None);

        await Assert.That(output.Text).IsEqualTo(
            """
            {"jsonrpc":"2.0","id":1,"result":{}}
            {"jsonrpc":"2.0","method":"after","params":{}}
            {"jsonrpc":"2.0","id":2,"result":{}}

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task An_error_reply_sends_nothing_after_it() {
        var output = new Lines();
        var loop   = new IdeRpcLoop(new StringReader(Request("1", "start")), output);
        loop.HandleThen("start", (_, _) => throw new IdeRpcException(IdeErrorCodes.InvalidParams, "bad"));

        await loop.RunAsync(CancellationToken.None);

        var all = output.All;
        await Assert.That(all.Count).IsEqualTo(1);
        await Assert.That(all[0]["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.InvalidParams);
    }

    [Test]
    public async Task Then_does_not_run_once_the_peer_is_gone() {
        var output = new Lines { FailWrite = () => new IOException("broken pipe") };
        var ran    = false;
        var loop   = new IdeRpcLoop(new StringReader(Request("1", "start")), output);
        loop.HandleThen("start", (_, _) => Task.FromResult(new IdeReply(null, () => { ran = true; return Task.CompletedTask; })));

        await loop.RunAsync(CancellationToken.None);

        await Assert.That(ran).IsFalse();
    }

    [Test, NotInParallel]
    public async Task A_failing_Then_goes_to_stderr_and_the_loop_continues() {
        using var stderr = ConsoleOutput.StartErrorCapture();
        var output = new Lines();
        var loop   = new IdeRpcLoop(new StringReader(string.Join('\n', Request("1", "start"), Request("2", "fine"))), output);
        loop.HandleThen("start", (_, _) => Task.FromResult(new IdeReply(null, () => throw new InvalidOperationException("follow-up broke"))));
        loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));

        await loop.RunAsync(CancellationToken.None);

        var all = output.All;
        await Assert.That(all.Count).IsEqualTo(2);
        await Assert.That(all[1]["id"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(stderr.GetCapturedError()).Contains("follow-up broke");
        await Assert.That(output.Text).DoesNotContain("follow-up broke");
    }
}
