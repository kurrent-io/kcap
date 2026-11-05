namespace Capacitor.App.Services.Voice;

/// What reads a command that matched none of the fixed phrases.
public enum VoiceUnderstanding {
    /// Nothing: it is sent to the agent as a prompt.
    Phrases,
    /// A small model served by Ollama on this machine, with Claude Haiku when it has no answer.
    Local,
    /// Claude Haiku through the Claude Code CLI, signed in as the user.
    Online,
}
