namespace TravelApi.Application.Interfaces;

public interface IVoucherService
{
    Task<byte[]> GenerateVoucherHtmlAsync(int reservaId, CancellationToken cancellationToken);
    Task<byte[]> GenerateVoucherPdfAsync(int reservaId, CancellationToken cancellationToken);
}
