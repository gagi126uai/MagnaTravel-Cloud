namespace TravelApi.Application.Interfaces;

public interface IBnaExchangeRateService
{
    Task<BnaUsdSellerRateDto?> GetUsdSellerRateAsync(CancellationToken cancellationToken);
}
