using Capacitor.App.ViewModels.Composer;

namespace Capacitor.App.Services.Composer;

public sealed class ComposerPickers(FileCatalog files, CommandCatalog commands) {
    public ComposerPicker Create(IObservable<ComposerContext?> contexts) =>
        new(contexts, new SlashCompletion(commands.ListAsync), new MentionCompletion(files.InRepoAsync, files.InFolderAsync));
}
