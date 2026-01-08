using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Quotes;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/quotes")]
[Authorize]
public class QuotesController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public QuotesController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<QuoteSummaryDto>>> GetQuotes(CancellationToken cancellationToken)
    {
        var quotes = await _dbContext.Quotes
            .AsNoTracking()
            .Include(quote => quote.Customer)
            .Include(quote => quote.Versions)
            .OrderByDescending(quote => quote.CreatedAt)
            .Select(quote => new
            {
                quote.Id,
                quote.ReferenceCode,
                quote.Status,
                CustomerName = quote.Customer != null ? quote.Customer.FullName : string.Empty,
                quote.CreatedAt,
                Latest = quote.Versions
                    .OrderByDescending(version => version.VersionNumber)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var payload = quotes
            .Where(item => item.Latest is not null)
            .Select(item => new QuoteSummaryDto(
                item.Id,
                item.ReferenceCode,
                item.Status,
                item.CustomerName,
                item.Latest!.VersionNumber,
                item.Latest.ProductType,
                item.Latest.Currency,
                item.Latest.TotalAmount,
                item.CreatedAt))
            .ToList();

        return Ok(payload);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<QuoteDetailDto>> GetQuote(int id, CancellationToken cancellationToken)
    {
        var quote = await _dbContext.Quotes
            .AsNoTracking()
            .Include(found => found.Customer)
            .Include(found => found.Versions)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (quote is null)
        {
            return NotFound();
        }

        var dto = new QuoteDetailDto(
            quote.Id,
            quote.ReferenceCode,
            quote.Status,
            quote.Customer?.FullName ?? string.Empty,
            quote.CreatedAt,
            quote.Versions
                .OrderByDescending(version => version.VersionNumber)
                .Select(version => new QuoteVersionDto(
                    version.Id,
                    version.VersionNumber,
                    version.ProductType,
                    version.Currency,
                    version.TotalAmount,
                    version.ValidUntil,
                    version.Notes,
                    version.CreatedAt))
                .ToList());

        return Ok(dto);
    }

    [HttpPost]
    public async Task<ActionResult<QuoteDetailDto>> CreateQuote(
        CreateQuoteRequest request,
        CancellationToken cancellationToken)
    {
        var customerExists = await _dbContext.Customers
            .AsNoTracking()
            .AnyAsync(customer => customer.Id == request.CustomerId, cancellationToken);

        if (!customerExists)
        {
            return BadRequest("El cliente no existe.");
        }

        var status = string.IsNullOrWhiteSpace(request.Status) ? QuoteStatuses.Draft : request.Status.Trim();
        if (!QuoteStatuses.IsValid(status))
        {
            return BadRequest("El estado de la cotización no es válido.");
        }

        if (!IsVersionValid(request.Version))
        {
            return BadRequest("La versión de cotización no es válida.");
        }

        var quote = new Quote
        {
            ReferenceCode = request.ReferenceCode,
            Status = status,
            CustomerId = request.CustomerId
        };

        var version = new QuoteVersion
        {
            VersionNumber = 1,
            ProductType = request.Version.ProductType,
            Currency = request.Version.Currency,
            TotalAmount = request.Version.TotalAmount,
            ValidUntil = NormalizeUtc(request.Version.ValidUntil),
            Notes = request.Version.Notes
        };

        quote.Versions.Add(version);
        _dbContext.Quotes.Add(quote);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new QuoteDetailDto(
            quote.Id,
            quote.ReferenceCode,
            quote.Status,
            string.Empty,
            quote.CreatedAt,
            new List<QuoteVersionDto>
            {
                new(
                    version.Id,
                    version.VersionNumber,
                    version.ProductType,
                    version.Currency,
                    version.TotalAmount,
                    version.ValidUntil,
                    version.Notes,
                    version.CreatedAt)
            });

        return CreatedAtAction(nameof(GetQuote), new { id = quote.Id }, dto);
    }

    [HttpPost("{id:int}/versions")]
    public async Task<ActionResult<QuoteVersionDto>> CreateVersion(
        int id,
        CreateQuoteVersionRequest request,
        CancellationToken cancellationToken)
    {
        var quote = await _dbContext.Quotes
            .Include(found => found.Versions)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (quote is null)
        {
            return NotFound();
        }

        if (!IsVersionValid(request))
        {
            return BadRequest("La versión de cotización no es válida.");
        }

        var nextVersion = quote.Versions.Any()
            ? quote.Versions.Max(version => version.VersionNumber) + 1
            : 1;

        var version = new QuoteVersion
        {
            VersionNumber = nextVersion,
            ProductType = request.ProductType,
            Currency = request.Currency,
            TotalAmount = request.TotalAmount,
            ValidUntil = NormalizeUtc(request.ValidUntil),
            Notes = request.Notes
        };

        quote.Versions.Add(version);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new QuoteVersionDto(
            version.Id,
            version.VersionNumber,
            version.ProductType,
            version.Currency,
            version.TotalAmount,
            version.ValidUntil,
            version.Notes,
            version.CreatedAt);

        return CreatedAtAction(nameof(GetQuote), new { id }, dto);
    }

    private static bool IsVersionValid(CreateQuoteVersionRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.ProductType)
            && request.TotalAmount >= 0m;
    }

    private static DateTime? NormalizeUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        return value.Value.Kind == DateTimeKind.Utc
            ? value.Value
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
    }
}
