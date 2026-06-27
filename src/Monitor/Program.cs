using Monitor.Cli;
using Monitor.Modes;

Options opt;
try
{
    opt = Options.Parse(args);
    opt.ApplyExecutableDefault(System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath));
    if (!opt.ShowHelp) opt.Validate();
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Options.HelpText);
    return 2;
}

if (opt.ShowHelp)
{
    Console.WriteLine(Options.HelpText);
    return 0;
}

#if WINDOWS_GUI
// Windows client: --service runs under the SCM; otherwise launching with no args (a double-click) or
// --gui opens the settings GUI. Falls through to the console agent/hub for everything else.
if (opt.Agent && opt.Service)
    return await WindowsServiceRunner.RunAsync(opt);
if (opt.Agent && (opt.Gui || args.Length == 0))
    return Monitor.Gui.AgentGui.Run(opt);
#endif

return opt.Agent
    ? await AgentRunner.RunAsync(opt)
    : await HubHost.RunAsync(opt);
