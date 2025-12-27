using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Search;
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
            .Where(customer =>
                customer.FullName.ToLower().Contains(normalized) ||
                (customer.Email != null && customer.Email.ToLower().Contains(normalized)) ||
                (customer.Phone != null && customer.Phone.ToLower().Contains(normalized)))
            .OrderBy(customer => customer.FullName)
            .Take(5)
            .Select(customer => new CustomerSearchResult(
                customer.Id,
                customer.FullName,
                customer.Email,
                customer.Phone))
            .ToListAsync(cancellationToken);

        var vouchers = await _dbContext.Reservations
            .AsNoTracking()
            .Include(reservation => reservation.Customer)
            .Where(reservation =>
                reservation.ReferenceCode.ToLower().Contains(normalized) ||
                reservation.Customer.FullName.ToLower().Contains(normalized))
            .OrderByDescending(reservation => reservation.CreatedAt)
            .Take(5)
            .Select(reservation => new ReservationSearchResult(
                reservation.Id,
                reservation.ReferenceCode,
                reservation.Status,
                reservation.TotalAmount,
                reservation.Customer.FullName))
            .ToListAsync(cancellationToken);

        var payments = await _dbContext.Payments
            .AsNoTracking()
            .Include(payment => payment.Reservation)
            .Where(payment =>
                payment.Reservation.ReferenceCode.ToLower().Contains(normalized) ||
                payment.Method.ToLower().Contains(normalized) ||
                payment.Status.ToLower().Contains(normalized))
            .OrderByDescending(payment => payment.PaidAt)
            .Take(5)
            .Select(payment => new PaymentSearchResult(
                payment.Id,
                payment.Amount,
                payment.Status,
                payment.Method,
                payment.Reservation.ReferenceCode))
            .ToListAsync(cancellationToken);

        var response = new SearchResultsResponse(query, customers, vouchers, payments);

        return Ok(response);
    }
}
