using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class SearchService : ISearchService
{
    private readonly AppDbContext _dbContext;

    public SearchService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SearchResultsResponse> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResultsResponse(string.Empty, [], [], []);
        }

        var normalized = query.Trim().ToLowerInvariant();

        var customers = await _dbContext.Customers
            .AsNoTracking()
            .Where(c => c.FullName.ToLower().Contains(normalized) ||
                (c.Email != null && c.Email.ToLower().Contains(normalized)) ||
                (c.Phone != null && c.Phone.ToLower().Contains(normalized)))
            .OrderBy(c => c.FullName)
            .Take(5)
            .Select(c => new CustomerSearchResult(c.Id, c.FullName, c.Email, c.Phone))
            .ToListAsync(cancellationToken);

        var reservas = await _dbContext.Reservas
            .AsNoTracking()
            .Include(f => f.Payer)
            .Where(f => f.NumeroReserva.ToLower().Contains(normalized) ||
                f.Name.ToLower().Contains(normalized) ||
                (f.Payer != null && f.Payer.FullName.ToLower().Contains(normalized)))
            .OrderByDescending(f => f.CreatedAt)
            .Take(5)
            .Select(f => new ReservaSearchResult(f.Id, f.NumeroReserva, f.Name, f.Status.ToString(), f.Payer != null ? f.Payer.FullName : null))
            .ToListAsync(cancellationToken);

        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Reserva)
            .Where(p => p.Method.ToLower().Contains(normalized) ||
                p.Status.ToString().ToLower().Contains(normalized))
            .OrderByDescending(p => p.PaidAt)
            .Take(5)
            .Select(p => new PaymentSearchResult(
                p.Id, 
                p.Amount, 
                p.Status.ToString(), 
                p.Method, 
                p.Reserva != null ? p.Reserva.NumeroReserva : null))
            .ToListAsync(cancellationToken);

        return new SearchResultsResponse(query, customers, reservas, payments);
    }
}
