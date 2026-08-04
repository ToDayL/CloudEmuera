using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SupervisorService>();

await builder.Build().RunAsync();

internal sealed partial class SupervisorService(ILogger<SupervisorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "CloudEmuera Worker Supervisor started")]
    private static partial void LogStarted(ILogger logger);
}
