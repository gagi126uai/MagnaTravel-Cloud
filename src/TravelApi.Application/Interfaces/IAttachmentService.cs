using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IAttachmentService
{
    Task<IEnumerable<AttachmentDto>> GetAttachmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct);
    Task<AttachmentDto> UploadAttachmentAsync(string reservaPublicIdOrLegacyId, Stream stream, string fileName, string contentType, string uploadedBy, CancellationToken ct);
    Task<(byte[] Bytes, string ContentType, string FileName)> DownloadAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct);

    /// <summary>
    /// Renombra la etiqueta (FileName) de un adjunto ya cargado. Es un cambio de metadato:
    /// NO mueve ni reescribe el archivo fisico en MinIO ni toca el ContentType. Por eso se
    /// preserva la extension original del archivo (la extension define como se abre/descarga).
    /// </summary>
    Task<AttachmentDto> RenameAttachmentAsync(string attachmentPublicIdOrLegacyId, string newFileName, string modifiedBy, CancellationToken ct);

    Task DeleteAttachmentAsync(string attachmentPublicIdOrLegacyId, CancellationToken ct);
}
