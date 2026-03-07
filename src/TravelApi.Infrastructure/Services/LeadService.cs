using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class LeadService : ILeadService
{
    private readonly AppDbContext _db;

    public LeadService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<Lead>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await _db.Leads
            .Include(l => l.Activities.OrderByDescending(a => a.CreatedAt).Take(3))
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
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
            lead.ClosedAt = DateTime.UtcNow;

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
        lead.Status = LeadStatus.Won;
        lead.ClosedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return customer.Id;
    }

    public async Task<object> GetPipelineAsync(CancellationToken cancellationToken)
    {
        var leads = await _db.Leads
            .Select(l => new
            {
                l.Id, l.FullName, l.Status, l.Source, l.InterestedIn,
                l.EstimatedBudget, l.AssignedToName, l.NextFollowUp, l.CreatedAt,
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
}
