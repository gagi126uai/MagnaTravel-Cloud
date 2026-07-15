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
            OwnedEntity.Lead => await ResolveLeadResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Quote => await ResolveQuoteResponsibleAsync(publicId, legacyId, cancellationToken),
            // FC1.2.0 v3 (2026-05-17): nuevas entidades del modulo de cancelacion/refund.
            OwnedEntity.BookingCancellation => await ResolveBookingCancellationResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.ClientCreditEntry => await ResolveClientCreditEntryResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.Attachment => await ResolveAttachmentResponsibleAsync(publicId, legacyId, cancellationToken),
            // ADR-020 (2026-06-08): servicios tipados -> Reserva.ResponsibleUserId.
            OwnedEntity.FlightSegment => await ResolveFlightResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.HotelBooking => await ResolveHotelResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.TransferBooking => await ResolveTransferResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.PackageBooking => await ResolvePackageResponsibleAsync(publicId, legacyId, cancellationToken),
            OwnedEntity.AssistanceBooking => await ResolveAssistanceResponsibleAsync(publicId, legacyId, cancellationToken),
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

    private Task<string?> ResolveLeadResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Leads.AsNoTracking().AsQueryable();
        query = publicId.HasValue
            ? query.Where(lead => lead.PublicId == publicId.Value)
            : query.Where(lead => lead.Id == legacyId!.Value);
        return query.Select(lead => lead.AssignedToUserId).FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveQuoteResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.Quotes.AsNoTracking().AsQueryable();
        query = publicId.HasValue
            ? query.Where(quote => quote.PublicId == publicId.Value)
            : query.Where(quote => quote.Id == legacyId!.Value);
        return query.Select(quote => quote.Lead != null
            ? quote.Lead.AssignedToUserId
            : quote.ConvertedReserva != null ? quote.ConvertedReserva.ResponsibleUserId : null)
            .FirstOrDefaultAsync(ct);
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

    /// <summary>
    /// FC1.2.0 v3 (2026-05-17): BookingCancellation -> Reserva.ResponsibleUserId.
    /// La cancelacion es propiedad logica del responsable de la reserva que se
    /// cancela: si Pedro es responsable de la reserva, Pedro es responsable de
    /// cancelarla (o un admin con ReservasViewAll/ReservasCancel global).
    /// </summary>
    private Task<string?> ResolveBookingCancellationResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.BookingCancellations.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(b => b.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(b => b.Id == legacyId!.Value);
        }
        // BookingCancellation.Reserva siempre es required en el modelo (FK NOT NULL),
        // pero defendemos con un null-check por si una query incompleta hace skip.
        return query
            .Where(b => b.Reserva != null)
            .Select(b => b.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// FC1.2.0 v3 (2026-05-17): ClientCreditEntry hereda ownership via su
    /// <see cref="ClientCreditEntry.BookingCancellation"/> y, finalmente, via la
    /// <see cref="BookingCancellation.Reserva"/>.
    ///
    /// **Verificado**: Customer.cs NO tiene ResponsibleUserId (grep 2026-05-17).
    /// Si en el futuro la ficha de cliente gana un responsable comercial, este
    /// resolver puede agregar el fallback `entry.Customer.ResponsibleUserId`
    /// como primera opcion, manteniendo BC->Reserva como segundo fallback.
    /// </summary>
    private Task<string?> ResolveClientCreditEntryResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.ClientCreditEntries.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(e => e.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(e => e.Id == legacyId!.Value);
        }
        return query
            .Where(e => e.BookingCancellation != null && e.BookingCancellation.Reserva != null)
            .Select(e => e.BookingCancellation.Reserva.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 2026-06-03: ReservaAttachment -> Reserva.ResponsibleUserId. Espeja el
    /// resolver de Voucher: el adjunto es propiedad logica del responsable de la
    /// reserva a la que pertenece. Cierra el IDOR del AttachmentsController, donde
    /// cualquier autenticado podia listar/descargar/renombrar/borrar adjuntos de
    /// cualquier reserva. Admins/supervisores siguen entrando via bypassPermission
    /// (ReservasViewAll) resuelto en el filter, no aca.
    /// </summary>
    private Task<string?> ResolveAttachmentResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.ReservaAttachments.AsNoTracking().AsQueryable();
        if (publicId.HasValue)
        {
            query = query.Where(a => a.PublicId == publicId.Value);
        }
        else
        {
            query = query.Where(a => a.Id == legacyId!.Value);
        }
        // ReservaAttachment.Reserva es la reserva padre (FK ReservaId NOT NULL en el modelo);
        // el null-check defiende contra una query incompleta, igual que Voucher.
        return query
            .Where(a => a.Reserva != null)
            .Select(a => a.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    // ADR-020 (2026-06-08): resolvers de los 5 servicios tipados. Todos siguen el mismo patron
    // que ResolveServicioResponsibleAsync (servicio generico): servicio -> Reserva.ResponsibleUserId.
    // El PATCH /status de cada servicio se identifica por el id del servicio, no por reservaId.

    private Task<string?> ResolveFlightResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.FlightSegments.AsNoTracking().AsQueryable();
        query = publicId.HasValue
            ? query.Where(f => f.PublicId == publicId.Value)
            : query.Where(f => f.Id == legacyId!.Value);
        return query
            .Where(f => f.Reserva != null)
            .Select(f => f.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveHotelResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.HotelBookings.AsNoTracking().AsQueryable();
        query = publicId.HasValue
            ? query.Where(h => h.PublicId == publicId.Value)
            : query.Where(h => h.Id == legacyId!.Value);
        return query
            .Where(h => h.Reserva != null)
            .Select(h => h.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveTransferResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.TransferBookings.AsNoTracking().AsQueryable();
        query = publicId.HasValue
            ? query.Where(t => t.PublicId == publicId.Value)
            : query.Where(t => t.Id == legacyId!.Value);
        return query
            .Where(t => t.Reserva != null)
            .Select(t => t.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolvePackageResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.PackageBookings.AsNoTracking().AsQueryable();
        query = publicId.HasValue
            ? query.Where(p => p.PublicId == publicId.Value)
            : query.Where(p => p.Id == legacyId!.Value);
        return query
            .Where(p => p.Reserva != null)
            .Select(p => p.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }

    private Task<string?> ResolveAssistanceResponsibleAsync(Guid? publicId, int? legacyId, CancellationToken ct)
    {
        var query = _dbContext.AssistanceBookings.AsNoTracking().AsQueryable();
        query = publicId.HasValue
            ? query.Where(a => a.PublicId == publicId.Value)
            : query.Where(a => a.Id == legacyId!.Value);
        return query
            .Where(a => a.Reserva != null)
            .Select(a => a.Reserva!.ResponsibleUserId)
            .FirstOrDefaultAsync(ct);
    }
}
