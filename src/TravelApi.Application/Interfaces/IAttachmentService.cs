using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IAttachmentService
{
    Task<IEnumerable<AttachmentDto>> GetAttachmentsAsync(int reservaId, CancellationToken ct);
    Task<AttachmentDto> UploadAttachmentAsync(int reservaId, Stream stream, string fileName, string contentType, string uploadedBy, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType, string FileName)> DownloadAttachmentAsync(int id, CancellationToken ct);
    Task DeleteAttachmentAsync(int id, CancellationToken ct);
}
