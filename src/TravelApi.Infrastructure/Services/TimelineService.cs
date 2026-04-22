using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class TimelineService : ITimelineService
{
    private readonly AppDbContext _context;

    private static readonly string[] IgnoredFields = { 
        "UpdatedAt", "TotalSale", "TotalCost", "Balance", "TotalPaid", 
        "IsEconomicallySettled", "CanMoveToOperativo", "CanEmitVoucher", 
        "CanEmitAfipInvoice", "EconomicBlockReason"
    };

    public TimelineService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<TimelineEventDto>> GetTimelineAsync(string reservaPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, cancellationToken);
        return await GetTimelineAsync(reservaId, cancellationToken);
    }

    public async Task<List<TimelineEventDto>> GetTimelineAsync(int reservaId, CancellationToken cancellationToken)
    {
        var flightIds = await _context.FlightSegments.Where(x => x.ReservaId == reservaId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);
        var hotelIds = await _context.HotelBookings.Where(x => x.ReservaId == reservaId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);
        var packageIds = await _context.PackageBookings.Where(x => x.ReservaId == reservaId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);
        var transferIds = await _context.TransferBookings.Where(x => x.ReservaId == reservaId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);
        var serviceIds = await _context.Servicios.Where(x => x.ReservaId == reservaId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);
        var paymentIds = await _context.Payments.Where(x => x.ReservaId == reservaId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);
        var invoiceIds = await _context.Invoices.Where(x => x.ReservaId == reservaId).Select(x => x.Id.ToString()).ToListAsync(cancellationToken);

        var rId = reservaId.ToString();

        var logs = await _context.AuditLogs
            .AsNoTracking()
            .Where(a => 
                (a.EntityName == "Reserva" && a.EntityId == rId) ||
                (a.EntityName == "FlightSegment" && flightIds.Contains(a.EntityId)) ||
                (a.EntityName == "HotelBooking" && hotelIds.Contains(a.EntityId)) ||
                (a.EntityName == "PackageBooking" && packageIds.Contains(a.EntityId)) ||
                (a.EntityName == "TransferBooking" && transferIds.Contains(a.EntityId)) ||
                (a.EntityName == "ServicioReserva" && serviceIds.Contains(a.EntityId)) ||
                (a.EntityName == "Payment" && paymentIds.Contains(a.EntityId)) ||
                (a.EntityName == "Invoice" && invoiceIds.Contains(a.EntityId)) ||
                (a.EntityName == "ReservaAttachment" && a.Changes!.Contains($"\"ReservaId\":{{\"New\":{rId}}}")) // Fallback for attachments if they log FK
            )
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);

        var events = new List<TimelineEventDto>();

        foreach (var log in logs)
        {
            var eventType = log.Action;
            var friendlyEntity = NormalizeEntityName(log.EntityName);
            var title = $"{TranslateAction(log.Action)} {friendlyEntity}";
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.Changes))
            {
                try
                {
                    var changesObj = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(log.Changes);
                    if (changesObj != null)
                    {
                        var meaningfulChanges = changesObj.Where(kvp => !IgnoredFields.Contains(kvp.Key)).ToList();
                        
                        if (log.Action == "Update" && meaningfulChanges.Count == 0)
                        {
                            continue; // Skip logs that only contain ignored fields (like Balance auto-update)
                        }

                        foreach (var change in meaningfulChanges)
                        {
                            var oldVal = change.Value.ContainsKey("Old") ? change.Value["Old"].ToString() : "";
                            var newVal = change.Value.ContainsKey("New") ? change.Value["New"].ToString() : "";
                            details.Add($"• **{change.Key}**: {oldVal} ➔ {newVal}");
                        }
                    }
                }
                catch
                {
                    details.Add("Detalles técnicos ocultos por legibilidad.");
                }
            }

            events.Add(new TimelineEventDto
            {
                Timestamp = log.Timestamp,
                Actor = string.IsNullOrWhiteSpace(log.UserName) ? "Sistema" : log.UserName,
                EventType = eventType,
                Title = title,
                Details = details.Count > 0 ? string.Join("\n", details) : null,
                RelatedEntityType = log.EntityName
            });
        }

        return events;
    }

    private async Task<int> ResolveRequiredIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken)
        where TEntity : class, IHasPublicId
    {
        var resolved = await _context.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado.");
    }

    private static string NormalizeEntityName(string technicalName)
    {
        return technicalName switch
        {
            "Reserva" => "Reserva",
            "FlightSegment" => "Vuelo",
            "HotelBooking" => "Hotel",
            "PackageBooking" => "Paquete",
            "TransferBooking" => "Traslado",
            "ServicioReserva" => "Servicio Gral.",
            "Payment" => "Cobranza",
            "Invoice" => "Factura AFIP",
            "ReservaAttachment" => "Archivo Adjunto",
            _ => technicalName
        };
    }

    private static string TranslateAction(string action)
    {
        return action switch
        {
            "Create" => "Alta de",
            "Update" => "Modificación en",
            "Delete" => "Eliminación de",
            _ => action
        };
    }
}
