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
    Task<VoucherDto> IssueVoucherAsync(string voucherPublicIdOrLegacyId, IssueVoucherRequest request, OperationActor actor, CancellationToken cancellationToken);
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
