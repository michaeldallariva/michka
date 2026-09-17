using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monitor.Storage;

/// <summary>
/// All persisted settings, stored on the device as <c>michka.conf</c> (JSON) next to the database.
/// Created on first run with defaults if it does not exist. <c>Port</c> is consumed by the server;
/// the display fields are opaque to the server and just round-trip to the Settings page so they
/// persist on the device rather than per-browser.
/// </summary>
public sealed class ServerConfig
{
    // Server
    public int? Port { get; set; }

    // Shared secret that agents must present on /api/ingest (and which authorises the dashboard's
    // admin endpoints from a non-browser client). Generated on first run; copy it into each agent's
    // michka_c.conf. Empty disables token auth (not recommended).
    public string Token { get; set; } = "";

    // Whether /api/ingest enforces the token. "on" (default) = agents must present it; "off" = any LAN
    // device may push without it (convenient, but anyone can then spoof metrics). The token value is
    // kept either way so flipping this back on restores the same secret.
    public string TokenRequired { get; set; } = "on";  // "on" | "off"

    // Optional PIN gate on the dashboard UI. "off" (default) = open; "on" = a browser must enter the
    // 6-digit PIN (numeric keypad login) before it can load the dashboard or read any UI data. PinHash
    // is the PBKDF2 salted hash of the PIN (never the PIN itself); empty when no PIN is set. Agents are
    // unaffected (they authenticate to /api/ingest with the token, not the PIN).
    public string PinEnabled { get; set; } = "off";   // "on" | "off"
    public string PinHash { get; set; } = "";

    // Extra Host header values accepted by the DNS-rebinding guard, beyond the built-in defaults
    // (loopback, any raw IP, this machine's name). Add a hostname/FQDN here to reach the hub by name.
    // A single "*" entry disables the Host check entirely.
    public List<string> HostAllow { get; set; } = new();

    // Kiosk autostart — whether michka-kiosk.service launches the dashboard automatically at boot
    // ("on") or must be started manually ("off"). Toggling it enables/disables the systemd unit.
    public string KioskAuto { get; set; } = "on";  // "on" | "off"

    // Active UI template id (a folder name under the on-disk templates/ directory). Empty = the
    // built-in default dashboard. The page loads the template's style.css + script.js over the top.
    public string Template { get; set; } = "";

    // UI language code (matches a file under wwwroot/lang/, e.g. "en", "fr"). The browser fetches
    // /lang/<code>.json and applies it live; the server just persists the choice.
    public string Lang { get; set; } = "en";

    // Display (opaque tokens that match the UI controls)
    public string TempUnit { get; set; } = "C";   // "C" | "F"
    public string Cores { get; set; } = "on";      // "on" | "off"
    public string Cycle { get; set; } = "off";     // "on" | "off"
    public string CycleSec { get; set; } = "10";   // "5" | "10" | "30"

    // Time/date format: "us" (12-hour clock, MM/DD/YYYY) or "intl" (24-hour clock, DD/MM/YYYY).
    public string DateFmt { get; set; } = "us";     // "us" | "intl"

    // Target touchscreen size ("WIDTHxHEIGHT"). The UI is fluid (always fills the window); this just
    // selects a scaling profile so each supported panel renders well — the short 1424x280 especially.
    // One of: "1280x400" | "1424x280" | "1920x440" | "1920x515".
    public string ScreenSize { get; set; } = "1280x400";

    // Screensaver — shown after a period of touch/pointer inactivity.
    public string SsMode { get; set; } = "off";    // "off" | "eye" | "michka" | "black" | "sleep"
    public string SsTimeout { get; set; } = "20";  // minutes: "5" | "20" | "40" | "60"

    // Monitored services (unit names) whose live status is shown in service boxes. <see cref="Services"/>
    // is the legacy global list (the hub's own host); <see cref="ServicesByHost"/> holds the per-host
    // selections keyed by host name — each host page picks its own services now that the picker lists
    // the selected host's catalog. The legacy global is migrated into the hub host's entry on startup.
    public List<string> Services { get; set; } = new();
    public Dictionary<string, List<string>> ServicesByHost { get; set; } = new();

    /// <summary>The monitored unit names for a given host (empty when the host has no selection).</summary>
    public List<string> ServicesForHost(string host)
        => ServicesByHost.TryGetValue(host, out var l) && l is not null ? l : new();

    // Dashboard layout — freeform boxes. <see cref="Layout"/> is the default/seed used for any host
    // without its own arrangement; <see cref="Layouts"/> holds per-host customisations keyed by host
    // name (each host page has its own boxes, so adding a box only affects the host you're viewing).
    public List<LayoutBox> Layout { get; set; } = new();
    public Dictionary<string, List<LayoutBox>> Layouts { get; set; } = new();

    // Swipe-down Widgets-page placements, per host: the ordered list of placed widget type ids (one of
    // each type per host). Stored server-side so the same host page shows the same widgets in every
    // browser/session, like <see cref="Layouts"/>.
    public Dictionary<string, List<string>> WidgetsByHost { get; set; } = new();

    // Saved per-widget-type config blobs — a widget's own settings (weather towns, Pi-hole URL/password,
    // uptime targets, NexusM server, …), keyed by widget type. Raw JSON so each widget owns its shape;
    // global per type (a widget keeps any per-host detail inside its own blob) and shared whether the
    // widget sits on the Widgets page or a board box. NOTE: a blob can hold credentials (e.g. a Pi-hole
    // password); it round-trips through /api/server-config, so enable the UI PIN gate if the LAN isn't
    // trusted.
    public Dictionary<string, JsonElement> WidgetConfig { get; set; } = new();

    [JsonIgnore] public string Path { get; private set; } = "michka.conf";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Load the config; create it with defaults on first run if the file is missing.</summary>
    public static ServerConfig Load(string path)
    {
        ServerConfig cfg;
        bool existed = File.Exists(path);
        try
        {
            cfg = existed
                ? JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), Json) ?? new ServerConfig()
                : new ServerConfig();
        }
        catch { cfg = new ServerConfig(); }
        cfg.Path = path;
        bool changed = cfg.EnsureToken();   // mint a secret for fresh or pre-token configs
        if (!existed || changed) { try { cfg.Save(); } catch { /* best effort on first run */ } }
        return cfg;
    }

    /// <summary>Generate a shared secret if none is set yet (first run, or a config from before tokens
    /// existed). Returns true when a new token was minted so the caller can persist it.</summary>
    public bool EnsureToken()
    {
        if (!string.IsNullOrWhiteSpace(Token)) return false;
        Token = Monitor.Api.Security.NewShortToken();
        return true;
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
    }
}

/// <summary>One draggable dashboard box: a metric tile or a monitored-service tile, positioned freely.</summary>
public sealed class LayoutBox
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";        // cpu | mem | net | storage | temp | service | widget
    public string? Service { get; set; }            // unit name when Type == "service"
    public string? Widget { get; set; }             // widget type id when Type == "widget"
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
}

/// <summary>Effective runtime info exposed to API endpoints / the Settings page.</summary>
public sealed class HubRuntime
{
    public required int Port { get; init; }
    public required string HostName { get; init; }
    public required string DbPath { get; init; }
    public required ServerConfig Config { get; init; }

    /// <summary>On-disk directory where users drop custom UI template folders (next to the db/config).</summary>
    public required string TemplatesDir { get; init; }

    /// <summary>On-disk directory where users drop custom widget folders (next to the db/config).</summary>
    public required string WidgetsDir { get; init; }

    public string Version => AppInfo.Version;
}

public static class AppInfo
{
    public const string Version = "1.0";
}
