using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    private readonly IAttachmentService _attachmentService;
    private readonly ILogger<AttachmentsController> _logger;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public AttachmentsController(IAttachmentService attachmentService, ILogger<AttachmentsController> logger, IEntityReferenceResolver entityReferenceResolver)
    {
        _attachmentService = attachmentService;
        _logger = logger;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet("reserva/{reservaPublicIdOrLegacyId}")]
    public async Task<ActionResult> GetAttachments(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, cancellationToken);
        _logger.LogInformation("Getting attachments for ReservaId: {ReservaId}", reservaId);
        var attachments = await _attachmentService.GetAttachmentsAsync(reservaId, cancellationToken);
        return Ok(attachments);
    }

    [HttpPost("upload/{reservaPublicIdOrLegacyId}")]
    [EnableRateLimiting("uploads")]
    public async Task<ActionResult> UploadAttachment(string reservaPublicIdOrLegacyId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        try
        {
            var reservaId = await _entityReferenceResolver.ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, cancellationToken);
            var uploadedBy = User.Identity?.Name ?? "System";
            using var stream = file.OpenReadStream();
            var attachment = await _attachmentService.UploadAttachmentAsync(
                reservaId, 
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
        catch (InvalidOperationException)
        {
            return BadRequest(new { message = "El archivo adjunto es invalido o no esta permitido." });
        }
        catch
        {
            return Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo cargar el archivo adjunto.");
        }
    }

    [HttpGet("{publicIdOrLegacyId}/download")]
    public async Task<ActionResult> DownloadAttachment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<ReservaAttachment>(publicIdOrLegacyId, cancellationToken);
            var (bytes, contentType, fileName) = await _attachmentService.DownloadAttachmentAsync(id, cancellationToken);
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

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<ActionResult> DeleteAttachment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _entityReferenceResolver.ResolveRequiredIdAsync<ReservaAttachment>(publicIdOrLegacyId, cancellationToken);
            await _attachmentService.DeleteAttachmentAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
