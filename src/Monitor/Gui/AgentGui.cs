#if WINDOWS_GUI
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;
using Monitor.Cli;
using Monitor.Modes;
using Monitor.Storage;

namespace Monitor.Gui;

/// <summary>
/// The Windows client's settings GUI. Edits <c>michka_c.conf</c> (hub host/port/scheme, name,
/// interval), can run the agent in-process (Start/Stop) to verify pushing, and installs/removes a
/// Windows service via <c>sc.exe</c>. Launched when <c>michka_c.exe</c> is started with no arguments
/// (or <c>--gui</c>). Runs on a dedicated STA thread so no <c>[STAThread]</c> entry point is needed.
/// </summary>
public static class AgentGui
{
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    public static int Run(Options opt)
    {
        // Detach the console: a double-clicked console-subsystem exe gets its own console window
        // (closed by FreeConsole); when launched from a shell this just detaches us, leaving the
        // shell intact. Either way the GUI shows without a stray black window behind it.
        try { FreeConsole(); } catch { /* no console attached */ }

        int code = 0;
        var t = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new AgentForm(opt)); }
            catch (Exception ex) { MessageBox.Show(ex.ToString(), "Michka Client", MessageBoxButtons.OK, MessageBoxIcon.Error); code = 1; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return code;
    }
}

internal sealed class AgentForm : Form
{
    private readonly Options _opt;
    private readonly AgentConfig _cfg;

    private ComboBox _scheme = null!;
    private TextBox _host = null!;
    private NumericUpDown _port = null!;
    private Label _urlPreview = null!;
    private TextBox _name = null!;
    private NumericUpDown _interval = null!;
    private Button _btnSave = null!, _btnTest = null!, _btnRun = null!;
    private Label _svcStatus = null!;
    private Button _btnInstall = null!, _btnUninstall = null!, _btnSvcStart = null!, _btnSvcStop = null!;
    private TextBox _log = null!;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Icon? _appIcon;

    public AgentForm(Options opt)
    {
        _opt = opt;
        _cfg = AgentConfig.Load();
        _appIcon = LoadAppIcon();
        if (_appIcon != null) Icon = _appIcon;
        BuildUi();
        LoadFromConfig();
        RefreshServiceStatus();
        FormClosing += (_, _) => StopAgentInProcess();
    }

    private void BuildUi()
    {
        Text = "Michka Client";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 574);
        BackColor = Color.FromArgb(245, 246, 248);

        // Logo (the network-nodes app icon) top-left, with the title beside it and an About button
        // top-right. Rendering from the embedded .ico lets us pick a crisp 32px size for the header.
        if (_appIcon != null)
        {
            var logo = new PictureBox
            {
                Image = new Icon(_appIcon, new Size(32, 32)).ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(14, 12),
                Size = new Size(32, 32),
            };
            Controls.Add(logo);
        }

        var title = new Label { Text = "Michka Client : agent settings", AutoSize = true, Location = new Point(_appIcon != null ? 54 : 14, 16), Font = new Font("Segoe UI Semibold", 12f) };
        Controls.Add(title);

        var btnAbout = new Button { Text = "About", Location = new Point(366, 14), Size = new Size(80, 28) };
        btnAbout.Click += (_, _) => ShowAbout();
        Controls.Add(btnAbout);

        // ---- Hub group ----
        var gHub = new GroupBox { Text = "Hub", Location = new Point(14, 58), Size = new Size(432, 150) };
        Controls.Add(gHub);

        gHub.Controls.Add(new Label { Text = "Scheme", AutoSize = true, Location = new Point(14, 28) });
        _scheme = new ComboBox { Location = new Point(120, 24), Size = new Size(90, 24), DropDownStyle = ComboBoxStyle.DropDownList };
        _scheme.Items.AddRange(new object[] { "http", "https" });
        _scheme.SelectedIndexChanged += (_, _) => UpdateUrlPreview();
        gHub.Controls.Add(_scheme);

        gHub.Controls.Add(new Label { Text = "Host (IP or FQDN)", AutoSize = true, Location = new Point(14, 60) });
        _host = new TextBox { Location = new Point(120, 56), Size = new Size(296, 24), PlaceholderText = "192.168.0.115  or  hub.example.com" };
        _host.TextChanged += (_, _) => UpdateUrlPreview();
        gHub.Controls.Add(_host);

