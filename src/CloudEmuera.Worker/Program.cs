using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SessionWorkerService>();

await builder.Build().RunAsync();

internal sealed partial class SessionWorkerService(ILogger<SessionWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "CloudEmuera Session Worker started")]
    private static partial void LogStarted(ILogger logger);
}
