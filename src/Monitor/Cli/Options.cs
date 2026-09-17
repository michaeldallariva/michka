namespace Monitor.Cli;

/// <summary>Parsed command-line options. Minimal hand-rolled parser (no extra dependency).</summary>
public sealed class Options
{
    public bool Agent { get; private set; }
    public string? Hub { get; private set; }
    public string? Name { get; private set; }
    public string? Token { get; private set; }
    public int Port { get; private set; } = 5000;
    public bool PortSpecified { get; private set; }
    public int IntervalMs { get; private set; } = 1000;
    public bool IntervalSpecified { get; private set; }
    public bool NoLocal { get; private set; }
    public string DbPath { get; private set; } = "monitor.db";
    public bool ShowHelp { get; private set; }

    /// <summary>Open the Windows settings GUI (default when the client is launched with no arguments).</summary>
    public bool Gui { get; private set; }

    /// <summary>Run as a Windows service (started by the Service Control Manager).</summary>
    public bool Service { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--agent": o.Agent = true; break;
                case "--gui": o.Gui = true; break;
                case "--service": o.Service = true; o.Agent = true; break;
                case "--no-local": o.NoLocal = true; break;
                case "-h" or "--help": o.ShowHelp = true; break;
                case "--hub": o.Hub = Next(args, ref i, a); break;
                case "--name": o.Name = Next(args, ref i, a); break;
                case "--token": o.Token = Next(args, ref i, a); break;
                case "--db": o.DbPath = Next(args, ref i, a); break;
                case "--port": o.Port = ParseInt(Next(args, ref i, a), a); o.PortSpecified = true; break;
                case "--interval": o.IntervalMs = ParseInt(Next(args, ref i, a), a); o.IntervalSpecified = true; break;
                default:
                    throw new ArgumentException($"Unknown argument: {a}");
            }
        }

        if (o.IntervalMs < 200) o.IntervalMs = 200; // sanity floor
        return o;
    }

    /// <summary>
    /// Pick the default mode from the executable name when not given explicitly: a binary named
    /// <c>michka_c*</c> (or ending <c>_c</c>) defaults to agent; <c>michka_s*</c> (or <c>_s</c>) to hub.
    /// An explicit <c>--agent</c> always wins.
    /// </summary>
    public void ApplyExecutableDefault(string? exeName)
    {
        if (Agent) return;
        var n = (exeName ?? "").ToLowerInvariant();
        bool isClient = n.Contains("michka_c") || n.EndsWith("_c");
        bool isServer = n.Contains("michka_s") || n.EndsWith("_s");
        if (isClient && !isServer) Agent = true;
    }

    /// <summary>
    /// Throw if the resolved options are inconsistent. Agent mode no longer requires <c>--hub</c> on
    /// the command line: the hub can come from <c>michka_c.conf</c> (set via the GUI). A missing hub
    /// is reported by <see cref="Monitor.Modes.AgentRunner"/> after the conf has been consulted.
    /// </summary>
    public void Validate()
    {
        if (PortSpecified && Port is < 1 or > 65535)
            throw new ArgumentException($"--port must be 1-65535, got {Port}");
    }

    private static string Next(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"{flag} requires a value");
        return args[++i];
    }

    private static int ParseInt(string s, string flag)
        => int.TryParse(s, out var v) ? v : throw new ArgumentException($"{flag} expects a number, got '{s}'");

    public const string HelpText = """
        michka — cross-platform hardware dashboard

        Binaries:
          michka_s   server / hub (web UI + local metrics)   [default mode: hub]
          michka_c   client / agent (push metrics to a hub)  [default mode: agent]
        (A single build behaves as either; the default mode is chosen from the binary name.)

        Usage:
          michka_s [options]                       Run a hub
          michka_c [--hub <url>] [options]         Run an agent (hub also read from michka_c.conf)
          michka_c                                 (Windows) open the settings GUI
          michka_c --service                       (Windows) run as a Windows service
          michka_s --agent --hub <url> [options]   Force agent mode regardless of name

        The client reads michka_c.conf (next to the .exe; created on first run) for the hub host,
        port, scheme, name and interval. Command-line flags override the conf file.

        Options:
          --agent              Force agent mode (push only, no web UI)
          --gui                (Windows) open the settings GUI
          --service            (Windows) run as a Windows service
          --hub <url>          Hub base URL to push to (overrides michka_c.conf)
          --name <name>        Host label reported to the hub (default: machine name)
          --token <secret>     Shared secret for the hub (overrides michka_c.conf; from the hub's michka.conf)
          --port <n>           Hub HTTP port (overrides the saved server config; default 5000)
          --interval <ms>      Sample/push interval in ms (default: 1000, min 200)
          --no-local           Hub: do not collect the local machine
          --db <path>          SQLite history file (hub, default: monitor.db)
          -h, --help           Show this help
        """;
}