        gHub.Controls.Add(new Label { Text = "Port", AutoSize = true, Location = new Point(14, 92) });
        _port = new NumericUpDown { Location = new Point(120, 88), Size = new Size(90, 24), Minimum = 1, Maximum = 65535, Value = 5000 };
        _port.ValueChanged += (_, _) => UpdateUrlPreview();
        gHub.Controls.Add(_port);

        _urlPreview = new Label { AutoSize = true, Location = new Point(14, 122), ForeColor = Color.FromArgb(60, 90, 160), Font = new Font("Consolas", 9f) };
        gHub.Controls.Add(_urlPreview);

        // ---- Agent group ----
        var gAgent = new GroupBox { Text = "Agent", Location = new Point(14, 218), Size = new Size(432, 96) };
        Controls.Add(gAgent);

        gAgent.Controls.Add(new Label { Text = "Name", AutoSize = true, Location = new Point(14, 28) });
        _name = new TextBox { Location = new Point(120, 24), Size = new Size(296, 24), PlaceholderText = Environment.MachineName + "  (default)" };
        gAgent.Controls.Add(_name);

        gAgent.Controls.Add(new Label { Text = "Interval (ms)", AutoSize = true, Location = new Point(14, 60) });
        _interval = new NumericUpDown { Location = new Point(120, 56), Size = new Size(110, 24), Minimum = 200, Maximum = 600000, Increment = 100, Value = 1000 };
        gAgent.Controls.Add(_interval);

        // ---- Action buttons ----
        _btnSave = new Button { Text = "Save", Location = new Point(14, 326), Size = new Size(96, 30) };
        _btnSave.Click += (_, _) => { SaveToConfig(); Log("Saved " + _cfg.Path); };
        Controls.Add(_btnSave);

        _btnTest = new Button { Text = "Test connection", Location = new Point(118, 326), Size = new Size(130, 30) };
        _btnTest.Click += async (_, _) => await TestConnectionAsync();
        Controls.Add(_btnTest);

        _btnRun = new Button { Text = "Start agent", Location = new Point(256, 326), Size = new Size(190, 30) };
        _btnRun.Click += (_, _) => ToggleAgent();
        Controls.Add(_btnRun);

        // ---- Service group ----
        var gSvc = new GroupBox { Text = "Windows service", Location = new Point(14, 366), Size = new Size(432, 96) };
        Controls.Add(gSvc);

        _svcStatus = new Label { Text = "Status: …", AutoSize = true, Location = new Point(14, 26) };
        gSvc.Controls.Add(_svcStatus);

        _btnInstall = new Button { Text = "Install", Location = new Point(14, 52), Size = new Size(96, 30) };
        _btnInstall.Click += (_, _) => InstallService();
        gSvc.Controls.Add(_btnInstall);

        _btnUninstall = new Button { Text = "Uninstall", Location = new Point(116, 52), Size = new Size(96, 30) };
        _btnUninstall.Click += (_, _) => UninstallService();
        gSvc.Controls.Add(_btnUninstall);

        _btnSvcStart = new Button { Text = "Start", Location = new Point(222, 52), Size = new Size(96, 30) };
        _btnSvcStart.Click += (_, _) => ControlService("start");
        gSvc.Controls.Add(_btnSvcStart);

        _btnSvcStop = new Button { Text = "Stop", Location = new Point(324, 52), Size = new Size(96, 30) };
        _btnSvcStop.Click += (_, _) => ControlService("stop");
        gSvc.Controls.Add(_btnSvcStop);

        // ---- Log ----
        _log = new TextBox { Location = new Point(14, 470), Size = new Size(432, 92), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        Controls.Add(_log);
    }

    private void LoadFromConfig()
    {
        _scheme.SelectedItem = _cfg.Scheme == "https" ? "https" : "http";
        if (_scheme.SelectedIndex < 0) _scheme.SelectedIndex = 0;
        _host.Text = _cfg.Host;
        _port.Value = Math.Clamp(_cfg.Port, (int)_port.Minimum, (int)_port.Maximum);
        _name.Text = _cfg.Name;
        _interval.Value = Math.Clamp(_cfg.IntervalMs, (int)_interval.Minimum, (int)_interval.Maximum);
        UpdateUrlPreview();
    }

