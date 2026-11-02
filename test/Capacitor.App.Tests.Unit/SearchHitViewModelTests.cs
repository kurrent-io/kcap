using Avalonia.Automation;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class SearchHitViewModelTests {
    static SessionSearchHitDto Hit(string? displayName, string? user) => new() {
        SessionId = "S1", Slug = "fix-auth", Title = "Fix auth flow", StartedAt = "2026-10-29T09:00:00Z",
        MatchedField = "title", Snippet = "Fix auth flow", OwnerDisplayName = displayName, OwnerUser = user,
    };

    [Test]
    [Arguments("Wendy Ng", null, AccessibilityView.Content)]
    [Arguments(null, "wendy", AccessibilityView.Content)]
    [Arguments(null, null, AccessibilityView.Raw)]
    public async Task The_avatar_is_a_control_element_only_when_it_has_an_owner_to_name(string? displayName, string? user, AccessibilityView expected) {
        var hit = SearchHitViewModel.ForSession(Hit(displayName, user), DateTimeOffset.UnixEpoch);

        await Assert.That(hit.OwnerAvatarView).IsEqualTo(expected);
    }
}
