using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IAttachmentService
{
    Task<IEnumerable<AttachmentDto>> GetAttachmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct);
    Task<AttachmentDto> UploadAttachmentAsync(string reservaPublicIdOrLegacyId, Stream stream, string fileName, string contentType, string uploadedBy, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType, string FileName)> DownloadAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct);
    Task DeleteAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct);
}
