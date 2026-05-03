using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class QuoteService : IQuoteService
{
    private readonly AppDbContext _db;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public QuoteService(AppDbContext db, IEntityReferenceResolver entityReferenceResolver)
    {
        _db = db;
        _entityReferenceResolver = entityReferenceResolver;
    }

    public async Task<List<QuoteSummaryDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var quotes = await GetAllEntitiesAsync(cancellationToken);
        return quotes.Select(MapQuoteSummary).ToList();
    }

    public async Task<QuoteDetailDto?> GetByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var quote = await GetByIdAsync(id, cancellationToken);
        return quote == null ? null : MapQuoteDetail(quote);
    }

    public async Task<QuoteDetailDto> CreateAsync(UpsertQuoteRequest request, CancellationToken cancellationToken)
    {
        var quote = await CreateAsync(await BuildQuoteEntityAsync(request, cancellationToken), cancellationToken);
        var detail = await GetByIdAsync(quote.Id, cancellationToken) ?? quote;
        return MapQuoteDetail(detail);
    }

    public async Task<QuoteDetailDto> UpdateAsync(string publicIdOrLegacyId, UpsertQuoteRequest updated, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var quote = await UpdateAsync(id, await BuildQuoteEntityAsync(updated, cancellationToken), cancellationToken);
        var detail = await GetByIdAsync(quote.Id, cancellationToken) ?? quote;
        return MapQuoteDetail(detail);
    }

    public async Task DeleteAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        await DeleteAsync(id, cancellationToken);
    }

    public async Task<QuoteDetailDto> AddItemAsync(string quotePublicIdOrLegacyId, UpsertQuoteItemRequest item, CancellationToken cancellationToken)
    {
        var quoteId = await ResolveRequiredIdAsync<Quote>(quotePublicIdOrLegacyId, cancellationToken);
        var quote = await AddItemAsync(quoteId, await BuildQuoteItemEntityAsync(item, cancellationToken), cancellationToken);
        return MapQuoteDetail(quote);
    }

    public async Task RemoveItemAsync(string quotePublicIdOrLegacyId, string itemPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var quoteId = await ResolveRequiredIdAsync<Quote>(quotePublicIdOrLegacyId, cancellationToken);
        var itemId = await ResolveRequiredIdAsync<QuoteItem>(itemPublicIdOrLegacyId, cancellationToken);
        await RemoveItemAsync(quoteId, itemId, cancellationToken);
    }

    public async Task<QuoteDetailDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var quote = await UpdateStatusAsync(id, status, cancellationToken);
        var detail = await GetByIdAsync(quote.Id, cancellationToken) ?? quote;
        return MapQuoteDetail(detail);
    }

    public async Task<QuoteConversionResultDto> ConvertToFileAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var reservaId = await ConvertToFileAsync(id, cancellationToken);
        var reservaPublicId = await _entityReferenceResolver.ResolvePublicIdAsync<Reserva>(reservaId, cancellationToken);

        return new QuoteConversionResultDto
        {
            ReservaPublicId = reservaPublicId ?? Guid.Empty
        };
    }

    public async Task<List<Quote>> GetAllEntitiesAsync(CancellationToken cancellationToken)
    {
        return await _db.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Lead)
            .Include(q => q.Items)
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Quote?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _db.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Lead)
            .Include(q => q.Items).ThenInclude(i => i.Supplier).Include(q => q.Items).ThenInclude(i => i.Rate)
            .Include(q => q.ConvertedReserva)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);
    }

    public async Task<Quote> CreateAsync(Quote quote, CancellationToken cancellationToken)
    {
        quote.CustomerId = await ResolveCustomerFromLeadAsync(quote.CustomerId, quote.LeadId, cancellationToken);

        // Auto-generate quote number
        var count = await _db.Quotes.CountAsync(cancellationToken);
        quote.QuoteNumber = $"COT-{(count + 1).ToString().PadLeft(5, '0')}";
        quote.CreatedAt = DateTime.UtcNow;
        quote.ValidUntil = ToUtc(quote.ValidUntil) ?? DateTime.UtcNow.AddDays(15);
        quote.TravelStartDate = ToUtc(quote.TravelStartDate);
        quote.TravelEndDate = ToUtc(quote.TravelEndDate);
        quote.AcceptedAt = ToUtc(quote.AcceptedAt);

        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(cancellationToken);
        await RecalculateTotalsAsync(quote.Id, cancellationToken);
        return quote;
    }

    private static DateTime? ToUtc(DateTime? dt)
    {
        if (!dt.HasValue) return null;
        return dt.Value.Kind == DateTimeKind.Unspecified 
            ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) 
            : dt.Value.ToUniversalTime();
    }

    public async Task<Quote> UpdateAsync(int id, Quote updated, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new KeyNotFoundException($"CotizaciÃ³n {id} no encontrada.");

        quote.Title = updated.Title;
        quote.Description = updated.Description;
        quote.CustomerId = await ResolveCustomerFromLeadAsync(updated.CustomerId, quote.LeadId, cancellationToken);
        quote.ValidUntil = ToUtc(updated.ValidUntil);
        quote.TravelStartDate = ToUtc(updated.TravelStartDate);
        quote.TravelEndDate = ToUtc(updated.TravelEndDate);
        quote.Destination = updated.Destination;
        quote.Adults = updated.Adults;
        quote.Children = updated.Children;
        quote.Notes = updated.Notes;

        await _db.SaveChangesAsync(cancellationToken);
        return quote;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes.Include(q => q.Items)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"CotizaciÃ³n {id} no encontrada.");

        _db.QuoteItems.RemoveRange(quote.Items);
        _db.Quotes.Remove(quote);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Quote> AddItemAsync(int quoteId, QuoteItem item, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes.FindAsync(new object[] { quoteId }, cancellationToken)
            ?? throw new KeyNotFoundException($"CotizaciÃ³n {quoteId} no encontrada.");

        item.QuoteId = quoteId;
        item.CreatedAt = DateTime.UtcNow;

                // Autocompletar desde Tarifario si se provee RateId
        if (item.RateId.HasValue)
        {
            var rate = await _db.Rates.AsNoTracking().FirstOrDefaultAsync(r => r.Id == item.RateId, cancellationToken);
            if (rate != null)
            {
                item.UnitCost = rate.NetCost;
                item.UnitPrice = rate.SalePrice;
                item.Description = rate.ProductName;
                item.ServiceType = rate.ServiceType;
                item.SupplierId = rate.SupplierId;
                if (rate.NetCost > 0)
                {
                    item.MarkupPercent = ((rate.SalePrice / rate.NetCost) - 1) * 100;
                }
            }
        }

        // Auto-calculate markup
        if (item.UnitCost > 0 && item.MarkupPercent > 0 && item.UnitPrice == 0)
        {
            item.UnitPrice = item.UnitCost * (1 + item.MarkupPercent / 100);
        }

        _db.QuoteItems.Add(item);
        await _db.SaveChangesAsync(cancellationToken);
        await RecalculateTotalsAsync(quoteId, cancellationToken);

        return (await GetByIdAsync(quoteId, cancellationToken))!;
    }

    public async Task RemoveItemAsync(int quoteId, int itemId, CancellationToken cancellationToken)
    {
        var item = await _db.QuoteItems.FirstOrDefaultAsync(
            i => i.Id == itemId && i.QuoteId == quoteId, cancellationToken)
            ?? throw new KeyNotFoundException($"Item {itemId} no encontrado.");

        _db.QuoteItems.Remove(item);
        await _db.SaveChangesAsync(cancellationToken);
        await RecalculateTotalsAsync(quoteId, cancellationToken);
    }

    public async Task<Quote> UpdateStatusAsync(int id, string status, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new KeyNotFoundException($"CotizaciÃ³n {id} no encontrada.");

        quote.Status = status;
        if (quote.LeadId.HasValue && (status == QuoteStatus.Sent || status == QuoteStatus.Accepted))
        {
            var lead = await _db.Leads.FindAsync(new object[] { quote.LeadId.Value }, cancellationToken);
            if (lead != null && lead.Status != LeadStatus.Won && lead.Status != LeadStatus.Lost)
            {
                lead.Status = LeadStatus.Quoted;
                lead.ClosedAt = null;
            }
        }

        if (status == QuoteStatus.Accepted)
            quote.AcceptedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return quote;
    }

    public async Task<int> ConvertToFileAsync(int quoteId, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes
            .Include(q => q.Items)
            .FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken)
            ?? throw new KeyNotFoundException($"Cotización {quoteId} no encontrada.");

        if (quote.ConvertedReservaId.HasValue)
            throw new InvalidOperationException("Esta cotización ya fue convertida a reserva.");

        // Create Reserva from quote
        quote.CustomerId = await ResolveCustomerFromLeadAsync(quote.CustomerId, quote.LeadId, cancellationToken);

        var fileCount = await _db.Reservas.CountAsync(cancellationToken);
        var file = new Reserva
        {
            NumeroReserva = $"RES-{(fileCount + 1).ToString().PadLeft(5, '0')}",
            Name = quote.Title,
            Description = quote.Description,
            Status = EstadoReserva.Reserved,
            PayerId = quote.CustomerId,
            SourceLeadId = quote.LeadId,
            SourceQuoteId = quote.Id,
            StartDate = quote.TravelStartDate,
            EndDate = quote.TravelEndDate,
            // Even though generic File Service calculates dynamically, we still populate DB bounds
            TotalCost = quote.TotalCost,
            TotalSale = quote.TotalSale,
            Balance = quote.TotalSale,
            AdultCount = quote.Adults,
            ChildCount = quote.Children,
            CreatedAt = DateTime.UtcNow
        };

        _db.Reservas.Add(file);
        await _db.SaveChangesAsync(cancellationToken);

        // Migrate Quote Items to File Services (Smart Conversion)
        foreach (var item in quote.Items)
        {
            // Resolve specifics from Rate if available
            var rate = item.RateId.HasValue 
                ? await _db.Set<Rate>().FindAsync(new object[] { item.RateId.Value }, cancellationToken) 
                : null;

            bool specializedCreated = false;

            // Normalize ServiceType for switching
            var sType = (item.ServiceType ?? "").ToLower().Trim();

            if (sType == "hotel" || sType == "alojamiento")
            {
                var hotel = new HotelBooking
                {
                    ReservaId = file.Id,
                    SupplierId = item.SupplierId ?? (rate?.SupplierId ?? 0),
                    RateId = item.RateId,
                    HotelName = rate?.HotelName ?? item.Description,
                    City = rate?.City ?? string.Empty,
                    CheckIn = quote.TravelStartDate ?? DateTime.UtcNow,
                    CheckOut = quote.TravelEndDate ?? DateTime.UtcNow.AddDays(1),
                    RoomType = rate?.RoomType ?? "Standard",
                    MealPlan = rate?.MealPlan ?? "RO",
                    Adults = quote.Adults > 0 ? quote.Adults : 2,
                    Children = quote.Children,
                    Rooms = item.Quantity,
                    Status = "Solicitado",
                    NetCost = item.TotalCost,
                    SalePrice = item.TotalPrice,
                    Commission = item.TotalPrice - item.TotalCost,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Set<HotelBooking>().Add(hotel);
                specializedCreated = true;
            }
            else if (sType == "vuelo" || sType == "aereo" || sType == "flight")
            {
                var flight = new FlightSegment
                {
                    ReservaId = file.Id,
                    SupplierId = item.SupplierId ?? (rate?.SupplierId ?? 0),
                    RateId = item.RateId,
                    AirlineCode = rate?.AirlineCode ?? "YY",
                    AirlineName = rate?.Airline ?? "A definir",
                    FlightNumber = "TBD",
                    Origin = rate?.Origin ?? "TBD",
                    Destination = rate?.Destination ?? "TBD",
                    DepartureTime = quote.TravelStartDate ?? DateTime.UtcNow,
                    ArrivalTime = quote.TravelStartDate?.AddHours(2) ?? DateTime.UtcNow.AddHours(2),
                    CabinClass = rate?.CabinClass ?? "Economy",
                    Status = "HK",
                    NetCost = item.TotalCost,
                    SalePrice = item.TotalPrice,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Set<FlightSegment>().Add(flight);
                specializedCreated = true;
            }
            else if (sType == "traslado" || sType == "transfer")
            {
                var transfer = new TransferBooking
                {
                    ReservaId = file.Id,
                    SupplierId = item.SupplierId ?? (rate?.SupplierId ?? 0),
                    RateId = item.RateId,
                    PickupLocation = rate?.PickupLocation ?? "TBD",
                    DropoffLocation = rate?.DropoffLocation ?? "TBD",
                    PickupDateTime = quote.TravelStartDate ?? DateTime.UtcNow,
                    VehicleType = rate?.VehicleType ?? "Private",
                    Passengers = 1,
                    Status = "Solicitado",
                    NetCost = item.TotalCost,
                    SalePrice = item.TotalPrice,
                    Commission = item.TotalPrice - item.TotalCost,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Set<TransferBooking>().Add(transfer);
                specializedCreated = true;
            }
            else if (sType == "paquete" || sType == "package")
            {
                var package = new PackageBooking
                {
                    ReservaId = file.Id,
                    SupplierId = item.SupplierId ?? (rate?.SupplierId ?? 0),
                    RateId = item.RateId,
                    PackageName = rate?.ProductName ?? item.Description,
                    Destination = rate?.Destination ?? "Multi",
                    StartDate = quote.TravelStartDate ?? DateTime.UtcNow,
                    EndDate = quote.TravelEndDate ?? DateTime.UtcNow.AddDays(7),
                    Status = "Solicitado",
                    NetCost = item.TotalCost,
                    SalePrice = item.TotalPrice,
                    Commission = item.TotalPrice - item.TotalCost,
                    Adults = quote.Adults > 0 ? quote.Adults : 2,
                    Children = quote.Children,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Set<PackageBooking>().Add(package);
                specializedCreated = true;
            }

            // Fallback to generic service if no specialized type or extra customization needed
            if (!specializedCreated)
            {
                var res = new ServicioReserva
                {
                    ReservaId = file.Id,
                    CustomerId = quote.CustomerId,
                    SupplierId = item.SupplierId,
                    RateId = item.RateId,
                    ServiceType = string.IsNullOrWhiteSpace(item.ServiceType) ? ServiceTypes.Other : item.ServiceType,
                    ProductType = string.IsNullOrWhiteSpace(item.ServiceType) ? ServiceTypes.Other : item.ServiceType,
                    Description = $"{item.Quantity}x {item.Description}",
                    ConfirmationNumber = "PENDIENTE",
                    Status = ReservationStatuses.Requested,
                    DepartureDate = quote.TravelStartDate ?? DateTime.UtcNow,
                    ReturnDate = quote.TravelEndDate,
                    NetCost = item.TotalCost,
                    SalePrice = item.TotalPrice,
                    Commission = item.TotalPrice - item.TotalCost,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Servicios.Add(res);
            }
        }

        // Link quote to the file
        quote.ConvertedReservaId = file.Id;
        quote.Status = QuoteStatus.Accepted;
        quote.AcceptedAt = DateTime.UtcNow;
        if (quote.LeadId.HasValue)
        {
            var lead = await _db.Leads.FindAsync(new object[] { quote.LeadId.Value }, cancellationToken);
            if (lead != null && lead.Status != LeadStatus.Lost)
            {
                lead.Status = LeadStatus.Won;
                lead.ClosedAt = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);

        return file.Id;
    }

    public async Task RecalculateTotalsAsync(int quoteId, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes.Include(q => q.Items)
            .FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken);
        if (quote == null) return;

        quote.TotalCost = quote.Items.Sum(i => i.UnitCost * i.Quantity);
        quote.TotalSale = quote.Items.Sum(i => i.UnitPrice * i.Quantity);
        quote.GrossMargin = quote.TotalSale - quote.TotalCost;

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> ResolveRequiredIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken)
        where TEntity : class, IHasPublicId
    {
        var resolved = await _db.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado.");
    }

    private async Task<Quote> BuildQuoteEntityAsync(UpsertQuoteRequest request, CancellationToken cancellationToken)
    {
        return new Quote
        {
            Title = request.Title,
            Description = request.Description,
            CustomerId = await ResolveOptionalIdAsync<Customer>(request.CustomerPublicId, cancellationToken),
            LeadId = await ResolveOptionalIdAsync<Lead>(request.LeadPublicId, cancellationToken),
            ValidUntil = request.ValidUntil,
            TravelStartDate = request.TravelStartDate,
            TravelEndDate = request.TravelEndDate,
            Destination = request.Destination,
            Adults = request.Adults,
            Children = request.Children,
            Notes = request.Notes
        };
    }

    private async Task<QuoteItem> BuildQuoteItemEntityAsync(UpsertQuoteItemRequest request, CancellationToken cancellationToken)
    {
        return new QuoteItem
        {
            ServiceType = request.ServiceType,
            Description = request.Description,
            SupplierId = await ResolveOptionalIdAsync<Supplier>(request.SupplierPublicId, cancellationToken),
            RateId = await ResolveOptionalIdAsync<Rate>(request.RateId, cancellationToken),
            Quantity = request.Quantity,
            UnitCost = request.UnitCost,
            UnitPrice = request.UnitPrice,
            MarkupPercent = request.MarkupPercent,
            Notes = request.Notes
        };
    }

    private async Task<int?> ResolveOptionalIdAsync<TEntity>(string? publicIdOrLegacyId, CancellationToken cancellationToken)
        where TEntity : class, IHasPublicId
    {
        if (string.IsNullOrWhiteSpace(publicIdOrLegacyId))
        {
            return null;
        }

        return await ResolveRequiredIdAsync<TEntity>(publicIdOrLegacyId, cancellationToken);
    }

    private static QuoteSummaryDto MapQuoteSummary(Quote quote)
    {
        return new QuoteSummaryDto
        {
            PublicId = quote.PublicId,
            QuoteNumber = quote.QuoteNumber,
            Title = quote.Title,
            Description = quote.Description,
            Status = quote.Status,
            CustomerPublicId = quote.Customer?.PublicId,
            CustomerName = quote.Customer?.FullName,
            LeadPublicId = quote.Lead?.PublicId,
            LeadName = quote.Lead?.FullName,
            ConvertedReservaPublicId = quote.ConvertedReserva?.PublicId,
            ConvertedReservaNumeroReserva = quote.ConvertedReserva?.NumeroReserva,
            CreatedAt = quote.CreatedAt,
            ValidUntil = quote.ValidUntil,
            AcceptedAt = quote.AcceptedAt,
            TravelStartDate = quote.TravelStartDate,
            TravelEndDate = quote.TravelEndDate,
            Destination = quote.Destination,
            Adults = quote.Adults,
            Children = quote.Children,
            TotalCost = quote.TotalCost,
            TotalSale = quote.TotalSale,
            GrossMargin = quote.GrossMargin,
            Notes = quote.Notes
        };
    }

    private static QuoteDetailDto MapQuoteDetail(Quote quote)
    {
        var summary = MapQuoteSummary(quote);
        return new QuoteDetailDto
        {
            PublicId = summary.PublicId,
            QuoteNumber = summary.QuoteNumber,
            Title = summary.Title,
            Description = summary.Description,
            Status = summary.Status,
            CustomerPublicId = summary.CustomerPublicId,
            CustomerName = summary.CustomerName,
            LeadPublicId = summary.LeadPublicId,
            LeadName = summary.LeadName,
            ConvertedReservaPublicId = summary.ConvertedReservaPublicId,
            ConvertedReservaNumeroReserva = summary.ConvertedReservaNumeroReserva,
            CreatedAt = summary.CreatedAt,
            ValidUntil = summary.ValidUntil,
            AcceptedAt = summary.AcceptedAt,
            TravelStartDate = summary.TravelStartDate,
            TravelEndDate = summary.TravelEndDate,
            Destination = summary.Destination,
            Adults = summary.Adults,
            Children = summary.Children,
            TotalCost = summary.TotalCost,
            TotalSale = summary.TotalSale,
            GrossMargin = summary.GrossMargin,
            Notes = summary.Notes,
            Customer = quote.Customer == null ? null : new CustomerReferenceDto
            {
                PublicId = quote.Customer.PublicId,
                FullName = quote.Customer.FullName
            },
            Lead = quote.Lead == null ? null : new LeadReferenceDto
            {
                PublicId = quote.Lead.PublicId,
                FullName = quote.Lead.FullName
            },
            ConvertedReserva = quote.ConvertedReserva == null ? null : new ReservaReferenceDto
            {
                PublicId = quote.ConvertedReserva.PublicId,
                NumeroReserva = quote.ConvertedReserva.NumeroReserva,
                Name = quote.ConvertedReserva.Name
            },
            Items = quote.Items
                .OrderByDescending(item => item.CreatedAt)
                .Select(item => new QuoteItemDto
                {
                    PublicId = item.PublicId,
                    ServiceType = item.ServiceType,
                    Description = item.Description,
                    SupplierPublicId = item.Supplier?.PublicId,
                    SupplierName = item.Supplier?.Name,
                    RatePublicId = item.Rate?.PublicId,
                    Quantity = item.Quantity,
                    UnitCost = item.UnitCost,
                    UnitPrice = item.UnitPrice,
                    MarkupPercent = item.MarkupPercent,
                    TotalCost = item.TotalCost,
                    TotalPrice = item.TotalPrice,
                    Notes = item.Notes,
                    CreatedAt = item.CreatedAt
                })
                .ToList()
        };
    }

    private async Task<int?> ResolveCustomerFromLeadAsync(int? customerId, int? leadId, CancellationToken cancellationToken)
    {
        if (customerId.HasValue || !leadId.HasValue)
        {
            return customerId;
        }

        var convertedCustomerId = await _db.Leads
            .Where(lead => lead.Id == leadId.Value)
            .Select(lead => lead.ConvertedCustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        return convertedCustomerId ?? customerId;
    }
}



