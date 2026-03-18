using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class AttachmentService : IAttachmentService
{
    private readonly IRepository<ReservaAttachment> _attachmentRepo;
    private readonly IRepository<Reserva> _reservaRepo;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(
        IRepository<ReservaAttachment> attachmentRepo,
        IRepository<Reserva> reservaRepo,
        IWebHostEnvironment env,
        ILogger<AttachmentService> logger)
    {
        _attachmentRepo = attachmentRepo;
        _reservaRepo = reservaRepo;
        _env = env;
        _logger = logger;
    }

    public async Task<IEnumerable<AttachmentDto>> GetAttachmentsAsync(int reservaId, CancellationToken ct)
    {
        return await _attachmentRepo.Query()
            .Where(a => a.ReservaId == reservaId)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => new AttachmentDto
            {
                Id = a.Id,
                FileName = a.FileName,
                FileSize = a.FileSize,
                ContentType = a.ContentType,
                UploadedBy = a.UploadedBy,
                UploadedAt = a.UploadedAt
            })
            .ToListAsync(ct);
    }

    public async Task<AttachmentDto> UploadAttachmentAsync(int reservaId, Stream fileStream, string fileName, string contentType, string uploadedBy, CancellationToken ct)
    {
        var reserva = await _reservaRepo.GetByIdAsync(reservaId, ct);
        if (reserva == null) throw new KeyNotFoundException("Reserva not found.");

        var uploadsFolder = Path.Combine(_env.ContentRootPath, "Uploads", "Files", DateTime.UtcNow.Year.ToString());
        if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

        var storedFileName = $"{Guid.NewGuid()}{Path.GetExtension(fileName)}";
        var filePath = Path.Combine(uploadsFolder, storedFileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await fileStream.CopyToAsync(stream, ct);
        }

        var attachment = new ReservaAttachment
        {
            ReservaId = reservaId,
            FileName = fileName,
            StoredFileName = Path.Combine(DateTime.UtcNow.Year.ToString(), storedFileName),
            ContentType = contentType,
            FileSize = fileStream.Length,
            UploadedBy = uploadedBy,
            UploadedAt = DateTime.UtcNow
        };

        await _attachmentRepo.AddAsync(attachment, ct);

        return new AttachmentDto
        {
            Id = attachment.Id,
            FileName = attachment.FileName,
            FileSize = attachment.FileSize,
            ContentType = attachment.ContentType,
            UploadedBy = attachment.UploadedBy,
            UploadedAt = attachment.UploadedAt
        };
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> DownloadAttachmentAsync(int id, CancellationToken ct)
    {
        var attachment = await _attachmentRepo.GetByIdAsync(id, ct);
        if (attachment == null) throw new KeyNotFoundException("Attachment not found.");

        var uploadsFolder = Path.Combine(_env.ContentRootPath, "Uploads", "Files");
        var filePath = Path.Combine(uploadsFolder, attachment.StoredFileName);

        if (!File.Exists(filePath)) throw new FileNotFoundException("File not found on server.");

        var provider = new FileExtensionContentTypeProvider();
        if (!provider.TryGetContentType(attachment.FileName, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        return (bytes, contentType, attachment.FileName);
    }

    public async Task DeleteAttachmentAsync(int id, CancellationToken ct)
    {
        var attachment = await _attachmentRepo.GetByIdAsync(id, ct);
        if (attachment == null) throw new KeyNotFoundException("Attachment not found.");

        await _attachmentRepo.DeleteAsync(attachment, ct);

        try
        {
            var uploadsFolder = Path.Combine(_env.ContentRootPath, "Uploads", "Files");
            var filePath = Path.Combine(uploadsFolder, attachment.StoredFileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file from disk: {FilePath}", attachment.StoredFileName);
        }
    }
}
