using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Ide;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>The stdio loop answers each request under its own id, one at a time, and every write is a whole line.</summary>
public class IdeRpcLoopTests {
    sealed class Lines : TextWriter {
        readonly ConcurrentQueue<string> _lines = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(string? value) {
            _lines.Enqueue(value!);
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> Raw => [.. _lines];

        public IReadOnlyList<JsonObject> All => [.. _lines.Select(l => JsonNode.Parse(l)!.AsObject())];
    }

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
        var output = await RunAsync(Request("1", "no/such"), _ => { });

        var error = output.All.Single()["error"]!;
        await Assert.That(error["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.MethodNotFound);
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
    public async Task Unparseable_and_blank_lines_are_skipped() {
        var output = await RunAsync(
            string.Join('\n', "{not json", "", "   ", "[1,2]", """{"jsonrpc":"2.0","method":"note"}""", Request("1", "fine")),
            loop => {
                loop.Handle("note", (_, _) => throw new InvalidOperationException("a notification must not run a request handler"));
                loop.Handle("fine", (_, _) => Task.FromResult<JsonNode?>(null));
            });

        await Assert.That(output.All.Single()["id"]!.GetValue<int>()).IsEqualTo(1);
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
        await Assert.That(all.Count).IsEqualTo(201);
        await Assert.That(all.Count(l => l["method"]?.GetValue<string>() == "tick" && !l.ContainsKey("id"))).IsEqualTo(200);
        await Assert.That(all.Count(l => l["id"] is not null)).IsEqualTo(1);
    }

    [Test]
    public async Task Run_returns_at_end_of_input() {
        var loop = new IdeRpcLoop(new StringReader(""), new Lines());

        await loop.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }
}
