#if WINDOWS_GUI
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Monitor.Cli;
using Monitor.Storage;

namespace Monitor.Modes;

/// <summary>
/// Runs the agent under the Windows Service Control Manager (started via <c>michka_c --service</c>,
/// which is how the installed service launches it). Settings come from <c>michka_c.conf</c>; status
/// goes to the Windows Event Log. The GUI installs/removes the service with <c>sc.exe</c>.
/// </summary>
public static class WindowsServiceRunner
{
    public const string ServiceName = "michka_c";
    public const string DisplayName = "Michka Client";

    public static async Task<int> RunAsync(Options opt)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.AddSingleton(opt);
        builder.Services.AddHostedService<AgentBackgroundService>();
        await builder.Build().RunAsync();
        return 0;
    }
}

/// <summary>Hosts the shared agent push-loop as a background service, logging to the Event Log.</summary>
internal sealed class AgentBackgroundService(Options opt, ILogger<AgentBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = AgentConfig.Load();
        var r = AgentRunner.Resolve(opt, cfg);
        if (string.IsNullOrWhiteSpace(r.HubUrl))
        {
            logger.LogWarning("No hub configured in {Path}; service is idle. Set the hub in the GUI.", cfg.Path);
            return;
        }
        try
        {
            await AgentRunner.RunLoopAsync(r.HubUrl!, r.Name, r.IntervalMs,
                m => logger.LogInformation("{Message}", m), stoppingToken, r.Token);
        }
        catch (OperationCanceledException) { /* service stopping */ }
    }
}
#endif
