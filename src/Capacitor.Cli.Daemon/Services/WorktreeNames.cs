namespace Capacitor.Cli.Daemon.Services;

/// <summary>Short <c>adjective-noun</c> names for daemon-owned worktree directories. Every word is three to five
/// letters and easy to say, spell and type, because people read these names aloud and type them into paths.</summary>
static class WorktreeNames {
    internal static readonly string[] Adjectives = [
        "bold", "brave", "brisk", "busy", "calm", "clean", "clear", "cool", "crisp", "eager",
        "easy", "epic", "fair", "fancy", "fast", "fine", "fond", "free", "fresh", "full",
        "fuzzy", "glad", "good", "grand", "green", "happy", "hardy", "jolly", "keen", "kind",
        "lucky", "lush", "merry", "mild", "neat", "new", "nice", "noble", "open", "pink",
        "plain", "plush", "proud", "pure", "quick", "quiet", "red", "rich", "ripe", "rosy",
        "safe", "shiny", "silky", "sleek", "smart", "snug", "soft", "still", "sunny", "sweet",
        "swift", "tall", "tidy", "tiny", "true", "vast", "warm", "wavy", "wild", "wise",
        "witty", "young", "zesty", "zippy"
    ];

    internal static readonly string[] Nouns = [
        "ant", "apple", "ball", "bean", "bear", "bee", "bell", "bike", "boat", "book",
        "bowl", "box", "bread", "bus", "cake", "camel", "car", "cat", "chair", "coat",
        "coin", "corn", "cow", "crab", "crow", "cup", "deer", "desk", "dog", "door",
        "drum", "duck", "egg", "fan", "fish", "fork", "fox", "frog", "goat", "hare",
        "hat", "hen", "honey", "horse", "jam", "jar", "jug", "key", "kite", "koala",
        "lamb", "lamp", "leaf", "lime", "lion", "map", "mat", "milk", "mole", "moth",
        "mouse", "mug", "nest", "note", "otter", "owl", "pan", "panda", "pear", "pen",
        "pie", "pig", "plum", "pot", "ring", "robin", "rope", "rug", "seal", "sheep",
        "ship", "shoe", "sock", "sofa", "spoon", "star", "swan", "table", "tea", "tent",
        "tiger", "tile", "toad", "toy", "tray", "tree", "vase", "whale", "wolf", "zebra"
    ];

    const int Attempts = 32;

    internal static string Generate(Random random) => $"{Pick(Adjectives, random)}-{Pick(Nouns, random)}";

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
