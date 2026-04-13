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
    private const int MaxAttachmentCountPerReserva = 20;
    private const long MaxAttachmentSizeBytes = 10 * 1024 * 1024;

    private static readonly Dictionary<string, string[]> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new[] { "application/pdf" },
        [".png"] = new[] { "image/png" },
        [".jpg"] = new[] { "image/jpeg" },
        [".jpeg"] = new[] { "image/jpeg" },
        [".doc"] = new[] { "application/msword" },
        [".docx"] = new[] { "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/zip" },
        [".xls"] = new[] { "application/vnd.ms-excel" },
        [".xlsx"] = new[] { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/zip" }
    };

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
                PublicId = a.PublicId,
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
        var existingCount = await _attachmentRepo.Query().CountAsync(a => a.ReservaId == reservaId, ct);
        if (existingCount >= MaxAttachmentCountPerReserva)
        {
            throw new InvalidOperationException("Se alcanzó el límite de adjuntos permitidos para esta reserva.");
        }

        var safeFileName = SanitizeOriginalFileName(fileName);
        var extension = Path.GetExtension(safeFileName);
        if (!AllowedTypes.ContainsKey(extension))
        {
            throw new InvalidOperationException("El tipo de archivo no está permitido.");
        }

        await using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            throw new InvalidOperationException("El archivo está vacío.");
        }

        if (buffer.Length > MaxAttachmentSizeBytes)
        {
            throw new InvalidOperationException("El archivo supera el tamaño máximo permitido de 10 MB.");
        }

        var normalizedContentType = (contentType ?? string.Empty).Trim();
        if (AllowedTypes.TryGetValue(extension, out var allowedContentTypes) &&
            allowedContentTypes.Length > 0 &&
            !allowedContentTypes.Contains(normalizedContentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("El contenido del archivo no coincide con el tipo declarado.");
        }

        if (!MatchesFileSignature(extension, buffer.ToArray()))
        {
            throw new InvalidOperationException("La firma del archivo no coincide con el tipo permitido.");
        }

        var uploadsFolder = Path.Combine(_env.ContentRootPath, "Uploads", "Files", DateTime.UtcNow.Year.ToString());
        if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

        var storedFileName = $"{Guid.NewGuid()}{extension}";
        var filePath = Path.Combine(uploadsFolder, storedFileName);

        buffer.Position = 0;
        await using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await buffer.CopyToAsync(stream, ct);
        }

        var attachment = new ReservaAttachment
        {
            ReservaId = reservaId,
            FileName = safeFileName,
            StoredFileName = Path.Combine(DateTime.UtcNow.Year.ToString(), storedFileName),
            ContentType = normalizedContentType,
            FileSize = buffer.Length,
            UploadedBy = uploadedBy,
            UploadedAt = DateTime.UtcNow
        };

        await _attachmentRepo.AddAsync(attachment, ct);

        return new AttachmentDto
        {
            PublicId = attachment.PublicId,
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
        var filePath = GetSafeFilePath(uploadsFolder, attachment.StoredFileName);

        if (!File.Exists(filePath)) throw new FileNotFoundException("File not found on server.");

        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        return (bytes, "application/octet-stream", attachment.FileName);
    }

    public async Task DeleteAttachmentAsync(int id, CancellationToken ct)
    {
        var attachment = await _attachmentRepo.GetByIdAsync(id, ct);
        if (attachment == null) throw new KeyNotFoundException("Attachment not found.");

        await _attachmentRepo.DeleteAsync(attachment, ct);

        try
        {
            var uploadsFolder = Path.Combine(_env.ContentRootPath, "Uploads", "Files");
            var filePath = GetSafeFilePath(uploadsFolder, attachment.StoredFileName);
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

    private static string SanitizeOriginalFileName(string fileName)
    {
        var original = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(original))
        {
            original = "adjunto";
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            original = original.Replace(invalidChar, '_');
        }

        return original;
    }

    private static bool MatchesFileSignature(string extension, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        return extension.ToLowerInvariant() switch
        {
            ".pdf" => bytes.AsSpan().StartsWith("%PDF"u8),
            ".png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
            ".jpg" or ".jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            ".docx" or ".xlsx" => bytes.AsSpan().StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) ||
                                  bytes.AsSpan().StartsWith(new byte[] { 0x50, 0x4B, 0x05, 0x06 }) ||
                                  bytes.AsSpan().StartsWith(new byte[] { 0x50, 0x4B, 0x07, 0x08 }),
            ".doc" or ".xls" => bytes.AsSpan().StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }),
            _ => false
        };
    }

    private static string GetSafeFilePath(string baseFolder, string storedFileName)
    {
        var fullPath = Path.GetFullPath(Path.Combine(baseFolder, storedFileName));
        if (!fullPath.StartsWith(Path.GetFullPath(baseFolder), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Ruta de archivo inválida.");
        }
        return fullPath;
    }
}
