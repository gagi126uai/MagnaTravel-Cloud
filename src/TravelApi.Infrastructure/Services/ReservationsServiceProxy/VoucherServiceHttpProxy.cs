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
}
