namespace Capacitor.Cli.Daemon.Services;

/// <summary>Memorable <c>adjective-verb-animal</c> names for daemon-owned worktree directories.</summary>
static class WorktreeNames {
    static readonly string[] Adjectives = [
        "amber", "bold", "brave", "bright", "brisk", "calm", "clever", "cosy", "crisp", "curious",
        "daring", "eager", "fair", "fancy", "gentle", "glad", "golden", "grand", "happy", "hardy",
        "humble", "jolly", "keen", "kind", "lively", "lucky", "merry", "mighty", "misty", "nimble",
        "noble", "polished", "proud", "quick", "quiet", "rapid", "rosy", "rustic", "shiny", "silent",
        "silver", "sleek", "smooth", "snowy", "spry", "steady", "sunny", "swift", "tidy", "trusty",
        "vivid", "warm", "wise", "witty", "zesty", "zippy"
    ];

    static readonly string[] Verbs = [
        "baking", "bouncing", "building", "chasing", "climbing", "coasting", "crafting", "dancing",
        "darting", "diving", "drifting", "dreaming", "floating", "flying", "foraging", "gliding",
        "hopping", "humming", "hunting", "jumping", "juggling", "leaping", "marching", "mending",
        "napping", "painting", "pondering", "prancing", "racing", "roaming", "rolling", "running",
        "sailing", "singing", "skating", "sliding", "soaring", "spinning", "sprinting", "strolling",
        "surfing", "swimming", "tinkering", "trotting", "wandering", "weaving", "whistling", "zooming"
    ];

    static readonly string[] Animals = [
        "badger", "beaver", "bison", "crane", "dolphin", "eagle", "falcon", "ferret", "finch", "fox",
        "gecko", "heron", "ibis", "jaguar", "koala", "lark", "lemur", "lynx", "marmot", "meerkat",
        "mole", "moose", "newt", "octopus", "orca", "otter", "owl", "panda", "pelican", "penguin",
        "puffin", "quail", "rabbit", "raven", "robin", "salmon", "seal", "sparrow", "squirrel", "stoat",
        "swan", "tapir", "tiger", "toucan", "turtle", "walrus", "weasel", "wombat", "wren", "yak", "zebra"
    ];

    const int Attempts = 32;

    internal static string Generate(Random random) =>
        $"{Pick(Adjectives, random)}-{Pick(Verbs, random)}-{Pick(Animals, random)}";

    /// <summary>A name <paramref name="isTaken"/> rejects is drawn again. After that many collisions a
    /// random suffix is added, so the caller always gets a name and never loops.</summary>
    internal static string PickFree(Func<string, bool> isTaken, Random? random = null) {
        random ??= Random.Shared;

        for (var i = 0; i < Attempts; i++) {
            var name = Generate(random);
            if (!isTaken(name)) return name;
        }

        string suffixed;
        do suffixed = $"{Generate(random)}-{random.Next(0x10000):x4}";
        while (isTaken(suffixed));

        return suffixed;
    }

    static string Pick(string[] words, Random random) => words[random.Next(words.Length)];
}
