using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TravelApi.Controllers
{
    [Authorize(Roles = "Admin")]
    [ApiController]
    [Route("api/[controller]")]
    public class LogsController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;

        public LogsController(IConfiguration configuration, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _httpClientFactory = httpClientFactory;
        }

        [HttpGet("api-tail")]
        public IActionResult GetApiTail()
        {
            try
            {
                // Find latest log file
                var logDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
                if (!Directory.Exists(logDir)) return Ok(new List<string>());

                var latestFile = Directory.GetFiles(logDir, "log-*.txt")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();

                if (latestFile == null) return Ok(new List<string>());

                // Read last 100 lines
                var lines = ReadLastLines(latestFile, 100);
                return Ok(lines.Select(l => $"[API] {l}"));
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("bot-tail")]
        public async Task<IActionResult> GetBotTail()
        {
            try
            {
                var botUrl = _configuration["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
                var client = _httpClientFactory.CreateClient();
                var response = await client.GetAsync($"{botUrl}/logs");
                
                if (response.IsSuccessStatusCode)
                {
                    var logs = await response.Content.ReadFromJsonAsync<List<string>>();
                    return Ok(logs?.Select(l => $"[BOT] {l}") ?? new List<string>());
                }
                return Ok(new List<string>());
            }
            catch
            {
                return Ok(new List<string>());
            }
        }

        private List<string> ReadLastLines(string filePath, int count)
        {
            var lines = new List<string>();
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                // Simple approach: read all and take last N. 
                // For log files of a single day, this is usually acceptable.
                var allLines = new List<string>();
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line)) allLines.Add(line);
                }
                return allLines.TakeLast(count).ToList();
            }
        }
    }
}
