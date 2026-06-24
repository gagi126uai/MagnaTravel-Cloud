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

    /// <summary>
    /// Borra un adjunto (registro + archivo en MinIO). B3/OBS-2 (2026-06-24): bloqueado por estado terminal
    /// (en Finalizada/Anulada/Perdida/Esperando reembolso los documentos son solo lectura: borrar = modificar)
    /// y AUDITADO (quien/cuando/que archivo), igual que subir y renombrar. <paramref name="deletedBy"/> es el
    /// actor para el rastro.
    /// </summary>
    Task DeleteAttachmentAsync(string attachmentPublicIdOrLegacyId, string deletedBy, CancellationToken ct);
}
