using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Authorization;

/// <summary>
/// B1.15 Fase 1: implementacion default de <see cref="IOwnershipResolver"/>.
/// Lookup por <see cref="OwnedEntity"/> contra AppDbContext.
///
/// Solo Reserva tiene <c>ResponsibleUserId</c>; las demas entidades se resuelven
/// via la Reserva padre (ServicioReserva, Payment, Invoice, Voucher, Passenger),
/// o via el Passenger padre (PassengerServiceAssignment).
///
/// Si la Reserva padre tiene <c>ResponsibleUserId IS NULL</c> (legacy sin backfill),
/// se rechaza explicitamente — Decision Gaston B1.15: no asumir ownership en
/// historicos sin backfill.
/// </summary>
public sealed class OwnershipResolver : IOwnershipResolver
{
    private readonly AppDbContext _dbContext;

    public OwnershipResolver(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> IsOwnerAsync(
        string userId,
        OwnedEntity entity,
        string publicIdOrLegacyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(publicIdOrLegacyId))
        {
            return false;
        }

        var (publicId, legacyId) = ParseId(publicIdOrLegacyId);
        if (publicId is null && legacyId is null)
        {
            return false;
        }

        // Resolver responsible por entidad. Patron: query a Reservas con un join logico
        // sobre la entidad, devolviendo el ResponsibleUserId.
        var responsibleUserId = entity switch
        {
            OwnedEntity.Reserva => await ResolveReservaResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Servicio => await ResolveServicioResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Payment => await ResolvePaymentResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Invoice => await ResolveInvoiceResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Voucher => await ResolveVoucherResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Passenger => await ResolvePassengerResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Assignment => await ResolveAssignmentResponsibleAsync(publicId, legacyId, cancellationToken),
            _ => null,
        };

        // Si la entidad no existe o la Reserva padre no tiene responsable, rechazar.
        if (string.IsNullOrEmpty(responsibleUserId))
        {
            return false;
        }

        return string.Equals(responsibleUserId, userId, StringComparison.Ordinal);
    }

    private static (Guid? publicId, int? legacyId) ParseId(string raw)
    {
        if (Guid.TryParse(raw, out var guid))
        {
            return (guid, null);
        }
        if (int.TryParse(raw, out var legacy) && legacy > 0)
        {
            return (null, legacy);
        }
        return (null, null);
    }

    private Task<string?> ResolveReservaResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Reservas.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(r => r.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(r => r.Id == legacyId!.Value);
        }
        return query.Select(r => r.ResponsibleUserId).FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveServicioResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Servicios.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(s => s.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(s => s.Id == legacyId!.Value);
        }
        return query
            .Where(s => s.Reserva != null)
            .Select(s => s.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolvePaymentResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Payments.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(p => p.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(p => p.Id == legacyId!.Value);
        }
        return query
            .Where(p => p.Reserva != null)
            .Select(p => p.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveInvoiceResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Invoices.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(i => i.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(i => i.Id == legacyId!.Value);
        }
        return query
            .Where(i => i.Reserva != null)
            .Select(i => i.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveVoucherResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Vouchers.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(v => v.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(v => v.Id == legacyId!.Value);
        }
        return query
            .Where(v => v.Reserva != null)
            .Select(v => v.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolvePassengerResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Passengers.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(p => p.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(p => p.Id == legacyId!.Value);
        }
        return query
            .Where(p => p.Reserva != null)
            .Select(p => p.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveAssignmentResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.PassengerServiceAssignments.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(a => a.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(a => a.Id == legacyId!.Value);
        }
        // Asignacion -> Passenger -> Reserva
        return query
            .Where(a => a.Passenger != null && a.Passenger.Reserva != null)
            .Select(a => a.Passenger!.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }
}
