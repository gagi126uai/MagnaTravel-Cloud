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
            catch
            {
                return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudieron leer los logs de API.");
            }
        }

        [HttpGet("bot-tail")]
        public async Task<IActionResult> GetBotTail()
        {
            try
            {
                var botUrl = _configuration["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
                var secret = _configuration["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";
                var client = _httpClientFactory.CreateClient();
                client.DefaultRequestHeaders.Add("X-Webhook-Secret", secret);
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
            const long maxScanBytes = 512 * 1024; // 512 KB max scan
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, fs.Length - maxScanBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            
            // Skip potentially partial first line if we seeked into the middle
            if (start > 0) reader.ReadLine();
            
            var lines = new List<string>();
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            return lines.TakeLast(count).ToList();
        }
    }
}