    private void SaveToConfig()
    {
        _cfg.Scheme = (string)(_scheme.SelectedItem ?? "http");
        _cfg.Host = _host.Text.Trim();
        _cfg.Port = (int)_port.Value;
        _cfg.Name = _name.Text.Trim();
        _cfg.IntervalMs = (int)_interval.Value;
        try { _cfg.Save(); }
        catch (Exception ex) { Log("Could not save config: " + ex.Message); }
        UpdateUrlPreview();
    }

    private void UpdateUrlPreview()
    {
        var scheme = (string)(_scheme.SelectedItem ?? "http");
        var host = _host.Text.Trim();
        _urlPreview.Text = string.IsNullOrEmpty(host) ? "Hub URL: (set a host)" : $"Hub URL: {scheme}://{host}:{(int)_port.Value}";
    }

    private async Task TestConnectionAsync()
    {
        SaveToConfig();
        if (!_cfg.HasHub) { Log("Set a hub host first."); return; }
        var url = _cfg.HubUrl.TrimEnd('/') + "/api/hosts";
        _btnTest.Enabled = false;
        Log($"Testing {url} …");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await http.GetAsync(url);
            Log(resp.IsSuccessStatusCode ? "Hub reachable ✓" : $"Hub responded {(int)resp.StatusCode}");
        }
        catch (Exception ex) { Log("Hub unreachable: " + ex.Message); }
        finally { _btnTest.Enabled = true; }
    }

    private void ToggleAgent()
    {
        if (_runCts == null)
        {
            SaveToConfig();
            var r = AgentRunner.Resolve(_opt, _cfg);
            if (string.IsNullOrWhiteSpace(r.HubUrl)) { Log("Set a hub host first."); return; }

            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            _runTask = Task.Run(async () =>
            {
                try { await AgentRunner.RunLoopAsync(r.HubUrl!, r.Name, r.IntervalMs, Log, token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log("Agent error: " + ex.Message); }
            });
            _btnRun.Text = "Stop agent";
            SetSettingsEnabled(false);
            Log("Agent started.");
        }
        else
        {
            StopAgentInProcess();
            Log("Agent stopped.");
        }
    }

    private void StopAgentInProcess()
    {
        if (_runCts == null) return;
        try { _runCts.Cancel(); } catch { }
        try { _runTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _runCts.Dispose();
        _runCts = null;
        _runTask = null;
        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { _btnRun.Text = "Start agent"; SetSettingsEnabled(true); });
    }

    private void SetSettingsEnabled(bool on)
    {
        _scheme.Enabled = _host.Enabled = _port.Enabled = _name.Enabled = _interval.Enabled = _btnSave.Enabled = on;
    }

    // ---------------- Windows service (sc.exe) ----------------

    private void InstallService()
    {
        var exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) { Log("Cannot resolve executable path."); return; }
        SaveToConfig();
        // sc.exe quirks: a space is required after each `key=`, and the whole binPath value (which
        // itself contains a quoted path) must be quoted as one argument.
        var binPath = $"\"\\\"{exe}\\\" --service\"";
        var args = $"create {WindowsServiceRunner.ServiceName} binPath= {binPath} start= auto DisplayName= \"{WindowsServiceRunner.DisplayName}\"";
        if (RunSc(args, elevate: true))
        {
            RunSc($"description {WindowsServiceRunner.ServiceName} \"Michka metrics agent — pushes this machine's metrics to the hub.\"", elevate: true);
            Log("Service installed. Use Start to run it (it reads michka_c.conf).");
        }
        RefreshServiceStatus();
    }

    private void UninstallService()
    {
        RunSc("stop " + WindowsServiceRunner.ServiceName, elevate: true);
        if (RunSc("delete " + WindowsServiceRunner.ServiceName, elevate: true)) Log("Service removed.");
        RefreshServiceStatus();
    }

    private void ControlService(string verb)
    {
        if (RunSc($"{verb} {WindowsServiceRunner.ServiceName}", elevate: true)) Log($"Service {verb} requested.");
        RefreshServiceStatus();
    }

    /// <summary>Query the service state without elevation and reflect it in the UI.</summary>
    private void RefreshServiceStatus()
    {
        var (ok, output) = RunCaptured("sc.exe", "query " + WindowsServiceRunner.ServiceName);
        string status;
        bool installed = ok && output.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase);
        if (!installed) status = "Not installed";
        else if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) status = "Installed · Running";
        else if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) status = "Installed · Stopped";
        else status = "Installed";

        _svcStatus.Text = "Status: " + status;
        _btnInstall.Enabled = !installed;
        _btnUninstall.Enabled = installed;
        _btnSvcStart.Enabled = installed && !status.Contains("Running");
        _btnSvcStop.Enabled = installed && status.Contains("Running");
    }

    /// <summary>Run an sc.exe command. Service changes need admin, so elevate via the UAC prompt.</summary>
    private bool RunSc(string args, bool elevate)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args) { UseShellExecute = elevate && !IsAdministrator() };
            if (psi.UseShellExecute) psi.Verb = "runas";   // trigger UAC for the privileged sc command
            else { psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.CreateNoWindow = true; }
            using var p = Process.Start(psi)!;
            if (!psi.UseShellExecute)
            {
                var so = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) Log($"sc {args.Split(' ')[0]} failed ({p.ExitCode}): {so.Trim()}");
                return p.ExitCode == 0;
            }
            p.WaitForExit();
            if (p.ExitCode != 0) Log($"sc {args.Split(' ')[0]} failed ({p.ExitCode}).");
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log("sc.exe error: " + ex.Message + " (administrator rights are required).");
            return false;
        }
    }

    private static (bool ok, string output) RunCaptured(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode == 0, output);
        }
        catch { return (false, ""); }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Load the app icon (the network-nodes glyph) from the embedded resource so it can be
    /// rendered at any size; returns null if the resource is missing.</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            using var s = typeof(AgentForm).Assembly.GetManifestResourceStream("Monitor.icon.ico");
            return s != null ? new Icon(s) : null;
        }
        catch { return null; }
    }

    // ---------------- About box ----------------

    private void ShowAbout()
    {
        using var dlg = new Form
        {
            Text = "About Michka Client",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(440, 430),
            BackColor = Color.FromArgb(245, 246, 248),
            Font = new Font("Segoe UI", 9f),
        };
        if (_appIcon != null) dlg.Icon = _appIcon;

        // Large logo, centred near the top.
        if (_appIcon != null)
        {
            var logo = new PictureBox
            {
                Image = new Icon(_appIcon, new Size(96, 96)).ToBitmap(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(96, 96),
                Location = new Point((dlg.ClientSize.Width - 96) / 2, 18),
            };
            dlg.Controls.Add(logo);
        }

        var heading = new Label
        {
            Text = "Michka Client",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 120),
            Size = new Size(dlg.ClientSize.Width, 26),
            Font = new Font("Segoe UI Semibold", 13f),
        };
        dlg.Controls.Add(heading);

        var info = new Label
        {
            AutoSize = false,
            Location = new Point(24, 156),
            Size = new Size(dlg.ClientSize.Width - 48, 230),
            Text =
                "Authors  :  Michael DALLA RIVA / Patrick DUQUESNOY\r\n" +
                "Version  :  michka_c (michka client) v1.3\r\n" +
                "Website  :  michka.org\r\n" +
                "\r\n" +
                "Licenses and libraries :\r\n" +
                "  .  .NET 10 Runtime  —  MIT License\r\n" +
                "  .  Microsoft.Data.Sqlite  —  MIT License\r\n" +
                "  .  Microsoft.Extensions.FileProviders.Embedded  —  MIT License\r\n" +
                "  .  Microsoft.Extensions.Hosting.WindowsServices  —  MIT License\r\n" +
                "  .  System.Management  —  MIT License",
        };
        dlg.Controls.Add(info);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(90, 30), Location = new Point((dlg.ClientSize.Width - 90) / 2, 390) };
        dlg.Controls.Add(ok);
        dlg.AcceptButton = ok;

        dlg.ShowDialog(this);
    }

    private void Log(string msg)
    {
        if (IsDisposed) return;
        void Append() => _log.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
        if (_log.InvokeRequired) { try { _log.BeginInvoke((Action)Append); } catch { } }
        else Append();
    }
}
#endif
