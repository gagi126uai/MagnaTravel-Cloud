using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly ILeadService _leadService;
    private readonly IWhatsAppDeliveryService _whatsAppDeliveryService;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhooksController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public WebhooksController(
        ILeadService leadService,
        IWhatsAppDeliveryService whatsAppDeliveryService,
        AppDbContext db,
        IConfiguration config,
        ILogger<WebhooksController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _leadService = leadService;
        _whatsAppDeliveryService = whatsAppDeliveryService;
        _db = db;
        _config = config;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    private bool ValidateSecret()
    {
        var expected = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";
        var provided = Request.Headers["X-Webhook-Secret"].FirstOrDefault();
        return !string.IsNullOrEmpty(provided) && provided == expected;
    }

    private static string NormalizePhone(string phone) => phone.Replace("+", "").Trim();

    private static bool IsPlaceholderLeadName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        name.StartsWith("Nuevo contacto WhatsApp", StringComparison.OrdinalIgnoreCase);

    private static bool LeadNeedsQualification(Lead lead) =>
        IsPlaceholderLeadName(lead.FullName) ||
        string.IsNullOrWhiteSpace(lead.InterestedIn) ||
        string.IsNullOrWhiteSpace(lead.TravelDates) ||
        string.IsNullOrWhiteSpace(lead.Travelers);

    private static void MergeStructuredWhatsAppCapture(Lead lead, WhatsAppWebhookDto dto)
    {
        if (!string.IsNullOrWhiteSpace(dto.Name) && IsPlaceholderLeadName(lead.FullName))
            lead.FullName = dto.Name.Trim();

        if (string.IsNullOrWhiteSpace(lead.Phone) && !string.IsNullOrWhiteSpace(dto.Phone))
            lead.Phone = dto.Phone.Trim();

        if (string.IsNullOrWhiteSpace(lead.Source))
            lead.Source = "WhatsApp";

        if (!string.IsNullOrWhiteSpace(dto.Interest))
            lead.InterestedIn = dto.Interest.Trim();

        if (!string.IsNullOrWhiteSpace(dto.Dates))
            lead.TravelDates = dto.Dates.Trim();

        if (!string.IsNullOrWhiteSpace(dto.Travelers))
            lead.Travelers = dto.Travelers.Trim();

        if (string.IsNullOrWhiteSpace(lead.Notes))
            lead.Notes = "Lead capturado por WhatsApp Bot.";
    }

    [HttpPost("whatsapp")]
    public async Task<ActionResult> WhatsAppLead(
        [FromBody] WhatsAppWebhookDto dto,
        CancellationToken cancellationToken)
    {
        if (!ValidateSecret())
        {
            _logger.LogWarning("Webhook rechazado: secret invalido desde {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { message = "Secret invalido." });
        }

        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Phone))
            return BadRequest(new { message = "Nombre y telefono son obligatorios." });

        var phoneToSearch = NormalizePhone(dto.Phone);
        var existingLead = await _db.Leads
            .Where(l => (l.Phone == dto.Phone || l.Phone == phoneToSearch) && l.Status != LeadStatus.Won && l.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingLead != null)
        {
            MergeStructuredWhatsAppCapture(existingLead, dto);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Lead WhatsApp enriquecido: {Phone} -> Lead #{Id}", dto.Phone, existingLead.Id);

            if (!string.IsNullOrWhiteSpace(dto.Transcript))
            {
                await _leadService.AddActivityAsync(existingLead.Id, new LeadActivity
                {
                    Type = "WhatsApp",
                    Description = $"Nueva conversacion con bot:\n{dto.Transcript}",
                    CreatedBy = "WhatsApp Bot"
                }, cancellationToken);
            }

            return Ok(new
            {
                message = "Lead actualizado exitosamente.",
                leadId = existingLead.Id,
                updated = true
            });
        }

        var lead = new Lead
        {
            FullName = dto.Name.Trim(),
            Phone = dto.Phone.Trim(),
            Source = "WhatsApp",
            InterestedIn = dto.Interest?.Trim(),
            TravelDates = dto.Dates?.Trim(),
            Travelers = dto.Travelers?.Trim(),
            Status = LeadStatus.New,
            Notes = "Lead capturado por WhatsApp Bot."
        };

        var created = await _leadService.CreateAsync(lead, cancellationToken);

        if (!string.IsNullOrWhiteSpace(dto.Transcript))
        {
            await _leadService.AddActivityAsync(created.Id, new LeadActivity
            {
                Type = "WhatsApp",
                Description = $"Conversacion capturada por bot:\n{dto.Transcript}",
                CreatedBy = "WhatsApp Bot"
            }, cancellationToken);
        }

        _logger.LogInformation("Nuevo lead WhatsApp: #{Id} - {Name} ({Phone})", created.Id, created.FullName, created.Phone);

        return StatusCode(201, new
        {
            message = "Lead creado exitosamente.",
            leadId = created.Id,
            name = created.FullName,
            phone = created.Phone
        });
    }

    [HttpPost("whatsapp/message")]
    public async Task<ActionResult> WhatsAppMessage(
        [FromBody] WhatsAppMessageDto dto,
        CancellationToken cancellationToken)
    {
        if (!ValidateSecret())
            return Unauthorized(new { message = "Secret invalido." });

        if (string.IsNullOrWhiteSpace(dto.Phone) || string.IsNullOrWhiteSpace(dto.Message))
            return BadRequest(new { message = "Phone y message son obligatorios." });

        if (dto.Sender == "Cliente")
        {
            var handledOperationally = await _whatsAppDeliveryService.TryHandleIncomingOperationalMessageAsync(
                dto.Phone,
                dto.Message,
                cancellationToken);

            if (handledOperationally)
                return Ok(new { handledBy = "operational" });
        }

        var phoneToSearch = NormalizePhone(dto.Phone);
        var lead = await _db.Leads
            .Where(l => (l.Phone == dto.Phone || l.Phone == phoneToSearch) && l.Status != LeadStatus.Won && l.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(cancellationToken);

        if (lead == null)
        {
            if (dto.SkipLeadAutoCreation)
            {
                return Ok(new
                {
                    handledBy = "none",
                    autoCreated = false,
                    allowBotCapture = true
                });
            }

            _logger.LogInformation("Auto-creando lead para mensaje entrante de: {Phone}", dto.Phone);
            lead = new Lead
            {
                FullName = $"Nuevo contacto WhatsApp ({dto.Phone})",
                Phone = dto.Phone,
                Source = "WhatsApp",
                Status = LeadStatus.New,
                Notes = "Lead creado automaticamente al recibir un mensaje sin proceso de bot completado."
            };
            lead = await _leadService.CreateAsync(lead, cancellationToken);
        }

        await _leadService.AddActivityAsync(lead.Id, new LeadActivity
        {
            Type = "WhatsApp",
            Description = dto.Message,
            CreatedBy = dto.Sender == "Cliente" ? $"WhatsApp ({lead.FullName})" : "Agente CRM"
        }, cancellationToken);

        return Ok(new
        {
            handledBy = "lead",
            leadId = lead.Id,
            autoCreated = lead.FullName.StartsWith("Nuevo contacto"),
            allowBotCapture = LeadNeedsQualification(lead)
        });
    }

    [HttpPost("/api/leads/{leadId}/whatsapp-message")]
    [Authorize]
    public async Task<ActionResult> SendWhatsAppMessage(
        int leadId,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var lead = await _leadService.GetByIdAsync(leadId, cancellationToken);
        if (lead == null) return NotFound(new { message = "Lead no encontrado." });

        if (string.IsNullOrWhiteSpace(lead.Phone))
            return BadRequest(new { message = "El lead no tiene telefono." });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "El mensaje es obligatorio." });

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
            await _leadService.AddActivityAsync(leadId, new LeadActivity
            {
                Type = "WhatsApp",
                Description = request.Message,
                CreatedBy = userName
            }, cancellationToken);

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
    [Authorize]
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
            return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }

    [HttpPost("reload")]
    [Authorize]
    public async Task<IActionResult> ReloadBotConfig()
    {
        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Webhook-Secret", secret);
            var response = await client.PostAsync($"{botUrl}/reload", null);

            if (response.IsSuccessStatusCode) return Ok(new { success = true });
            return StatusCode((int)response.StatusCode, "Error al notificar al bot");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> LogoutBot()
    {
        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Webhook-Secret", secret);
            var response = await client.PostAsync($"{botUrl}/logout", null);

            if (response.IsSuccessStatusCode) return Ok(new { success = true });
            return StatusCode((int)response.StatusCode, "Error al cerrar sesion del bot");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }

    [HttpGet("logs")]
    [Authorize]
    public async Task<IActionResult> GetBotLogs()
    {
        var botUrl = _config["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";

        try
        {
            var client = _httpClientFactory.CreateClient();
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
            return StatusCode(500, $"Error interno: {ex.Message}");
        }
    }
}

public class SendMessageRequest
{
    public string Message { get; set; } = string.Empty;
}
