using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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

    [HttpPost("{id:int}/book")]
    public async Task<ActionResult<int>> BookQuote(
        int id,
        [FromServices] UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken)
    {
        var quote = await _dbContext.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Versions)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);
            
        if (quote is null)
        {
            return NotFound("Cotización no encontrada.");
        }

        // B2B Logic: Check Agency Credit Limit
        var user = await userManager.GetUserAsync(User);
        if (user is not null)
        {
            // Load Agency explicitly as UserManager might not include navigation props by default or checks ID only
            // Better to fetch via DbContext to be sure we track changes
            var dbUser = await _dbContext.Users
                .Include(u => u.Agency)
                .FirstOrDefaultAsync(u => u.Id == user.Id, cancellationToken);

            if (dbUser?.Agency is not null)
            {
                var agency = dbUser.Agency;
                var validVersion = quote.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
                
                if (validVersion != null)
                {
                    if (agency.CurrentBalance + validVersion.TotalAmount > agency.CreditLimit)
                    {
                        return BadRequest($"Límite de crédito excedido. Disponible: {agency.CreditLimit - agency.CurrentBalance:C} (Requerido: {validVersion.TotalAmount:C})");
                    }
                    
                    // Increase Debt
                    agency.CurrentBalance += validVersion.TotalAmount;
                }
            }
        }
        
        if (quote.Status != QuoteStatuses.Sent && quote.Status != QuoteStatuses.Approved)
        {
             // Opcional: Permitir reservar si está en Draft? Mejor ser estricto.
             // return BadRequest("La cotización debe estar Enviada o Aprobada para reservarse.");
        }
        
        var validVersionForBooking = quote.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        if (validVersionForBooking is null)
        {
            return BadRequest("La cotización no tiene versiones válidas.");
        }
        
        // 1. Create Travel File
        var fileNumber = $"FILE-{DateTime.UtcNow.Year}-{DateTime.UtcNow.Ticks.ToString()[^4..]}"; // Simple generator
        var travelFile = new TravelFile
        {
            FileNumber = fileNumber,
            Name = $"Viaje {quote.Customer?.FullName ?? "Cliente"} - {validVersionForBooking.ProductType}",
            Status = "Open",
            CreatedAt = DateTime.UtcNow
        };
        
        _dbContext.TravelFiles.Add(travelFile);
        await _dbContext.SaveChangesAsync(cancellationToken); // Save to get Id
        
        // 2. Create Reservation
        var reservation = new Reservation
        {
            ReferenceCode = $"RES-{DateTime.UtcNow.Ticks.ToString()[^6..]}",
            Status = ReservationStatuses.Confirmed,
            ProductType = validVersionForBooking.ProductType,
            DepartureDate = validVersionForBooking.ValidUntil ?? DateTime.UtcNow.AddDays(30), // Fallback if null, usually came from Query
            ReturnDate = null,
            BasePrice = validVersionForBooking.TotalAmount, // Simplification: Base = Total
            Commission = 0,
            TotalAmount = validVersionForBooking.TotalAmount,
            SupplierName = "TBD",
            CreatedAt = DateTime.UtcNow,
            CustomerId = quote.CustomerId,
            TravelFileId = travelFile.Id
        };
        
        _dbContext.Reservations.Add(reservation);
        
        // 3. Update Quote Status
        quote.Status = QuoteStatuses.Booked;
        
        await _dbContext.SaveChangesAsync(cancellationToken);
        
        return Ok(new { Message = "Reserva creada exitosamente", TravelFileId = travelFile.Id, ReservationId = reservation.Id });
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
