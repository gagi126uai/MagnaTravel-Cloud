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
        "Id", "PublicId", "UpdatedAt", "CreatedAt", "TotalSale", "TotalCost", "Balance", "TotalPaid", 
        "IsEconomicallySettled", "CanMoveToOperativo", "CanEmitVoucher", 
        "CanEmitAfipInvoice", "EconomicBlockReason", "ReservaId", "RateId", "SupplierId", 
        "CustomerId", "SourceLeadId", "SourceQuoteId", "ServicioReservaId", "ResponsibleUserId",
        "TravelFileId", "ReservationId"
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
        var flightIds = await _context.FlightSegments.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var hotelIds = await _context.HotelBookings.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var packageIds = await _context.PackageBookings.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var transferIds = await _context.TransferBookings.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var serviceIds = await _context.Servicios.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var paymentIds = await _context.Payments.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var invoiceIds = await _context.Invoices.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);

        var rId = reservaId.ToString();
        var rPublicId = await _context.Reservas.Where(x => x.Id == reservaId).Select(x => x.PublicId.ToString()).FirstOrDefaultAsync(cancellationToken);

        var logsRaw = await _context.AuditLogs
            .AsNoTracking()
            .Where(a => 
                (a.EntityName == "Reserva" && (a.EntityId == rId || a.EntityId == rPublicId)) ||
                (a.EntityName == "FlightSegment" && flightIds.Contains(a.EntityId)) ||
                (a.EntityName == "HotelBooking" && hotelIds.Contains(a.EntityId)) ||
                (a.EntityName == "PackageBooking" && packageIds.Contains(a.EntityId)) ||
                (a.EntityName == "TransferBooking" && transferIds.Contains(a.EntityId)) ||
                (a.EntityName == "ServicioReserva" && serviceIds.Contains(a.EntityId)) ||
                (a.EntityName == "Payment" && paymentIds.Contains(a.EntityId)) ||
                (a.EntityName == "Invoice" && invoiceIds.Contains(a.EntityId)) ||
                (a.EntityName == "ReservaAttachment" && a.Changes!.Contains($"\"ReservaId\":{{\"New\":{rId}}}"))
            )
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);

        // Resolver nombres de usuario para logs antiguos o incompletos
        var userIds = logsRaw.Select(l => l.UserId).Distinct().ToList();
        var userMap = await _context.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var events = new List<TimelineEventDto>();

        foreach (var log in logsRaw)
        {
            var eventType = log.Action;
            var friendlyEntity = NormalizeEntityName(log.EntityName);
            var title = $"{TranslateAction(log.Action)} {friendlyEntity}";
            var details = new List<string>();

            // Resolver actor: Preferir FullName del mapa, luego UserName del log, luego "Sistema"
            var actor = "Sistema";
            if (userMap.TryGetValue(log.UserId, out var fullName) && !string.IsNullOrWhiteSpace(fullName))
            {
                actor = fullName;
            }
            else if (!string.IsNullOrWhiteSpace(log.UserName) && !Guid.TryParse(log.UserName, out _))
            {
                actor = log.UserName;
            }

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
                            continue;
                        }

                        foreach (var change in meaningfulChanges)
                        {
                            var fieldName = NormalizeFieldName(change.Key);
                            if (fieldName == change.Key) continue; // Si no tiene traducción, es técnico o no relevante

                            var oldValRaw = change.Value.ContainsKey("Old") ? change.Value["Old"].ToString() : "";
                            var newValRaw = change.Value.ContainsKey("New") ? change.Value["New"].ToString() : "";

                            if (log.Action == "Create")
                            {
                                var val = FormatValue(change.Key, oldValRaw.Length > 0 ? oldValRaw : newValRaw);
                                details.Add($"• **{fieldName}**: {val}");
                            }
                            else if (log.Action == "Update")
                            {
                                var oldVal = FormatValue(change.Key, oldValRaw);
                                var newVal = FormatValue(change.Key, newValRaw);
                                
                                if (oldVal == newVal) continue;

                                details.Add($"• {fieldName}: de *{oldVal}* a **{newVal}**");
                            }
                            else if (log.Action == "Delete")
                            {
                                var val = FormatValue(change.Key, oldValRaw);
                                details.Add($"• **{fieldName}**: {val}");
                            }
                        }
                    }
                }
                catch
                {
                    details.Add("Modificaciones en campos técnicos.");
                }
            }

            events.Add(new TimelineEventDto
            {
                Timestamp = log.Timestamp,
                Actor = actor,
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
            "Reserva" => "la Reserva",
            "FlightSegment" => "un Vuelo",
            "HotelBooking" => "un Hotel",
            "PackageBooking" => "un Paquete",
            "TransferBooking" => "un Traslado",
            "ServicioReserva" => "un Servicio",
            "Payment" => "un Pago",
            "Invoice" => "una Factura",
            "ReservaAttachment" => "un Archivo",
            _ => technicalName
        };
    }

    private static string TranslateAction(string action)
    {
        return action switch
        {
            "Create" => "Alta de",
            "Update" => "Cambio en",
            "Delete" => "Eliminación de",
            "SoftDelete" => "Anulación de",
            _ => action
        };
    }

    private static string NormalizeFieldName(string fieldName)
    {
        return fieldName switch
        {
            "Status" => "Estado",
            "Name" => "Nombre",
            "Amount" => "Importe",
            "Method" => "Método",
            "PaidAt" => "Fecha Pago",
            "CheckIn" => "Check-In",
            "CheckOut" => "Check-Out",
            "DepartureTime" => "Salida",
            "ArrivalTime" => "Llegada",
            "Origin" => "Origen",
            "Destination" => "Destino",
            "FlightNumber" => "Nro. Vuelo",
            "AirlineCode" => "Línea Aérea",
            "Rooms" => "Habitaciones",
            "Adults" => "Adultos",
            "Children" => "Menores",
            "NetCost" => "Costo Neto",
            "SalePrice" => "Precio Venta",
            "UnitNetCost" => "Costo Unitario",
            "UnitSalePrice" => "Venta Unitario",
            "Commission" => "Comisión",
            "Tax" => "Impuestos",
            "SupplierId" => "Proveedor",
            "Description" => "Descripción",
            "Notes" => "Notas",
            "EntryType" => "Tipo de Pago",
            "RoomType" => "Habitación",
            "MealPlan" => "Régimen",
            "WorkflowStatus" => "Estado Operativo",
            "IsDeleted" => "Borrado",
            "ConfirmationNumber" => "Confirmación",
            "StartDate" => "Inicio",
            "EndDate" => "Fin",
            "HotelName" => "Hotel",
            "City" => "Ciudad",
            "StarRating" => "Categoría",
            "PackageName" => "Paquete",
            "PickupLocation" => "Origen Traslado",
            "DropoffLocation" => "Destino Traslado",
            "PickupDate" => "Fecha Traslado",
            "PickupTime" => "Hora Traslado",
            _ => fieldName
        };
    }

    private static string FormatValue(string fieldName, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "null") return "N/A";
        if (value == "0" && (fieldName.Contains("Price") || fieldName.Contains("Cost") || fieldName.Contains("Amount"))) return "0";
        
        if (fieldName.Contains("Price") || fieldName.Contains("Cost") || fieldName.Contains("Amount") || fieldName.Contains("Tax") || fieldName.Contains("Commission"))
        {
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var decimalValue))
            {
                return decimalValue.ToString("C", new System.Globalization.CultureInfo("es-AR"));
            }
        }

        if (value.Contains("T") && DateTime.TryParse(value, out var dateValue))
        {
            if (dateValue.TimeOfDay.TotalSeconds == 0) return dateValue.ToString("dd/MM/yyyy");
            return dateValue.ToString("dd/MM/yyyy HH:mm");
        }

        if (value.ToLower() == "true") return "Sí";
        if (value.ToLower() == "false") return "No";

        return value;
    }
}
