using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    [HttpGet("file/{travelFileId}")]
    public async Task<ActionResult> GetAttachments(int travelFileId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting attachments for TravelFileId: {TravelFileId}", travelFileId);
        var attachments = await _attachmentService.GetAttachmentsAsync(travelFileId, cancellationToken);
        return Ok(attachments);
    }

    [HttpPost("upload/{travelFileId}")]
    public async Task<ActionResult> UploadAttachment(int travelFileId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        try
        {
            var uploadedBy = User.Identity?.Name ?? "System";
            using var stream = file.OpenReadStream();
            var attachment = await _attachmentService.UploadAttachmentAsync(
                travelFileId, 
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

    [HttpGet("{id}/download")]
    public async Task<ActionResult> DownloadAttachment(int id, CancellationToken cancellationToken)
    {
        try
        {
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

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteAttachment(int id, CancellationToken cancellationToken)
    {
        try
        {
            await _attachmentService.DeleteAttachmentAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
