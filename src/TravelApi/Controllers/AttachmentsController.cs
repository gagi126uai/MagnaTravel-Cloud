using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/attachments")]
[Authorize]
public class AttachmentsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AttachmentsController> _logger;

    public AttachmentsController(AppDbContext dbContext, IWebHostEnvironment environment, ILogger<AttachmentsController> logger)
    {
        _dbContext = dbContext;
        _environment = environment;
        _logger = logger;
    }

    [HttpGet("file/{travelFileId}")]
    public async Task<ActionResult> GetAttachments(int travelFileId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Getting attachments for TravelFileId: {TravelFileId}", travelFileId);

        var attachments = await _dbContext.TravelFileAttachments
            .Where(a => a.TravelFileId == travelFileId)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => new TravelApi.DTOs.AttachmentDto
            {
                Id = a.Id,
                FileName = a.FileName,
                FileSize = a.FileSize,
                ContentType = a.ContentType,
                UploadedBy = a.UploadedBy,
                UploadedAt = a.UploadedAt
            })
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Found {Count} attachments for TravelFileId: {TravelFileId}", attachments.Count, travelFileId);

        return Ok(attachments);
    }

    [HttpPost("upload/{travelFileId}")]
    public async Task<ActionResult> UploadAttachment(int travelFileId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        var travelFile = await _dbContext.TravelFiles.FindAsync(new object[] { travelFileId }, cancellationToken);
        if (travelFile == null)
            return NotFound("Travel file not found.");

        // Define storage path
        var uploadsFolder = Path.Combine(_environment.ContentRootPath, "Uploads", "Files", DateTime.UtcNow.Year.ToString());
        if (!Directory.Exists(uploadsFolder))
            Directory.CreateDirectory(uploadsFolder);

        var storedFileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
        var filePath = Path.Combine(uploadsFolder, storedFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        var attachment = new TravelFileAttachment
        {
            TravelFileId = travelFileId,
            FileName = file.FileName,
            StoredFileName = Path.Combine(DateTime.UtcNow.Year.ToString(), storedFileName), // Store relative path
            ContentType = file.ContentType,
            FileSize = file.Length,
            UploadedBy = User.Identity?.Name ?? "System",
            UploadedAt = DateTime.UtcNow
        };

        _dbContext.TravelFileAttachments.Add(attachment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Uploaded attachment {Id} for TravelFileId: {TravelFileId}", attachment.Id, travelFileId);

        return Ok(new TravelApi.DTOs.AttachmentDto
        {
            Id = attachment.Id,
            FileName = attachment.FileName,
            FileSize = attachment.FileSize,
            ContentType = attachment.ContentType,
            UploadedBy = attachment.UploadedBy,
            UploadedAt = attachment.UploadedAt
        });
    }

    [HttpGet("{id}/download")]
    public async Task<ActionResult> DownloadAttachment(int id, CancellationToken cancellationToken)
    {
        var attachment = await _dbContext.TravelFileAttachments.FindAsync(new object[] { id }, cancellationToken);
        if (attachment == null)
            return NotFound();

        var uploadsFolder = Path.Combine(_environment.ContentRootPath, "Uploads", "Files");
        var filePath = Path.Combine(uploadsFolder, attachment.StoredFileName);

        if (!System.IO.File.Exists(filePath))
            return NotFound("File not found on server.");

        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(attachment.FileName, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(filePath, cancellationToken);
        return File(bytes, contentType, attachment.FileName);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteAttachment(int id, CancellationToken cancellationToken)
    {
        var attachment = await _dbContext.TravelFileAttachments.FindAsync(new object[] { id }, cancellationToken);
        if (attachment == null)
            return NotFound();

        // Remove from DB
        _dbContext.TravelFileAttachments.Remove(attachment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Try remove from disk (swallow error if fails, to avoid blocking DB delete)
        try
        {
            var uploadsFolder = Path.Combine(_environment.ContentRootPath, "Uploads", "Files");
            var filePath = Path.Combine(uploadsFolder, attachment.StoredFileName);
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }
        }
        catch
        {
            // Log error in future
        }

        return NoContent();
    }
}
