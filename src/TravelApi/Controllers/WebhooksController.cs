using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

/// <summary>
/// Endpoints públicos (sin JWT) para recibir datos desde servicios externos.
/// Protegidos con un header secreto compartido (X-Webhook-Secret).
/// </summary>
[ApiController]
[Route("api/webhooks")]
public class WebhooksController : ControllerBase
{
    private readonly ILeadService _leadService;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(
        ILeadService leadService,
        AppDbContext db,
        IConfiguration config,
        ILogger<WebhooksController> logger)
    {
        _leadService = leadService;
        _db = db;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Recibe leads desde el bot de WhatsApp.
    /// POST /api/webhooks/whatsapp
    /// Header requerido: X-Webhook-Secret
    /// </summary>
    [HttpPost("whatsapp")]
    public async Task<ActionResult> WhatsAppLead(
        [FromBody] WhatsAppWebhookDto dto,
        CancellationToken cancellationToken)
    {
        // 1. Validar secret
        var expectedSecret = _config["WhatsApp:WebhookSecret"] ?? "CHANGE_THIS_SECRET";
        var providedSecret = Request.Headers["X-Webhook-Secret"].FirstOrDefault();

        if (string.IsNullOrEmpty(providedSecret) || providedSecret != expectedSecret)
        {
            _logger.LogWarning("Webhook WhatsApp rechazado: secret inválido desde {IP}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { message = "Secret inválido." });
        }

        // 2. Validar datos mínimos
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Phone))
        {
            return BadRequest(new { message = "Nombre y teléfono son obligatorios." });
        }

        // 3. Verificar duplicados: buscar lead activo con el mismo teléfono
        var existingLead = await _db.Leads
            .Where(l => l.Phone == dto.Phone
                && l.Status != LeadStatus.Won
                && l.Status != LeadStatus.Lost)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingLead != null)
        {
            _logger.LogInformation("Lead duplicado de WhatsApp: {Phone} ya existe como Lead #{Id}",
                dto.Phone, existingLead.Id);

            // Agregar una actividad al lead existente con la nueva conversación
            if (!string.IsNullOrWhiteSpace(dto.Transcript))
            {
                await _leadService.AddActivityAsync(existingLead.Id, new LeadActivity
                {
                    Type = "WhatsApp",
                    Description = $"Nueva conversación con bot:\n{dto.Transcript}",
                    CreatedBy = "WhatsApp Bot"
                }, cancellationToken);
            }

            return Conflict(new
            {
                message = "Ya existe un lead activo con este teléfono.",
                leadId = existingLead.Id
            });
        }

        // 4. Crear el lead
        var lead = new Lead
        {
            FullName = dto.Name.Trim(),
            Phone = dto.Phone.Trim(),
            Source = "WhatsApp",
            InterestedIn = dto.Interest?.Trim(),
            Status = LeadStatus.New,
            Notes = $"Lead capturado automáticamente por el bot de WhatsApp.",
        };

        var created = await _leadService.CreateAsync(lead, cancellationToken);

        // 5. Registrar la actividad con el transcript
        if (!string.IsNullOrWhiteSpace(dto.Transcript))
        {
            await _leadService.AddActivityAsync(created.Id, new LeadActivity
            {
                Type = "WhatsApp",
                Description = $"Conversación capturada por bot:\n{dto.Transcript}",
                CreatedBy = "WhatsApp Bot"
            }, cancellationToken);
        }

        _logger.LogInformation("✅ Nuevo lead de WhatsApp: #{Id} — {Name} ({Phone}) — Interés: {Interest}",
            created.Id, created.FullName, created.Phone, created.InterestedIn ?? "No especificado");

        return StatusCode(201, new
        {
            message = "Lead creado exitosamente.",
            leadId = created.Id,
            name = created.FullName,
            phone = created.Phone
        });
    }
}
