using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

// 2026-06-03: cierre de IDOR. Antes este controller era [Authorize] sin permission
// ni ownership: cualquier autenticado podia listar/descargar/renombrar/borrar/subir
// adjuntos de CUALQUIER reserva (documentos sensibles de clientes/pasajeros).
// Se replica el patron de VouchersController: [RequirePermission] coherente con
// reservas + [RequireOwnership] scopeado por la reserva, con bypass para
// admin/supervisores via ReservasViewAll. Endpoints keyed por reserva usan
// OwnedEntity.Reserva; los keyed por adjunto usan OwnedEntity.Attachment.
[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    private readonly IAttachmentService _attachmentService;
    private readonly ILogger<AttachmentsController> _logger;

    public AttachmentsController(IAttachmentService attachmentService, ILogger<AttachmentsController> logger)
    {
        _attachmentService = attachmentService;
        _logger = logger;
    }

    [HttpGet("reserva/{reservaPublicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> GetAttachments(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting attachments for Reserva {ReservaId}", reservaPublicIdOrLegacyId);
        var attachments = await _attachmentService.GetAttachmentsAsync(reservaPublicIdOrLegacyId, cancellationToken);
        return Ok(attachments);
    }

    [HttpPost("upload/{reservaPublicIdOrLegacyId}")]
    [EnableRateLimiting("uploads")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Reserva, "reservaPublicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> UploadAttachment(string reservaPublicIdOrLegacyId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded.");
        }

        try
        {
            var uploadedBy = User.Identity?.Name ?? "System";
            using var stream = file.OpenReadStream();
            var attachment = await _attachmentService.UploadAttachmentAsync(
                reservaPublicIdOrLegacyId,
                stream,
                file.FileName,
                file.ContentType,
                uploadedBy,
                cancellationToken);
            return Ok(attachment);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // B3 (2026-06-24): el servicio lanza InvalidOperationException tanto por archivo invalido/limite
            // como por estado terminal (documentos solo lectura). Surface el mensaje real (es texto de negocio
            // legible, sin datos sensibles) para que el usuario entienda por que no pudo subir.
            return BadRequest(new { message = ex.Message });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo cargar el archivo adjunto.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/download")]
    [RequirePermission(Permissions.ReservasView)]
    [RequireOwnership(OwnedEntity.Attachment, "publicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> DownloadAttachment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var (bytes, contentType, fileName) = await _attachmentService.DownloadAttachmentAsync(publicIdOrLegacyId, cancellationToken);
            return File(bytes, contentType, fileName);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Attachment file missing on disk for id {AttachmentId}", publicIdOrLegacyId);
            return NotFound();
        }
    }

    // Renombra la etiqueta del adjunto (FileName). Es una mutacion sobre la reserva,
    // por eso requiere ReservasEdit (igual que subir) + ownership por adjunto. El
    // bypass ReservasViewAll deja entrar a admin/supervisores.
    [HttpPatch("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasEdit)]
    [RequireOwnership(OwnedEntity.Attachment, "publicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> RenameAttachment(
        string publicIdOrLegacyId,
        [FromBody] RenameAttachmentRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.FileName))
        {
            return BadRequest(new { message = "El nombre del archivo no puede estar vacio." });
        }

        try
        {
            var modifiedBy = User.Identity?.Name ?? "System";
            var attachment = await _attachmentService.RenameAttachmentAsync(
                publicIdOrLegacyId,
                request.FileName,
                modifiedBy,
                cancellationToken);
            return Ok(attachment);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    [RequirePermission(Permissions.ReservasDelete)]
    [RequireOwnership(OwnedEntity.Attachment, "publicIdOrLegacyId", bypassPermission: Permissions.ReservasViewAll)]
    public async Task<ActionResult> DeleteAttachment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var deletedBy = User.Identity?.Name ?? "System";
            await _attachmentService.DeleteAttachmentAsync(publicIdOrLegacyId, deletedBy, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            // B3/OBS-2 (2026-06-24): en estado terminal los documentos son solo lectura -> no se borran.
            // Surface el mensaje real (texto de negocio legible, sin datos sensibles).
            return BadRequest(new { message = ex.Message });
        }
    }
}

public class RenameAttachmentRequest
{
    public string FileName { get; set; } = string.Empty;
}
