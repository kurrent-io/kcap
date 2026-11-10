namespace Capacitor.App.Services.CliAdoption;

/// <summary>What an entry the privileged helper deletes must be: a link is removed as a link, never its target.</summary>
public enum PrivilegedEntryKind { Directory, Link, File }
