using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/search")]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public SearchController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<SearchResultsResponse>> Search([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(new SearchResultsResponse(string.Empty, [], [], []));
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

        var files = await _dbContext.TravelFiles
            .AsNoTracking()
            .Include(f => f.Payer)
            .Where(f => f.FileNumber.ToLower().Contains(normalized) ||
                f.Name.ToLower().Contains(normalized) ||
                (f.Payer != null && f.Payer.FullName.ToLower().Contains(normalized)))
            .OrderByDescending(f => f.CreatedAt)
            .Take(5)
            .Select(f => new FileSearchResult(f.Id, f.FileNumber, f.Name, f.Status, f.Payer != null ? f.Payer.FullName : null))
            .ToListAsync(cancellationToken);

        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Include(p => p.Reservation)
            .ThenInclude(r => r!.TravelFile)
            .Where(p => p.Method.ToLower().Contains(normalized) ||
                p.Status.ToLower().Contains(normalized))
            .OrderByDescending(p => p.PaidAt)
            .Take(5)
            .Select(p => new PaymentSearchResult(
                p.Id, 
                p.Amount, 
                p.Status, 
                p.Method, 
                p.Reservation != null && p.Reservation.TravelFile != null ? p.Reservation.TravelFile.FileNumber : null))
            .ToListAsync(cancellationToken);

        return Ok(new SearchResultsResponse(query, customers, files, payments));
    }
}

// DTOs
public record SearchResultsResponse(
    string Query,
    List<CustomerSearchResult> Customers,
    List<FileSearchResult> Files,
    List<PaymentSearchResult> Payments);

public record CustomerSearchResult(int Id, string FullName, string? Email, string? Phone);
public record FileSearchResult(int Id, string FileNumber, string Name, string Status, string? PayerName);
public record PaymentSearchResult(int Id, decimal Amount, string Status, string Method, string? FileNumber);
