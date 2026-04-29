using System.Net.Http.Headers;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class VoucherServiceHttpProxy : ReservationsServiceHttpProxyBase, IVoucherService
{
    public VoucherServiceHttpProxy(HttpClient httpClient)
        : base(httpClient)
    {
    }

    public Task<byte[]> GenerateVoucherHtmlAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
        => GetBytesAsync($"api/reservas/{reservaPublicIdOrLegacyId}/voucher", cancellationToken);

    public Task<byte[]> GenerateVoucherPdfAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
        => GetBytesAsync($"api/reservas/{reservaPublicIdOrLegacyId}/voucher/pdf", cancellationToken);

    public Task<IReadOnlyList<VoucherDto>> GetVouchersAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
        => GetAsync<IReadOnlyList<VoucherDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/vouchers", cancellationToken);

    public Task<VoucherDto> GenerateVoucherRecordAsync(
        string reservaPublicIdOrLegacyId,
        GenerateVoucherRequest request,
        OperationActor actor,
        CancellationToken cancellationToken)
        => PostAsync<GenerateVoucherRequest, VoucherDto>($"api/reservas/{reservaPublicIdOrLegacyId}/vouchers/generate", request, cancellationToken);

    public async Task<VoucherDto> UploadExternalVoucherAsync(
        string reservaPublicIdOrLegacyId,
        UploadExternalVoucherRequest request,
        Stream stream,
        string fileName,
        string contentType,
        long fileSize,
        OperationActor actor,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "File", fileName);
        content.Add(new StringContent(request.Scope), nameof(request.Scope));
        content.Add(new StringContent(request.ExternalOrigin), nameof(request.ExternalOrigin));

        foreach (var passengerId in request.PassengerIds)
        {
            content.Add(new StringContent(passengerId), nameof(request.PassengerIds));
        }

        return await PostMultipartAsync<VoucherDto>($"api/reservas/{reservaPublicIdOrLegacyId}/vouchers/external", content, cancellationToken);
    }

    public Task<VoucherDto> IssueVoucherAsync(string voucherPublicIdOrLegacyId, IssueVoucherRequest request, OperationActor actor, CancellationToken cancellationToken)
        => PostAsync<IssueVoucherRequest, VoucherDto>($"api/vouchers/{voucherPublicIdOrLegacyId}/issue", request, cancellationToken);

    public Task<VoucherDto> ApproveVoucherIssueAsync(string voucherPublicIdOrLegacyId, OperationActor actor, CancellationToken cancellationToken)
        => PostAsync<object, VoucherDto>($"api/vouchers/{voucherPublicIdOrLegacyId}/approve", new { }, cancellationToken);

    public Task<VoucherDto> RejectVoucherIssueAsync(string voucherPublicIdOrLegacyId, RejectVoucherRequest request, OperationActor actor, CancellationToken cancellationToken)
        => PostAsync<RejectVoucherRequest, VoucherDto>($"api/vouchers/{voucherPublicIdOrLegacyId}/reject", request, cancellationToken);

    public Task<VoucherDto> EnsureVoucherCanBeSentAsync(
        string reservaPublicIdOrLegacyId,
        string voucherPublicIdOrLegacyId,
        string? passengerPublicIdOrLegacyId,
        VoucherExceptionRequest? exception,
        OperationActor actor,
        CancellationToken cancellationToken)
        => PostAsync<EnsureVoucherSendRequest, VoucherDto>(
            $"api/vouchers/{voucherPublicIdOrLegacyId}/ensure-send",
            new EnsureVoucherSendRequest
            {
                ReservaId = reservaPublicIdOrLegacyId,
                PassengerId = passengerPublicIdOrLegacyId,
                Exception = exception
            },
            cancellationToken);

    public async Task RecordVoucherSentAsync(string voucherPublicIdOrLegacyId, OperationActor actor, string? reason, CancellationToken cancellationToken)
    {
        _ = await PostAsync<object, VoucherDto>($"api/vouchers/{voucherPublicIdOrLegacyId}/sent", new { reason }, cancellationToken);
    }

    public Task<(byte[] Bytes, string ContentType, string FileName)> DownloadVoucherAsync(string voucherPublicIdOrLegacyId, CancellationToken cancellationToken)
        => GetFileAsync($"api/vouchers/{voucherPublicIdOrLegacyId}/download", cancellationToken);
}
