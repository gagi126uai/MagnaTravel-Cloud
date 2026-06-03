using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
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

    // Longitud maxima del FileName visible al usuario. La columna no esta acotada en la
    // entidad, pero limitamos para evitar etiquetas absurdas y mantener la UI legible.
    private const int MaxFileNameLength = 200;

    private readonly IRepository<ReservaAttachment> _attachmentRepo;
    private readonly IRepository<Reserva> _reservaRepo;
    private readonly IMinioClient _minioClient;
    private readonly ILogger<AttachmentService> _logger;
    // IAuditService es opcional para no romper construcciones por DI/tests que no lo provean.
    // Cuando esta presente, el renombre queda registrado en el log de auditoria.
    private readonly IAuditService? _auditService;
    private readonly string _bucketName;

    public AttachmentService(
        IRepository<ReservaAttachment> attachmentRepo,
        IRepository<Reserva> reservaRepo,
        IMinioClient minioClient,
        IConfiguration config,
        ILogger<AttachmentService> logger,
        IAuditService? auditService = null)
    {
        _attachmentRepo = attachmentRepo;
        _reservaRepo = reservaRepo;
        _minioClient = minioClient;
        _logger = logger;
        _auditService = auditService;
        _bucketName = config["Minio:BucketName"] ?? "reservations";
    }

    public async Task<IEnumerable<AttachmentDto>> GetAttachmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, ct);
        return await GetAttachmentsAsync(reservaId, ct);
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

    public async Task<AttachmentDto> UploadAttachmentAsync(string reservaPublicIdOrLegacyId, Stream fileStream, string fileName, string contentType, string uploadedBy, CancellationToken ct)
    {
        var reservaId = await ResolveReservaIdAsync(reservaPublicIdOrLegacyId, ct);
        return await UploadAttachmentAsync(reservaId, fileStream, fileName, contentType, uploadedBy, ct);
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

        var objectName = $"{DateTime.UtcNow.Year}/{Guid.NewGuid()}{extension}";

        try
        {
            buffer.Position = 0;
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithStreamData(buffer)
                .WithObjectSize(buffer.Length)
                .WithContentType(normalizedContentType);
            await _minioClient.PutObjectAsync(putObjectArgs, ct);
        }
        catch (MinioException e)
        {
            _logger.LogError(e, "Error uploading file to MinIO.");
            throw new InvalidOperationException("Falló la subida del adjunto al almacenamiento remoto.");
        }

        var attachment = new ReservaAttachment
        {
            ReservaId = reservaId,
            FileName = safeFileName,
            StoredFileName = objectName,
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

    public async Task<(byte[] Bytes, string ContentType, string FileName)> DownloadAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct)
    {
        var attachmentId = await ResolveAttachmentIdAsync(attachmentPublicIdOrLegacyId, ct);
        return await DownloadAttachmentAsync(attachmentId, ct);
    }

    public async Task<(byte[] Bytes, string ContentType, string FileName)> DownloadAttachmentAsync(int id, CancellationToken ct)
    {
        var attachment = await _attachmentRepo.GetByIdAsync(id, ct);
        if (attachment == null) throw new KeyNotFoundException("Attachment not found.");

        try
        {
            using var memoryStream = new MemoryStream();
            var args = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(attachment.StoredFileName.Replace('\\', '/'))
                .WithCallbackStream(stream => stream.CopyTo(memoryStream));
            await _minioClient.GetObjectAsync(args, ct);
            
            return (memoryStream.ToArray(), attachment.ContentType ?? "application/octet-stream", attachment.FileName);
        }
        catch (MinioException e)
        {
            _logger.LogError(e, "Error downloading file from MinIO.");
            throw new FileNotFoundException("File not found on remote storage.");
        }
    }

    public async Task<AttachmentDto> RenameAttachmentAsync(string attachmentPublicIdOrLegacyId, string newFileName, string modifiedBy, CancellationToken ct)
    {
        var attachmentId = await ResolveAttachmentIdAsync(attachmentPublicIdOrLegacyId, ct);
        return await RenameAttachmentAsync(attachmentId, newFileName, modifiedBy, ct);
    }

    public async Task<AttachmentDto> RenameAttachmentAsync(int id, string newFileName, string modifiedBy, CancellationToken ct)
    {
        var attachment = await _attachmentRepo.GetByIdAsync(id, ct);
        if (attachment == null) throw new KeyNotFoundException("Attachment not found.");

        // 1) Validar el nombre crudo antes de tocar nada. Vacio/solo-espacios se rechaza.
        var trimmed = (newFileName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("El nombre del archivo no puede estar vacio.");
        }

        // 2) Sanear como en el upload (saca path traversal y caracteres invalidos de nombre).
        var sanitized = SanitizeOriginalFileName(trimmed);

        // 3) Preservar la extension original. El renombre es solo una etiqueta: el archivo
        //    fisico y su ContentType no cambian, asi que la extension tampoco debe cambiar
        //    (si lo permitieramos, la descarga abriria un .pdf como si fuera .docx, p.ej.).
        var originalExtension = Path.GetExtension(attachment.FileName);
        if (!string.IsNullOrEmpty(originalExtension))
        {
            var providedExtension = Path.GetExtension(sanitized);
            if (!string.Equals(providedExtension, originalExtension, StringComparison.OrdinalIgnoreCase))
            {
                // Quitamos la extension que haya escrito el usuario (si la hay) y forzamos la original.
                var withoutExtension = string.IsNullOrEmpty(providedExtension)
                    ? sanitized
                    : sanitized[..^providedExtension.Length];
                sanitized = $"{withoutExtension}{originalExtension}";
            }
        }

        // 4) Validar longitud final (despues de forzar extension, para no pasarnos por agregarla).
        if (sanitized.Length > MaxFileNameLength)
        {
            throw new InvalidOperationException($"El nombre del archivo supera el maximo de {MaxFileNameLength} caracteres.");
        }

        var previousFileName = attachment.FileName;

        // Si el nombre efectivo no cambio, evitamos un UPDATE y un audit ruidosos.
        if (string.Equals(previousFileName, sanitized, StringComparison.Ordinal))
        {
            return MapToDto(attachment);
        }

        attachment.FileName = sanitized;
        await _attachmentRepo.UpdateAsync(attachment, ct);

        if (_auditService is not null)
        {
            // Auditamos solo metadato no sensible (nombres de etiqueta), nunca contenido del archivo.
            await _auditService.LogBusinessEventAsync(
                action: "Rename",
                entityName: nameof(ReservaAttachment),
                entityId: attachment.Id.ToString(),
                details: $"Adjunto de la reserva {attachment.ReservaId} renombrado de '{previousFileName}' a '{sanitized}'.",
                userId: modifiedBy,
                userName: modifiedBy,
                ct: ct);
        }

        return MapToDto(attachment);
    }

    public async Task DeleteAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct)
    {
        var attachmentId = await ResolveAttachmentIdAsync(attachmentPublicIdOrLegacyId, ct);
        await DeleteAttachmentAsync(attachmentId, ct);
    }

    public async Task DeleteAttachmentAsync(int id, CancellationToken ct)
    {
        var attachment = await _attachmentRepo.GetByIdAsync(id, ct);
        if (attachment == null) throw new KeyNotFoundException("Attachment not found.");

        await _attachmentRepo.DeleteAsync(attachment, ct);

        try
        {
            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(attachment.StoredFileName.Replace('\\', '/'));
            await _minioClient.RemoveObjectAsync(removeObjectArgs, ct);
        }
        catch (MinioException ex)
        {
            _logger.LogError(ex, "Error deleting file from MinIO: {FilePath}", attachment.StoredFileName);
        }
    }

    private async Task<int> ResolveReservaIdAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        var resolved = await _reservaRepo.Query()
            .AsNoTracking()
            .ResolveInternalIdAsync(reservaPublicIdOrLegacyId, ct);

        if (!resolved.HasValue && int.TryParse(reservaPublicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Reserva not found.");
    }

    private async Task<int> ResolveAttachmentIdAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct)
    {
        var resolved = await _attachmentRepo.Query()
            .AsNoTracking()
            .ResolveInternalIdAsync(attachmentPublicIdOrLegacyId, ct);

        if (!resolved.HasValue && int.TryParse(attachmentPublicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException("Attachment not found.");
    }

    private static AttachmentDto MapToDto(ReservaAttachment attachment)
    {
        return new AttachmentDto
        {
            PublicId = attachment.PublicId,
            FileName = attachment.FileName,
            FileSize = attachment.FileSize,
            ContentType = attachment.ContentType ?? string.Empty,
            UploadedBy = attachment.UploadedBy ?? string.Empty,
            UploadedAt = attachment.UploadedAt
        };
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


}
