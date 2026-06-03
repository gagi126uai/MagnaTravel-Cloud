using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IVoucherService
{
    Task<byte[]> GenerateVoucherHtmlAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<byte[]> GenerateVoucherPdfAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VoucherDto>> GetVouchersAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<VoucherDto> GenerateVoucherRecordAsync(string reservaPublicIdOrLegacyId, GenerateVoucherRequest request, OperationActor actor, CancellationToken cancellationToken);
    Task<VoucherDto> UploadExternalVoucherAsync(
        string reservaPublicIdOrLegacyId,
        UploadExternalVoucherRequest request,
        Stream stream,
        string fileName,
        string contentType,
        long fileSize,
        OperationActor actor,
        CancellationToken cancellationToken);
    /// <summary>
    /// Edita un voucher EXTERNO ya cargado: actualiza el origen (ExternalOrigin) y,
    /// opcionalmente, reemplaza el archivo almacenado. Solo aplica a vouchers externos
    /// y solo si el voucher esta en un estado editable (no anulado). Si <paramref name="stream"/>
    /// es null, se conserva el archivo actual.
    /// </summary>
    Task<VoucherDto> EditExternalVoucherAsync(
        string voucherPublicIdOrLegacyId,
        EditExternalVoucherRequest request,
        Stream? stream,
        string? fileName,
        string? contentType,
        long fileSize,
        OperationActor actor,
        CancellationToken cancellationToken);
    Task<VoucherDto> IssueVoucherAsync(string voucherPublicIdOrLegacyId, IssueVoucherRequest request, OperationActor actor, CancellationToken cancellationToken);
    Task<VoucherDto> ApproveVoucherIssueAsync(string voucherPublicIdOrLegacyId, OperationActor actor, CancellationToken cancellationToken);
    Task<VoucherDto> RejectVoucherIssueAsync(string voucherPublicIdOrLegacyId, RejectVoucherRequest request, OperationActor actor, CancellationToken cancellationToken);
    Task<VoucherDto> RevokeVoucherAsync(string voucherPublicIdOrLegacyId, RevokeVoucherRequest request, OperationActor actor, CancellationToken cancellationToken);
    Task<VoucherDto> EnsureVoucherCanBeSentAsync(
        string reservaPublicIdOrLegacyId,
        string voucherPublicIdOrLegacyId,
        string? passengerPublicIdOrLegacyId,
        VoucherExceptionRequest? exception,
        OperationActor actor,
        CancellationToken cancellationToken);
    Task RecordVoucherSentAsync(string voucherPublicIdOrLegacyId, OperationActor actor, string? reason, CancellationToken cancellationToken);
    Task<(byte[] Bytes, string ContentType, string FileName)> DownloadVoucherAsync(string voucherPublicIdOrLegacyId, CancellationToken cancellationToken);
}
