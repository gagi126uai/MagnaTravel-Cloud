namespace TravelApi.Application.Interfaces;

public interface IVoucherService
{
    Task<byte[]> GenerateVoucherHtmlAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<byte[]> GenerateVoucherPdfAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken);
}
