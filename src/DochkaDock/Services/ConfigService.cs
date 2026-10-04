using System.IO;
using System.Text.Json;
using DochkaDock.Models;

namespace DochkaDock.Services;

/// <summary>Loads/saves the dock layout. Never throws: a missing or corrupt
/// config simply falls back to sensible defaults so a bad write can't brick
/// startup.</summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }

    public ConfigService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DochkaDock");
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
    }

    public DockConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<DockConfig>(json, JsonOptions);
                if (config is not null)
                    return config;
            }
        }
        catch
        {
            // Corrupt or unreadable config — fall through to defaults rather than crash.
        }

        return CreateDefaultConfig();
    }

    public void Save(DockConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Copy(tempPath, ConfigPath, overwrite: true);
            File.Delete(tempPath);
        }
        catch
        {
            // Best-effort persistence: a failed save shouldn't crash the dock.
        }
    }

    private static DockConfig CreateDefaultConfig()
    {
        var config = new DockConfig();
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        void AddIfExists(string path, string name)
        {
            if (File.Exists(path))
            {
                config.Items.Add(new DockItemData
                {
                    Kind = DockItemKind.Shortcut,
                    Path = path,
                    DisplayName = name
                });
            }
        }

        AddIfExists(Path.Combine(windir, "explorer.exe"), "File Explorer");
        AddIfExists(Path.Combine(windir, "ImmersiveControlPanel", "SystemSettings.exe"), "Settings");
        AddIfExists(Path.Combine(windir, "System32", "notepad.exe"), "Notepad");

        config.Items.Add(new DockItemData
        {
            Kind = DockItemKind.RecycleBin,
            Path = "::{645FF040-5081-101B-9F08-00AA002F954E}",
            DisplayName = "Recycle Bin"
        });

        return config;
    }
}
