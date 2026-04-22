using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IQuoteService
{
    Task<List<QuoteSummaryDto>> GetAllAsync(CancellationToken cancellationToken);
    Task<QuoteDetailDto?> GetByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<QuoteDetailDto> CreateAsync(UpsertQuoteRequest request, CancellationToken cancellationToken);
    Task<QuoteDetailDto> UpdateAsync(string publicIdOrLegacyId, UpsertQuoteRequest updated, CancellationToken cancellationToken);
    Task DeleteAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task<QuoteDetailDto> AddItemAsync(string quotePublicIdOrLegacyId, UpsertQuoteItemRequest item, CancellationToken cancellationToken);
    Task RemoveItemAsync(string quotePublicIdOrLegacyId, string itemPublicIdOrLegacyId, CancellationToken cancellationToken);
    Task<QuoteDetailDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, CancellationToken cancellationToken);
    Task<QuoteConversionResultDto> ConvertToFileAsync(string publicIdOrLegacyId, CancellationToken cancellationToken);
    Task RecalculateTotalsAsync(int quoteId, CancellationToken cancellationToken);
}
