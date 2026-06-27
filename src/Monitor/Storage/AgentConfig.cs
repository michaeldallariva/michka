using System.Text.Json;
using System.Text.Json.Serialization;

namespace Monitor.Storage;

/// <summary>
/// Persisted settings for the <c>michka_c</c> client/agent, stored as <c>michka_c.conf</c> (JSON)
/// in the same folder as the executable. Created on first run with defaults if missing. The GUI and
/// the console/service agent both read it on startup; the GUI rewrites it when the user edits a value.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>URL scheme for the hub: <c>http</c> or <c>https</c> (lets a FQDN target use TLS).</summary>
    public string Scheme { get; set; } = "http";

    /// <summary>Hub host — an IP address, hostname, or fully-qualified domain name (e.g. <c>hub.lan</c>).</summary>
    public string Host { get; set; } = "";

    /// <summary>Hub HTTP(S) port.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>Host label reported to the hub. Empty = use this machine's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Sample/push interval in milliseconds (min 200).</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>The hub base URL built from <see cref="Scheme"/>/<see cref="Host"/>/<see cref="Port"/>.</summary>
    [JsonIgnore]
    public string HubUrl => $"{Scheme}://{Host}:{Port}";

    /// <summary>True once a hub host has been configured (so the agent has somewhere to push).</summary>
    [JsonIgnore]
    public bool HasHub => !string.IsNullOrWhiteSpace(Host);

    [JsonIgnore] public string Path { get; private set; } = DefaultPath();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The conf path next to the running executable (falls back to the working dir).</summary>
    public static string DefaultPath()
    {
        var exe = Environment.ProcessPath;
        var dir = string.IsNullOrEmpty(exe) ? AppContext.BaseDirectory : System.IO.Path.GetDirectoryName(exe);
        return System.IO.Path.Combine(dir ?? ".", "michka_c.conf");
    }

    /// <summary>Load the config; create it with defaults on first run if the file is missing.</summary>
    public static AgentConfig Load(string? path = null)
    {
        path ??= DefaultPath();
        AgentConfig cfg;
        bool existed = File.Exists(path);
        try
        {
            cfg = existed
                ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), Json) ?? new AgentConfig()
                : new AgentConfig();
        }
        catch { cfg = new AgentConfig(); }
        cfg.Path = path;
        cfg.Normalize();
        if (!existed) { try { cfg.Save(); } catch { /* best effort on first run */ } }
        return cfg;
    }

    /// <summary>Clamp/normalise fields to sane values.</summary>
    public void Normalize()
    {
        Scheme = string.Equals(Scheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        Host = (Host ?? "").Trim();
        Name = (Name ?? "").Trim();
        if (Port is < 1 or > 65535) Port = 5000;
        if (IntervalMs < 200) IntervalMs = 200;
    }

    public void Save()
    {
        Normalize();
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
    }
}
