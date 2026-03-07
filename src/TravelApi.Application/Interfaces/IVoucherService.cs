namespace TravelApi.Application.Interfaces;

public interface IVoucherService
{
    Task<byte[]> GenerateVoucherAsync(int travelFileId, CancellationToken cancellationToken);
}
