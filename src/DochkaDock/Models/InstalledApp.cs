namespace DochkaDock.Models;

/// <summary>An app discovered from the Start Menu, offered in the "add
/// application" picker.</summary>
public sealed record InstalledApp(string DisplayName, string Path);
