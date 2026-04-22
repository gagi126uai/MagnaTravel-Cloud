using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Cryptography;
using System.Text;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Contracts.Leads;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly ILeadService _leadService;
    private readonly IWhatsAppWebhookService _whatsAppWebhookService;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhooksController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public WebhooksController(
        ILeadService leadService,
        IWhatsAppWebhookService whatsAppWebhookService,
        IConfiguration config,
        ILogger<WebhooksController> logger,
        IHttpClientFactory httpClientFactory,
        IEntityReferenceResolver entityReferenceResolver)
    {
        _leadService = leadService;
        _whatsAppWebhookService = whatsAppWebhookService;
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _entityReferenceResolver = entityReferenceResolver;
    }

    private bool ValidateSecret()
    {
        var expected = _config["WhatsApp:WebhookSecret"];
        if (string.IsNullOrWhiteSpace(expected) ||
            expected.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogCritical("WhatsApp webhook secret is not configured or is using a default value. All webhooks will be rejected.");
            return false;
        }

        var provided = Request.Headers["X-Webhook-Secret"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(provided));
    }

    [HttpPost("whatsapp")]
    [EnableRateLimiting("webhooks")]
    public async Task<ActionResult> WhatsAppLead(
        [FromBody] WhatsAppWebhookDto dto,
        CancellationToken cancellationToken)
    {
        if (!ValidateSecret())
        {
            _logger.LogWarning("Webhook rechazado: secret invalido desde {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { message = "Secret invalido." });
        }

        try
        {
            var result = await _whatsAppWebhookService.ProcessLeadCaptureAsync(dto, cancellationToken);
            if (result.Created)
            {
                _logger.LogInformation("Nuevo lead WhatsApp: {Name} ({Phone})", result.Name, result.Phone);
                return StatusCode(201, new
                {
                    message = "Lead creado exitosamente.",
                    leadPublicId = result.LeadPublicId,
                    name = result.Name,
                    phone = result.Phone
                });
            }

            _logger.LogInformation("Lead WhatsApp enriquecido: {Phone}", dto.Phone);
            return Ok(new
            {
                message = "Lead actualizado exitosamente.",
                leadPublicId = result.LeadPublicId,
                updated = true
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "Nombre y telefono son obligatorios." });
        }
    }

    [HttpPost("whatsapp/message")]
    [EnableRateLimiting("webhooks")]
    public async Task<ActionResult> WhatsAppMessage(
        [FromBody] WhatsAppMessageDto dto,
        CancellationToken cancellationToken)
    {
        if (!ValidateSecret())
        {
            return Unauthorized(new { message = "Secret invalido." });
        }

        try
        {
            var result = await _whatsAppWebhookService.ProcessIncomingMessageAsync(dto, cancellationToken);
            if (result.HandledBy == "operational")
            {
                return Ok(new { handledBy = result.HandledBy });
            }

            if (result.HandledBy == "none")
            {
                return Ok(new
                {
                    handledBy = result.HandledBy,
                    autoCreated = result.AutoCreated,
                    allowBotCapture = result.AllowBotCapture
                });
            }

            return Ok(new
            {
                handledBy = result.HandledBy,
                leadPublicId = result.LeadPublicId,
                autoCreated = result.AutoCreated,
                allowBotCapture = result.AllowBotCapture
            });
        }
        catch (ArgumentException)
        {
            return BadRequest(new { message = "Phone y message son obligatorios." });
        }
    }

    [HttpPost("/api/leads/{publicIdOrLegacyId}/whatsapp-message")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<ActionResult> SendWhatsAppMessage(
        string publicIdOrLegacyId,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var lead = await _leadService.GetByIdAsync(publicIdOrLegacyId, cancellationToken);
        if (lead == null)
        {
            return NotFound(new { message = "Lead no encontrado." });
        }

        if (string.IsNullOrWhiteSpace(lead.Phone))
        {
            return BadRequest(new { message = "El lead no tiene telefono." });
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { message = "El mensaje es obligatorio." });
        }

        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);

            var botRequest = new HttpRequestMessage(HttpMethod.Post, $"{botUrl}/send")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new
                {
                    phone = lead.Phone,
                    message = request.Message
                })
            };
            botRequest.Headers.Add("X-Webhook-Secret", secret);

            var response = await httpClient.SendAsync(botRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Error enviando WhatsApp a {Phone}: {Error}", lead.Phone, error);
                return StatusCode(502, new { message = "Error al enviar mensaje por WhatsApp." });
            }

            var userName = User.Identity?.Name ?? "Agente";
            await _leadService.AddActivityAsync(
                publicIdOrLegacyId,
                new LeadActivityUpsertRequest("WhatsApp", request.Message.Trim(), userName),
                userName,
                cancellationToken);

            _logger.LogInformation("WhatsApp enviado a {Phone} por {User}", lead.Phone, userName);
            return Ok(new { message = "Mensaje enviado exitosamente." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error conectando con bot WhatsApp");
            return StatusCode(502, new { message = "No se pudo conectar con el bot de WhatsApp." });
        }
    }

    [HttpGet("status")]
    [Authorize(Roles = "Admin")]
    [EnableRateLimiting("afip")]
    public async Task<IActionResult> GetBotStatus()
    {
        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Webhook-Secret", secret);
            var response = await client.GetAsync($"{botUrl}/status");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return Content(content, "application/json");
            }

            return StatusCode((int)response.StatusCode, "Error al obtener estado del bot");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving bot status");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo obtener el estado del bot.");
        }
    }

    [HttpPost("reload")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReloadBotConfig()
    {
        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Webhook-Secret", secret);
            var response = await client.PostAsync($"{botUrl}/reload", null);

            if (response.IsSuccessStatusCode)
            {
                return Ok(new { success = true });
            }

            return StatusCode((int)response.StatusCode, "Error al notificar al bot");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading bot config");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo recargar la configuracion del bot.");
        }
    }

    [HttpPost("logout")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> LogoutBot()
    {
        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Webhook-Secret", secret);
            var response = await client.PostAsync($"{botUrl}/logout", null);

            if (response.IsSuccessStatusCode)
            {
                return Ok(new { success = true });
            }

            return StatusCode((int)response.StatusCode, "Error al cerrar sesion del bot");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging out bot");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudo cerrar la sesion del bot.");
        }
    }

    [HttpGet("logs")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> GetBotLogs()
    {
        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Webhook-Secret", secret);
            var response = await client.GetAsync($"{botUrl}/logs");

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return Content(content, "application/json");
            }

            return StatusCode((int)response.StatusCode, "Error al obtener logs del bot");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving bot logs");
            return Problem(statusCode: StatusCodes.Status502BadGateway, title: "No se pudieron obtener los logs del bot.");
        }
    }
}

public class SendMessageRequest
{
    public string Message { get; set; } = string.Empty;
}
