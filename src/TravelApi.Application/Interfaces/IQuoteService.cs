using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IQuoteService
{
    Task<List<Quote>> GetAllAsync(CancellationToken cancellationToken);
    Task<Quote?> GetByIdAsync(int id, CancellationToken cancellationToken);
    Task<Quote> CreateAsync(Quote quote, CancellationToken cancellationToken);
    Task<Quote> UpdateAsync(int id, Quote updated, CancellationToken cancellationToken);
    Task DeleteAsync(int id, CancellationToken cancellationToken);
    Task<Quote> AddItemAsync(int quoteId, QuoteItem item, CancellationToken cancellationToken);
    Task RemoveItemAsync(int quoteId, int itemId, CancellationToken cancellationToken);
    Task<Quote> UpdateStatusAsync(int id, string status, CancellationToken cancellationToken);
    Task<int> ConvertToFileAsync(int quoteId, CancellationToken cancellationToken);
    Task RecalculateTotalsAsync(int quoteId, CancellationToken cancellationToken);
}
