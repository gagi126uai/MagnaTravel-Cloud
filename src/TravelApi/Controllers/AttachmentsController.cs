using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public AttachmentsController(IAttachmentService attachmentService, ILogger<AttachmentsController> logger, EntityReferenceResolver entityReferenceResolver)
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
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error uploading attachment: {ex.Message}");
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
            return NotFound(ex.Message);
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
