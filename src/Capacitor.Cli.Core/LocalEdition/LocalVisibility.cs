namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The default session visibility on the local edition, whose one user has no audience to choose.</summary>
public static class LocalVisibility {
    public const string Default = "private";

    /// <summary>The visibility a local installation records with, given the profile's.</summary>
    /// <remarks>A profile that never had <c>default_visibility</c> set reads as <c>org_public</c>, and nothing offers
    /// that value on the local edition, so it stands for no choice and maps to <see cref="Default"/>. Any other value
    /// was chosen and is kept.</remarks>
    public static string For(string profileVisibility) =>
        string.Equals(profileVisibility, "org_public", StringComparison.OrdinalIgnoreCase) ? Default : profileVisibility;
}
