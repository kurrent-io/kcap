using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap pass</c> and <c>kcap message</c> share: the server's say on whether either exists, picking the
/// recipient, and sending. Sharing is approved only by an answer typed at a terminal; there is no flag for it.</summary>
static class PassSending {
    /// <summary>Null when passes are offered; otherwise the exit code, having answered as for an unknown command. A server
    /// that cannot say is treated as not offering them, so the command's existence is never revealed by an error.</summary>
    public static async Task<int?> HiddenAsync(IPassesApi passes, string command, PassTerminal terminal) {
        try {
            if (await passes.PassesOfferedAsync()) return null;
        } catch (CapacitorApiException) {
            // Fall through to the unknown-command answer.
        }

        return UnknownCommand.Write(terminal.Error, command);
    }

    /// <summary>An exact id wins; otherwise the name must match exactly one colleague.</summary>
    public static async Task<UserCandidateDto?> RecipientAsync(IPassesApi passes, string who, PassTerminal terminal) {
        var candidates = await passes.SearchColleaguesAsync(who);

        if (candidates.FirstOrDefault(c => string.Equals(c.Id, who, StringComparison.Ordinal)) is { } exact) return exact;

        switch (candidates.Count) {
            case 1:
                return candidates[0];
            case 0:
                await terminal.Error.WriteLineAsync($"No colleague matches '{who}'.");

                return null;
            default:
                await terminal.Error.WriteLineAsync($"Several colleagues match '{who}'. Name one by id:");
                foreach (var c in candidates) await terminal.Error.WriteLineAsync($"  {c.Id}  {c.DisplayName}");

                return null;
        }
    }

    public static async Task<int> SendAsync(IPassesApi passes, string serverUrl, StartPassBody body, string recipientName, PassTerminal terminal) {
        switch (await passes.SendAsync(body)) {
            case PassSendResult.Sent:
                await terminal.Out.WriteLineAsync($"Sent to {recipientName}.");

                return 0;
            case PassSendResult.AwaitingApproval { Pass: var pass }:
                return await ApproveAsync(passes, serverUrl, pass, terminal);
            case PassSendResult.Refused { Refusal: var refusal }:
                return await RefusedAsync(refusal, terminal);
            case PassSendResult.NotFound:
                await terminal.Error.WriteLineAsync("Not sent: no such item, or not one you can see.");

                return 1;
            default:
                throw new InvalidOperationException("Unknown send result");
        }
    }

    static async Task<int> ApproveAsync(IPassesApi passes, string serverUrl, StartPassResponseDto pass, PassTerminal terminal) {
        await terminal.Error.WriteLineAsync(pass.ApprovalText ?? "This pass needs your approval to share.");
        foreach (var grant in pass.Grants ?? [])
            await terminal.Error.WriteLineAsync($"  - {grant.Access} ({grant.ItemKind.Replace('_', ' ')} {grant.ItemId})"
                                              + (grant.AlsoAllows is { Length: > 0 } also ? $"; it also lets them {also}" : ""));

        if (!terminal.Interactive) {
            await terminal.Out.WriteLineAsync($"{McpPassServer.WaitingForApproval}: {PassLinks.Sent(serverUrl)}");

            return 0;
        }

        await terminal.Error.WriteAsync("Share and send? [y/N] ");
        var yes = terminal.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";

        var result = yes ? await passes.ApproveAsync(pass.PassId, "cli") : await passes.DeclineAsync(pass.PassId);

        if (result is PassAnswerResult.Refused { Refusal: var refusal }) return await RefusedAsync(refusal, terminal);

        var answer = ((PassAnswerResult.Answered)result).Answer;

        await terminal.Out.WriteLineAsync(answer.Outcome switch {
            "sent"     => "Shared and sent.",
            "declined" => "Declined: nothing was shared or sent.",
            "expired"  => "The approval expired: nothing was shared or sent.",
            "voided"   => $"Not sent: {answer.Reason ?? "a grant could not be made"}. Sharing already made stays.",
            "pending"  => "Still waiting: another answer to this pass is in progress.",
            _          => $"The pass is {answer.State}."
        });

        return answer.Outcome is "sent" or "declined" ? 0 : 1;
    }

    static async Task<int> RefusedAsync(PassRefusalDto refusal, PassTerminal terminal) {
        await terminal.Error.WriteLineAsync($"Not sent: {refusal.Reason}");
        if (refusal.ShareUrl is { } url) await terminal.Error.WriteLineAsync($"  Sharing: {url}");

        return 1;
    }
}
