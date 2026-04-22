using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TravelApi.Application.Interfaces;

namespace TravelApi.Controllers;

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
    public async Task<ActionResult> GetAttachments(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting attachments for Reserva {ReservaId}", reservaPublicIdOrLegacyId);
        var attachments = await _attachmentService.GetAttachmentsAsync(reservaPublicIdOrLegacyId, cancellationToken);
        return Ok(attachments);
    }

    [HttpPost("upload/{reservaPublicIdOrLegacyId}")]
    [EnableRateLimiting("uploads")]
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

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<ActionResult> DeleteAttachment(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        try
        {
            await _attachmentService.DeleteAttachmentAsync(publicIdOrLegacyId, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
