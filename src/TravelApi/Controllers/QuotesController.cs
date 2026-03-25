using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/quotes")]
[Authorize]
public class QuotesController : ControllerBase
{
    private readonly IQuoteService _quoteService;
    private readonly EntityReferenceResolver _entityReferenceResolver;

    public QuotesController(IQuoteService quoteService, EntityReferenceResolver entityReferenceResolver)
    {
        _quoteService = quoteService;
        _entityReferenceResolver = entityReferenceResolver;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<QuoteSummaryDto>>> GetAll(CancellationToken cancellationToken)
    {
        var quotes = await _quoteService.GetAllAsync(cancellationToken);
        return Ok(quotes.Select(MapQuoteSummary));
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<QuoteDetailDto>> GetById(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var quote = await _quoteService.GetByIdAsync(id, cancellationToken);
        if (quote == null) return NotFound();
        return Ok(MapQuoteDetail(quote));
    }

    [HttpPost]
    public async Task<ActionResult<QuoteDetailDto>> Create([FromBody] UpsertQuoteRequest request, CancellationToken cancellationToken)
    {
        var quote = await BuildQuoteEntityAsync(request, cancellationToken);
        var created = await _quoteService.CreateAsync(quote, cancellationToken);
        var response = await _quoteService.GetByIdAsync(created.Id, cancellationToken) ?? created;

        return CreatedAtAction(
            nameof(GetById),
            new { publicIdOrLegacyId = response.PublicId },
            MapQuoteDetail(response));
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<QuoteDetailDto>> Update(
        string publicIdOrLegacyId,
        [FromBody] UpsertQuoteRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var quote = await BuildQuoteEntityAsync(request, cancellationToken);
        var updated = await _quoteService.UpdateAsync(id, quote, cancellationToken);
        var response = await _quoteService.GetByIdAsync(updated.Id, cancellationToken) ?? updated;
        return Ok(MapQuoteDetail(response));
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<ActionResult> Delete(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        await _quoteService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("{publicIdOrLegacyId}/items")]
    public async Task<ActionResult<QuoteDetailDto>> AddItem(
        string publicIdOrLegacyId,
        [FromBody] UpsertQuoteItemRequest request,
        CancellationToken cancellationToken)
    {
        var quoteId = await _entityReferenceResolver.ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var item = await BuildQuoteItemEntityAsync(request, cancellationToken);
        var quote = await _quoteService.AddItemAsync(quoteId, item, cancellationToken);
        return Ok(MapQuoteDetail(quote));
    }

    [HttpDelete("{quotePublicIdOrLegacyId}/items/{itemPublicIdOrLegacyId}")]
    public async Task<ActionResult> RemoveItem(
        string quotePublicIdOrLegacyId,
        string itemPublicIdOrLegacyId,
        CancellationToken cancellationToken)
    {
        var quoteId = await _entityReferenceResolver.ResolveRequiredIdAsync<Quote>(quotePublicIdOrLegacyId, cancellationToken);
        var itemId = await _entityReferenceResolver.ResolveRequiredIdAsync<QuoteItem>(itemPublicIdOrLegacyId, cancellationToken);
        await _quoteService.RemoveItemAsync(quoteId, itemId, cancellationToken);
        return NoContent();
    }

    [HttpPatch("{publicIdOrLegacyId}/status")]
    public async Task<ActionResult<QuoteDetailDto>> UpdateStatus(
        string publicIdOrLegacyId,
        [FromBody] StatusUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var quote = await _quoteService.UpdateStatusAsync(id, request.Status, cancellationToken);
        var response = await _quoteService.GetByIdAsync(quote.Id, cancellationToken) ?? quote;
        return Ok(MapQuoteDetail(response));
    }

    [HttpPost("{publicIdOrLegacyId}/convert")]
    public async Task<ActionResult> ConvertToFile(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await _entityReferenceResolver.ResolveRequiredIdAsync<Quote>(publicIdOrLegacyId, cancellationToken);
        var reservaId = await _quoteService.ConvertToFileAsync(id, cancellationToken);
            var reservaPublicId = await _entityReferenceResolver.ResolvePublicIdAsync<Reserva>(reservaId, cancellationToken);

        if (!reservaPublicId.HasValue)
            return NotFound();

        return Ok(new
        {
            reservaPublicId = reservaPublicId.Value
        });
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
            return null;

        return await _entityReferenceResolver.ResolveRequiredIdAsync<TEntity>(publicIdOrLegacyId, cancellationToken);
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
            Customer = quote.Customer == null
                ? null
                : new CustomerReferenceDto
                {
                    PublicId = quote.Customer.PublicId,
                    FullName = quote.Customer.FullName
                },
            Lead = quote.Lead == null
                ? null
                : new LeadReferenceDto
                {
                    PublicId = quote.Lead.PublicId,
                    FullName = quote.Lead.FullName
                },
            ConvertedReserva = quote.ConvertedReserva == null
                ? null
                : new ReservaReferenceDto
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
}

public record StatusUpdateRequest(string Status);
