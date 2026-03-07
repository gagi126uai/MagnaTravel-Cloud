using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class QuoteService : IQuoteService
{
    private readonly AppDbContext _db;

    public QuoteService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Quote>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await _db.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Items)
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Quote?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _db.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Items).ThenInclude(i => i.Supplier)
            .Include(q => q.ConvertedFile)
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);
    }

    public async Task<Quote> CreateAsync(Quote quote, CancellationToken cancellationToken)
    {
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
            ?? throw new KeyNotFoundException($"Cotización {id} no encontrada.");

        quote.Title = updated.Title;
        quote.Description = updated.Description;
        quote.CustomerId = updated.CustomerId;
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
            ?? throw new KeyNotFoundException($"Cotización {id} no encontrada.");

        _db.QuoteItems.RemoveRange(quote.Items);
        _db.Quotes.Remove(quote);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Quote> AddItemAsync(int quoteId, QuoteItem item, CancellationToken cancellationToken)
    {
        var quote = await _db.Quotes.FindAsync(new object[] { quoteId }, cancellationToken)
            ?? throw new KeyNotFoundException($"Cotización {quoteId} no encontrada.");

        item.QuoteId = quoteId;
        item.CreatedAt = DateTime.UtcNow;

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
            ?? throw new KeyNotFoundException($"Cotización {id} no encontrada.");

        quote.Status = status;
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

        if (quote.ConvertedFileId.HasValue)
            throw new InvalidOperationException("Esta cotización ya fue convertida a expediente.");

        // Create TravelFile from quote
        var fileCount = await _db.TravelFiles.CountAsync(cancellationToken);
        var file = new TravelFile
        {
            FileNumber = $"EXP-{(fileCount + 1).ToString().PadLeft(5, '0')}",
            Name = quote.Title,
            Description = quote.Description,
            Status = FileStatus.Budget,
            PayerId = quote.CustomerId,
            StartDate = quote.TravelStartDate,
            EndDate = quote.TravelEndDate,
            TotalCost = quote.TotalCost,
            TotalSale = quote.TotalSale,
            Balance = quote.TotalSale,
            CreatedAt = DateTime.UtcNow
        };

        _db.TravelFiles.Add(file);
        await _db.SaveChangesAsync(cancellationToken);

        // Link quote to the file
        quote.ConvertedFileId = file.Id;
        quote.Status = QuoteStatus.Accepted;
        quote.AcceptedAt = DateTime.UtcNow;
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
}
