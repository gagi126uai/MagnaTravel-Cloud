using Microsoft.AspNetCore.SignalR;
using TravelApi.Hubs;

namespace TravelApi.Services
{
    public class BotLogMonitorService : BackgroundService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IHubContext<LogsHub> _hubContext;
        private readonly IConfiguration _configuration;
        private readonly ILogger<BotLogMonitorService> _logger;
        private HashSet<string> _seenLogs = new HashSet<string>();

        public BotLogMonitorService(
            IHttpClientFactory httpClientFactory, 
            IHubContext<LogsHub> hubContext,
            IConfiguration configuration,
            ILogger<BotLogMonitorService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _hubContext = hubContext;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var botUrl = _configuration["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    var response = await client.GetAsync($"{botUrl}/logs", stoppingToken);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var logs = await response.Content.ReadFromJsonAsync<List<string>>(stoppingToken);
                        if (logs != null)
                        {
                            foreach (var log in logs)
                            {
                                if (!_seenLogs.Contains(log))
                                {
                                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"[BOT] {log}", stoppingToken);
                                    _seenLogs.Add(log);
                                    
                                    // Limit cache size
                                    if (_seenLogs.Count > 1000) _seenLogs.Clear();
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Ignore bot offline errors
                }

                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
