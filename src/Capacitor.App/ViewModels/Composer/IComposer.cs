using Capacitor.App.Services;

namespace Capacitor.App.ViewModels.Composer;

/// What a prompt card's view model supplies, so every composer gets every composer feature.
public interface IComposer : IAttachmentSink {
    ComposerPicker? Picker { get; }
}
