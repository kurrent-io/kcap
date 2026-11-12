using Capacitor.App.ViewModels.Composer;

namespace Capacitor.App.Services.Composer;

/// The sources behind every composer's picker, one set for the app's life.
public sealed class ComposerPickers(FileCatalog files, CommandCatalog commands) {
    /// <paramref name="context"/> is read on each keystroke; null while the composer has no local checkout.
    public ComposerPicker Create(Func<ComposerContext?> context) =>
        new(context, new SlashCompletion(commands.ListAsync), new MentionCompletion(files.InRepoAsync, files.InFolderAsync));
}
