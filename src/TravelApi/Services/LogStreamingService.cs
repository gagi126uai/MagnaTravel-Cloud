using Microsoft.AspNetCore.SignalR;
using TravelApi.Hubs;
using TravelApi.Infrastructure.Logging;

namespace TravelApi.Services
{
    public class LogStreamingService : BackgroundService
    {
        private readonly IHubContext<LogsHub> _hubContext;
        private readonly ILogger<LogStreamingService> _logger;

        public LogStreamingService(IHubContext<LogsHub> hubContext, ILogger<LogStreamingService> logger)
        {
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LogStreamingService is starting...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await LogChannel.Reader.WaitToReadAsync(stoppingToken))
                    {
                        while (LogChannel.Reader.TryRead(out var logMessage))
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", logMessage, stoppingToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error streaming log to SignalR");
                    await Task.Delay(5000, stoppingToken);
                }
            }

            _logger.LogInformation("LogStreamingService is stopping...");
        }
    }
}
