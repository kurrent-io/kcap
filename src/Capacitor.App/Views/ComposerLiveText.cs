using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

internal sealed class ComposerLiveText : TextBlock {
    protected override Type StyleKeyOverride => typeof(TextBlock);
    protected override AutomationPeer OnCreateAutomationPeer() => new LiveTextPeer(this);

    sealed class LiveTextPeer(ComposerLiveText text) : TextBlockAutomationPeer(text) {
        // The macOS live announcement requires a name that can be placed in its notification payload.
        protected override AutomationLiveSetting GetLiveSettingCore() =>
            !text.IsAttachedToVisualTree() || !text.IsEffectivelyVisible || string.IsNullOrWhiteSpace(GetName())
                ? AutomationLiveSetting.Off
                : base.GetLiveSettingCore();
    }
}
