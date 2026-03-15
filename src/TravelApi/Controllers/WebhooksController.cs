using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

/// <summary>
/// Endpoints para WhatsApp: webhook público + envío de mensajes (autenticado).
/// </summary>
[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly ILeadService _leadService;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhooksController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public WebhooksController(
        ILeadService leadService,
        AppDbContext db,
        IConfiguration config,
        ILogger<WebhooksController> logger,
        IHttpClientFactory httpClientFactory)
    {
        _leadService = leadService;
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

    /// <summary>
    /// Recibe leads desde el bot de WhatsApp.
    /// POST /api/webhooks/whatsapp
    /// </summary>
    [HttpPost("whatsapp")]
    public async Task<ActionResult> WhatsAppLead(
        [FromBody] WhatsAppWebhookDto dto,
        CancellationToken cancellationToken)
    {
        if (!ValidateSecret())
        {
            _logger.LogWarning("Webhook rechazado: secret inválido desde {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { message = "Secret inválido." });
        }

        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Phone))
            return BadRequest(new { message = "Nombre y teléfono son obligatorios." });

        // Verificar duplicados
        var existingLead = await _db.Leads
            .Where(l => l.Phone == dto.Phone && l.Status != LeadStatus.Won && l.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingLead != null)
        {
            _logger.LogInformation("Lead duplicado WhatsApp: {Phone} → Lead #{Id}", dto.Phone, existingLead.Id);

            if (!string.IsNullOrWhiteSpace(dto.Transcript))
            {
                await _leadService.AddActivityAsync(existingLead.Id, new LeadActivity
                {
                    Type = "WhatsApp",
                    Description = $"Nueva conversación con bot:\n{dto.Transcript}",
                    CreatedBy = "WhatsApp Bot"
                }, cancellationToken);
            }

            return Conflict(new { message = "Ya existe un lead activo con este teléfono.", leadId = existingLead.Id });
        }

        // Crear lead
        var notes = "Lead capturado por WhatsApp Bot.";

        var lead = new Lead
        {
            FullName = dto.Name.Trim(),
            Phone = dto.Phone.Trim(),
            Source = "WhatsApp",
            InterestedIn = dto.Interest?.Trim(),
            TravelDates = dto.Dates?.Trim(),
            Travelers = dto.Travelers?.Trim(),
            Status = LeadStatus.New,
            Notes = notes,
        };

        var created = await _leadService.CreateAsync(lead, cancellationToken);

        if (!string.IsNullOrWhiteSpace(dto.Transcript))
        {
            await _leadService.AddActivityAsync(created.Id, new LeadActivity
            {
                Type = "WhatsApp",
                Description = $"Conversación capturada por bot:\n{dto.Transcript}",
                CreatedBy = "WhatsApp Bot"
            }, cancellationToken);
        }

        _logger.LogInformation("✅ Nuevo lead WhatsApp: #{Id} — {Name} ({Phone})", created.Id, created.FullName, created.Phone);

        return StatusCode(201, new
        {
            message = "Lead creado exitosamente.",
            leadId = created.Id,
            name = created.FullName,
            phone = created.Phone
        });
    }

    /// <summary>
    /// Recibe mensajes individuales del bot (para mantener el historial en el CRM).
    /// POST /api/webhooks/whatsapp/message
    /// </summary>
    [HttpPost("whatsapp/message")]
    public async Task<ActionResult> WhatsAppMessage(
        [FromBody] WhatsAppMessageDto dto,
        CancellationToken cancellationToken)
    {
        if (!ValidateSecret())
            return Unauthorized(new { message = "Secret inválido." });

        if (string.IsNullOrWhiteSpace(dto.Phone) || string.IsNullOrWhiteSpace(dto.Message))
            return BadRequest(new { message = "Phone y message son obligatorios." });

        // Buscar lead activo con ese teléfono
        var lead = await _db.Leads
            .Where(l => l.Phone == dto.Phone && l.Status != LeadStatus.Won && l.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(cancellationToken);

        if (lead == null)
            return Ok(new { message = "No hay lead activo con ese teléfono, mensaje ignorado." });

        // Guardar como actividad
        await _leadService.AddActivityAsync(lead.Id, new LeadActivity
        {
            Type = "WhatsApp",
            Description = dto.Message,
            CreatedBy = dto.Sender == "Cliente" ? $"WhatsApp ({lead.FullName})" : "Agente CRM"
        }, cancellationToken);

        return Ok(new { leadId = lead.Id });
    }

    /// <summary>
    /// Envía un mensaje de WhatsApp a un lead desde el CRM.
    /// POST /api/leads/{leadId}/whatsapp-message
    /// Requiere autenticación JWT.
    /// </summary>
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
            return BadRequest(new { message = "El lead no tiene teléfono." });

        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { message = "El mensaje es obligatorio." });

        // Enviar al bot via HTTP
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

            // Guardar como actividad
            var userName = User.Identity?.Name ?? "Agente";
            await _leadService.AddActivityAsync(leadId, new LeadActivity
            {
                Type = "WhatsApp",
                Description = request.Message,
                CreatedBy = userName
            }, cancellationToken);

            _logger.LogInformation("📤 WhatsApp enviado a {Phone} por {User}: {Msg}", lead.Phone, userName, request.Message.Substring(0, Math.Min(50, request.Message.Length)));

            return Ok(new { message = "Mensaje enviado exitosamente." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error conectando con bot WhatsApp");
            return StatusCode(502, new { message = "No se pudo conectar con el bot de WhatsApp." });
        }
    [HttpPost("reload")]
    [Authorize]
    public async Task<IActionResult> ReloadBotConfig()
    {
        var botUrl = _configuration["WhatsApp:BotUrl"] ?? "http://whatsapp-bot:3001";
        var secret = _configuration["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";

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
}

public class SendMessageRequest
{
    public string Message { get; set; } = string.Empty;
}
