using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Contracts.Leads;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class LeadService : ILeadService
{
    private readonly AppDbContext _db;
    private readonly IEntityReferenceResolver _entityReferenceResolver;

    public LeadService(AppDbContext db, IEntityReferenceResolver entityReferenceResolver)
    {
        _db = db;
        _entityReferenceResolver = entityReferenceResolver;
    }

    public async Task<LeadDetailDto?> GetByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var lead = await GetByIdAsync(id, cancellationToken);
        return lead == null ? null : MapLeadDetail(lead);
    }

    public async Task<LeadDetailDto> CreateAsync(LeadUpsertRequest request, CancellationToken cancellationToken)
    {
        var lead = await CreateAsync(MapLeadFromRequest(request), cancellationToken);
        var detail = await GetByIdAsync(lead.Id, cancellationToken) ?? lead;
        return MapLeadDetail(detail);
    }

    public async Task<LeadDetailDto> UpdateAsync(string publicIdOrLegacyId, LeadUpsertRequest updated, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var lead = await UpdateAsync(id, MapLeadFromRequest(updated), cancellationToken);
        var detail = await GetByIdAsync(lead.Id, cancellationToken) ?? lead;
        return MapLeadDetail(detail);
    }

    public async Task DeleteAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        await DeleteAsync(id, cancellationToken);
    }

    public async Task<LeadDetailDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var lead = await UpdateStatusAsync(id, status, cancellationToken);
        var detail = await GetByIdAsync(lead.Id, cancellationToken) ?? lead;
        return MapLeadDetail(detail);
    }

    public async Task<LeadActivityDto> AddActivityAsync(string publicIdOrLegacyId, LeadActivityUpsertRequest activity, string? createdBy, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var created = await AddActivityAsync(id, new LeadActivity
        {
            Type = activity.Type,
            Description = activity.Description,
            CreatedBy = createdBy
        }, cancellationToken);

        return MapLeadActivity(created);
    }

    public async Task<LeadConversionResultDto> ConvertToCustomerAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var customerId = await ConvertToCustomerAsync(id, cancellationToken);
        var customerPublicId = await _entityReferenceResolver.ResolvePublicIdAsync<Customer>(customerId, cancellationToken);

        return new LeadConversionResultDto
        {
            CustomerPublicId = customerPublicId ?? Guid.Empty
        };
    }

    public async Task<QuoteDraftResultDto> CreateQuoteDraftAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        var quote = await CreateQuoteDraftAsync(id, cancellationToken);
        var leadPublicId = await _entityReferenceResolver.ResolvePublicIdAsync<Lead>(id, cancellationToken);

        return new QuoteDraftResultDto
        {
            QuotePublicId = quote.PublicId,
            QuoteNumber = quote.QuoteNumber,
            CustomerPublicId = quote.Customer?.PublicId,
            LeadPublicId = leadPublicId ?? Guid.Empty
        };
    }

    public async Task<LeadJourneyDto> GetJourneyAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Lead>(publicIdOrLegacyId, cancellationToken);
        return await GetJourneyAsync(id, cancellationToken);
    }

    public async Task<PagedResponse<LeadSummaryDto>> GetAllAsync(LeadListQuery query, CancellationToken cancellationToken)
    {
        var leads = _db.Leads.AsNoTracking();
        leads = ApplyLeadView(leads, query.View);
        leads = ApplyLeadStatus(leads, query.Status);
        leads = ApplyLeadSource(leads, query.Source);
        leads = ApplyLeadSearch(leads, query.Search);
        leads = ApplyLeadOrdering(leads, query);

        return await leads
            .Select(lead => new LeadSummaryDto
            {
                PublicId = lead.PublicId,
                FullName = lead.FullName,
                Email = lead.Email,
                Phone = lead.Phone,
                Status = lead.Status,
                Source = lead.Source,
                InterestedIn = lead.InterestedIn,
                TravelDates = lead.TravelDates,
                Travelers = lead.Travelers,
                EstimatedBudget = lead.EstimatedBudget,
                Notes = lead.Notes,
                AssignedToUserId = lead.AssignedToUserId,
                AssignedToName = lead.AssignedToName,
                NextFollowUp = lead.NextFollowUp,
                CreatedAt = lead.CreatedAt,
                ClosedAt = lead.ClosedAt,
                ConvertedCustomerPublicId = lead.ConvertedCustomer != null ? lead.ConvertedCustomer.PublicId : null,
                ConvertedCustomerName = lead.ConvertedCustomer != null ? lead.ConvertedCustomer.FullName : null,
                ActivitiesCount = lead.Activities.Count,
                LastActivity = lead.Activities
                    .OrderByDescending(activity => activity.CreatedAt)
                    .Select(activity => activity.Description)
                    .FirstOrDefault()
            })
            .ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<Lead?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        return await _db.Leads
            .Include(l => l.Activities.OrderByDescending(a => a.CreatedAt))
            .Include(l => l.ConvertedCustomer)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    public async Task<Lead> CreateAsync(Lead lead, CancellationToken cancellationToken)
    {
        lead.CreatedAt = DateTime.UtcNow;
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync(cancellationToken);
        return lead;
    }

    public async Task<Lead> UpdateAsync(int id, Lead updated, CancellationToken cancellationToken)
    {
        var lead = await _db.Leads.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new KeyNotFoundException($"Lead {id} no encontrado.");

        lead.FullName = updated.FullName;
        lead.Email = updated.Email;
        lead.Phone = updated.Phone;
        lead.Source = updated.Source;
        lead.InterestedIn = updated.InterestedIn;
        lead.TravelDates = updated.TravelDates;
        lead.Travelers = updated.Travelers;
        lead.EstimatedBudget = updated.EstimatedBudget;
        lead.Notes = updated.Notes;
        lead.AssignedToUserId = updated.AssignedToUserId;
        lead.AssignedToName = updated.AssignedToName;
        lead.NextFollowUp = updated.NextFollowUp.HasValue
            ? DateTime.SpecifyKind(updated.NextFollowUp.Value, DateTimeKind.Utc) : null;

        await _db.SaveChangesAsync(cancellationToken);
        return lead;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        var lead = await _db.Leads.Include(l => l.Activities)
            .FirstOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Lead {id} no encontrado.");

        _db.LeadActivities.RemoveRange(lead.Activities);
        _db.Leads.Remove(lead);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Lead> UpdateStatusAsync(int id, string status, CancellationToken cancellationToken)
    {
        var lead = await _db.Leads.FindAsync(new object[] { id }, cancellationToken)
            ?? throw new KeyNotFoundException($"Lead {id} no encontrado.");

        lead.Status = status;
        if (status == LeadStatus.Won || status == LeadStatus.Lost)
        {
            lead.ClosedAt = DateTime.UtcNow;
        }
        else
        {
            lead.ClosedAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return lead;
    }

    public async Task<LeadActivity> AddActivityAsync(int leadId, LeadActivity activity, CancellationToken cancellationToken)
    {
        var lead = await _db.Leads.FindAsync(new object[] { leadId }, cancellationToken)
            ?? throw new KeyNotFoundException($"Lead {leadId} no encontrado.");

        activity.LeadId = leadId;
        activity.CreatedAt = DateTime.UtcNow;

        _db.LeadActivities.Add(activity);
        await _db.SaveChangesAsync(cancellationToken);
        return activity;
    }

    public async Task<int> ConvertToCustomerAsync(int leadId, CancellationToken cancellationToken)
    {
        var lead = await _db.Leads.FindAsync(new object[] { leadId }, cancellationToken)
            ?? throw new KeyNotFoundException($"Lead {leadId} no encontrado.");

        if (lead.ConvertedCustomerId.HasValue)
            throw new InvalidOperationException("Este lead ya fue convertido a cliente.");

        var customer = new Customer
        {
            FullName = lead.FullName,
            Email = lead.Email,
            Phone = lead.Phone,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(cancellationToken);

        lead.ConvertedCustomerId = customer.Id;
        await _db.SaveChangesAsync(cancellationToken);

        return customer.Id;
    }

    public async Task<Quote> CreateQuoteDraftAsync(int leadId, CancellationToken cancellationToken)
    {
        var lead = await _db.Leads
            .FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken)
            ?? throw new KeyNotFoundException($"Lead {leadId} no encontrado.");

        var existingDraft = await _db.Quotes
            .Where(q => q.LeadId == leadId && q.ConvertedReservaId == null && q.Status == QuoteStatus.Draft)
            .OrderByDescending(q => q.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingDraft != null)
        {
            if (!existingDraft.CustomerId.HasValue && lead.ConvertedCustomerId.HasValue)
            {
                existingDraft.CustomerId = lead.ConvertedCustomerId;
                await _db.SaveChangesAsync(cancellationToken);
            }

            return existingDraft;
        }

        var count = await _db.Quotes.CountAsync(cancellationToken);
        var quote = new Quote
        {
            QuoteNumber = $"COT-{(count + 1).ToString().PadLeft(5, '0')}",
            Title = !string.IsNullOrWhiteSpace(lead.InterestedIn)
                ? $"Cotizacion para {lead.FullName} - {lead.InterestedIn}"
                : $"Cotizacion para {lead.FullName}",
            Description = lead.Notes,
            Status = QuoteStatus.Draft,
            CustomerId = lead.ConvertedCustomerId,
            LeadId = lead.Id,
            Destination = lead.InterestedIn,
            Notes = $"Cotizacion creada desde posible cliente {lead.FullName}",
            CreatedAt = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(15)
        };

        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync(cancellationToken);
        return quote;
    }

    public async Task<LeadJourneyDto> GetJourneyAsync(int leadId, CancellationToken cancellationToken)
    {
        var lead = await _db.Leads
            .Include(l => l.ConvertedCustomer)
            .FirstOrDefaultAsync(l => l.Id == leadId, cancellationToken)
            ?? throw new KeyNotFoundException($"Lead {leadId} no encontrado.");

        var quotes = await _db.Quotes
            .Where(q => q.LeadId == leadId)
            .OrderByDescending(q => q.CreatedAt)
            .Select(q => new LeadJourneyQuoteDto
            {
                PublicId = q.PublicId,
                QuoteNumber = q.QuoteNumber,
                Title = q.Title,
                Status = q.Status,
                CustomerPublicId = q.Customer != null ? q.Customer.PublicId : null,
                ConvertedReservaPublicId = q.ConvertedReserva != null ? q.ConvertedReserva.PublicId : null,
                CreatedAt = q.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var reservas = await _db.Reservas
            .Where(r => r.SourceLeadId == leadId || (r.SourceQuote != null && r.SourceQuote.LeadId == leadId))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new LeadJourneyReservaDto
            {
                PublicId = r.PublicId,
                NumeroReserva = r.NumeroReserva,
                Name = r.Name,
                Status = r.Status,
                SourceQuotePublicId = r.SourceQuote != null ? r.SourceQuote.PublicId : null,
                CreatedAt = r.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return new LeadJourneyDto
        {
            LeadPublicId = lead.PublicId,
            ConvertedCustomerPublicId = lead.ConvertedCustomer?.PublicId,
            ConvertedCustomerName = lead.ConvertedCustomer?.FullName,
            LatestQuotePublicId = quotes.FirstOrDefault()?.PublicId,
            LatestReservaPublicId = reservas.FirstOrDefault()?.PublicId,
            Quotes = quotes,
            Reservas = reservas
        };
    }

    public async Task<object> GetPipelineAsync(CancellationToken cancellationToken)
    {
        var leads = await _db.Leads
            .Select(l => new
            {
                l.PublicId, l.FullName, l.Status, l.Source, l.InterestedIn,
                l.EstimatedBudget, l.AssignedToName, l.NextFollowUp, l.CreatedAt,
                ConvertedCustomerPublicId = l.ConvertedCustomer != null ? l.ConvertedCustomer.PublicId : (Guid?)null,
                ActivitiesCount = l.Activities.Count,
                LastActivity = l.Activities.OrderByDescending(a => a.CreatedAt).Select(a => a.Description).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return new
        {
            Nuevo = leads.Where(l => l.Status == LeadStatus.New).ToList(),
            Contactado = leads.Where(l => l.Status == LeadStatus.Contacted).ToList(),
            Cotizado = leads.Where(l => l.Status == LeadStatus.Quoted).ToList(),
            Ganado = leads.Where(l => l.Status == LeadStatus.Won).ToList(),
            Perdido = leads.Where(l => l.Status == LeadStatus.Lost).ToList(),
            TotalLeads = leads.Count,
            TotalBudget = leads.Sum(l => l.EstimatedBudget),
            ConversionRate = leads.Count > 0
                ? Math.Round((decimal)leads.Count(l => l.Status == LeadStatus.Won) / leads.Count * 100, 1)
                : 0
        };
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

    private static LeadSummaryDto MapLeadSummary(Lead lead)
    {
        return new LeadSummaryDto
        {
            PublicId = lead.PublicId,
            FullName = lead.FullName,
            Email = lead.Email,
            Phone = lead.Phone,
            Status = lead.Status,
            Source = lead.Source,
            InterestedIn = lead.InterestedIn,
            TravelDates = lead.TravelDates,
            Travelers = lead.Travelers,
            EstimatedBudget = lead.EstimatedBudget,
            Notes = lead.Notes,
            AssignedToUserId = lead.AssignedToUserId,
            AssignedToName = lead.AssignedToName,
            NextFollowUp = lead.NextFollowUp,
            CreatedAt = lead.CreatedAt,
            ClosedAt = lead.ClosedAt,
            ConvertedCustomerPublicId = lead.ConvertedCustomer?.PublicId,
            ConvertedCustomerName = lead.ConvertedCustomer?.FullName,
            ActivitiesCount = lead.Activities?.Count ?? 0,
            LastActivity = lead.Activities?
                .OrderByDescending(activity => activity.CreatedAt)
                .FirstOrDefault()?
                .Description
        };
    }

    private static LeadDetailDto MapLeadDetail(Lead lead)
    {
        var summary = MapLeadSummary(lead);
        return new LeadDetailDto
        {
            PublicId = summary.PublicId,
            FullName = summary.FullName,
            Email = summary.Email,
            Phone = summary.Phone,
            Status = summary.Status,
            Source = summary.Source,
            InterestedIn = summary.InterestedIn,
            TravelDates = summary.TravelDates,
            Travelers = summary.Travelers,
            EstimatedBudget = summary.EstimatedBudget,
            Notes = summary.Notes,
            AssignedToUserId = summary.AssignedToUserId,
            AssignedToName = summary.AssignedToName,
            NextFollowUp = summary.NextFollowUp,
            CreatedAt = summary.CreatedAt,
            ClosedAt = summary.ClosedAt,
            ConvertedCustomerPublicId = summary.ConvertedCustomerPublicId,
            ConvertedCustomerName = summary.ConvertedCustomerName,
            ActivitiesCount = summary.ActivitiesCount,
            LastActivity = summary.LastActivity,
            Activities = lead.Activities?
                .OrderByDescending(activity => activity.CreatedAt)
                .Select(MapLeadActivity)
                .ToList() ?? new List<LeadActivityDto>()
        };
    }

    private static LeadActivityDto MapLeadActivity(LeadActivity activity)
    {
        return new LeadActivityDto
        {
            PublicId = activity.PublicId,
            Type = activity.Type,
            Description = activity.Description,
            CreatedBy = activity.CreatedBy,
            CreatedAt = activity.CreatedAt
        };
    }

    private static Lead MapLeadFromRequest(LeadUpsertRequest request)
    {
        return new Lead
        {
            FullName = request.FullName,
            Email = request.Email,
            Phone = request.Phone,
            Source = request.Source,
            InterestedIn = request.InterestedIn,
            TravelDates = request.TravelDates,
            Travelers = request.Travelers,
            EstimatedBudget = request.EstimatedBudget,
            Notes = request.Notes,
            AssignedToUserId = request.AssignedToUserId,
            AssignedToName = request.AssignedToName,
            NextFollowUp = request.NextFollowUp
        };
    }

    private static IQueryable<Lead> ApplyLeadView(IQueryable<Lead> query, string? view)
    {
        var normalizedView = (view ?? "active").Trim().ToLowerInvariant();

        return normalizedView switch
        {
            "active" => query.Where(lead => lead.Status != LeadStatus.Won && lead.Status != LeadStatus.Lost),
            "closed" => query.Where(lead => lead.Status == LeadStatus.Won || lead.Status == LeadStatus.Lost),
            _ => query
        };
    }

    private static IQueryable<Lead> ApplyLeadStatus(IQueryable<Lead> query, string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            return query;
        }

        return query.Where(lead => lead.Status == status.Trim());
    }

    private static IQueryable<Lead> ApplyLeadSource(IQueryable<Lead> query, string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || string.Equals(source, "all", StringComparison.OrdinalIgnoreCase))
        {
            return query;
        }

        var normalizedSource = source.Trim().ToLowerInvariant();
        return query.Where(lead => lead.Source != null && lead.Source.ToLower() == normalizedSource);
    }

    private static IQueryable<Lead> ApplyLeadSearch(IQueryable<Lead> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalizedSearch = search.Trim().ToLowerInvariant();
        return query.Where(lead =>
            lead.FullName.ToLower().Contains(normalizedSearch) ||
            (lead.Phone != null && lead.Phone.ToLower().Contains(normalizedSearch)) ||
            (lead.Email != null && lead.Email.ToLower().Contains(normalizedSearch)) ||
            (lead.InterestedIn != null && lead.InterestedIn.ToLower().Contains(normalizedSearch)));
    }

    private static IQueryable<Lead> ApplyLeadOrdering(IQueryable<Lead> query, LeadListQuery request)
    {
        var sortBy = (request.SortBy ?? "createdAt").Trim().ToLowerInvariant();
        var isDescending = !string.Equals(request.SortDir, "asc", StringComparison.OrdinalIgnoreCase);

        return sortBy switch
        {
            "fullname" => isDescending
                ? query.OrderByDescending(lead => lead.FullName).ThenByDescending(lead => lead.CreatedAt)
                : query.OrderBy(lead => lead.FullName).ThenByDescending(lead => lead.CreatedAt),
            "nextfollowup" => isDescending
                ? query.OrderByDescending(lead => lead.NextFollowUp).ThenByDescending(lead => lead.CreatedAt)
                : query.OrderBy(lead => lead.NextFollowUp).ThenByDescending(lead => lead.CreatedAt),
            _ => isDescending
                ? query.OrderByDescending(lead => lead.CreatedAt).ThenByDescending(lead => lead.Id)
                : query.OrderBy(lead => lead.CreatedAt).ThenBy(lead => lead.Id)
        };
    }
}
