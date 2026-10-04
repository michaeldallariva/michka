using Microsoft.Win32;

namespace DochkaDock.Services;

/// <summary>Manages the "start with Windows" registry entry. Uses only the
/// per-user Run key — no admin rights, no task scheduler.</summary>
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DochkaDock";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string existing &&
               string.Equals(existing.Trim('"'), GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
            key.SetValue(ValueName, $"\"{GetExecutablePath()}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string GetExecutablePath() =>
        // Assembly.Location always returns "" for a published single-file
        // app, so it's not a usable fallback here (and trips an analyzer
        // warning). ProcessPath is reliably populated for a normally
        // launched Windows process.
        Environment.ProcessPath ?? throw new InvalidOperationException("Could not determine the running executable's path.");
}
