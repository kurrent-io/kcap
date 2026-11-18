using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>Teaches the agent the importance marks the desktop app fades or hides, while the profile has
/// the need-to-know-basis experiment on. Off, the agent never sees the convention, because a reader
/// without the app would see the corner brackets as noise.</summary>
static class NeedToKnowMarksNudge {
    public const string Text =
        "## Need to know basis\n" +
        "The person reading your replies can fade or hide their less important parts. In chat replies to them, " +
        "grade what you write by one test: how much do they need to know this?\n" +
        "- Leave must-know text unmarked. Every paragraph, bullet, heading and table cell starts at must know.\n" +
        "- Wrap worth-knowing text in ⌞ and ⌟, and only-if-curious text in ⌞⌞ and ⌟⌟. A curious span can sit inside a worth-knowing one.\n" +
        "- A marked span must be removable: start and end it at sentence or clause boundaries, so what is left still " +
        "reads correctly when it is hidden. Wrap a whole paragraph or bullet to grade all of it.\n" +
        "- Use the marks only in chat replies: never in files, code, commit messages, PR descriptions, comments or any other tool input.\n" +
        "Example: `Tests pass. ⌞7 new and 33 existing tests ran.⌟ CI is green. ⌞⌞The launcher suite takes 40 s.⌟⌟`";

    public static string? Resolve(ConfigRoot? config, string? profileName) =>
        config is not null && !string.IsNullOrEmpty(profileName)
        && new ExperimentalSettingsStore(config).Load(profileName).IsOn(ExperimentalFeatures.NeedToKnowBasis)
            ? Text
            : null;
}
