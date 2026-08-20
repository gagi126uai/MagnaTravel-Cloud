using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class TimelineService : ITimelineService
{
    private readonly AppDbContext _context;

    // Campos tecnicos que NUNCA importan en el timeline de NINGUNA entidad (ids internos, columnas
    // derivadas/calculadas, timestamps de auditoria). OJO: "Status" NO va aca — FlightSegment, HotelBooking,
    // PackageBooking, TransferBooking, AssistanceBooking, ServicioReserva y Payment tienen su PROPIO campo
    // Status (confirmacion de vuelo, cancelacion de un pago, etc.) y esos cambios SI tienen que seguir
    // apareciendo en el timeline. Ver IsIgnoredField mas abajo para el filtro puntual de Reserva.Status.
    private static readonly string[] IgnoredFields = {
        "Id", "PublicId", "UpdatedAt", "CreatedAt", "TotalSale", "TotalCost", "Balance", "TotalPaid",
        "IsEconomicallySettled", "CanMoveToOperativo", "CanEmitVoucher",
        "CanEmitAfipInvoice", "EconomicBlockReason", "ReservaId", "RateId", "SupplierId",
        "CustomerId", "SourceLeadId", "SourceQuoteId", "ServicioReservaId", "ResponsibleUserId",
        "TravelFileId", "ReservationId"
    };

    /// <summary>
    /// Tanda 3 (2026-08-18): el diff generico de AuditLogs ya NO arma el evento de cambio de estado DE LA
    /// RESERVA (era pobre: solo "de X a Y" sin motivo ni quien autorizo). Ahora el UNICO evento de cambio
    /// de estado de la Reserva sale de ReservaStatusChangeLogs, mas rico (Reason + autorizante) — ver
    /// BuildStatusChangeEventsAsync mas abajo. El filtro es PUNTUAL a "Reserva"+"Status" (no un
    /// IgnoredField global): las demas entidades con su propio campo Status (Payment, FlightSegment, etc.)
    /// siguen mostrando esos cambios normalmente, no tienen una fuente mas rica que los reemplace.
    /// </summary>
    private static bool IsIgnoredField(string entityName, string fieldName)
        => IgnoredFields.Contains(fieldName) || (entityName == "Reserva" && fieldName == "Status");

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
        var assistanceIds = await _context.AssistanceBookings.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var serviceIds = await _context.Servicios.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var paymentIds = await _context.Payments.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);
        var invoiceIds = await _context.Invoices.Where(x => x.ReservaId == reservaId).Select(x => x.PublicId.ToString()).ToListAsync(cancellationToken);

        // Item #6 (barrido T5, 2026-07-24): monto/moneda/metodo REALES de cada pago, leidos directo de
        // la tabla (no del diff generico de auditoria — ver el comentario mas abajo, junto al armado del
        // evento). IgnoreQueryFilters() a proposito: un pago anulado (IsDeleted=true) igual pudo dejar
        // eventos en el timeline (alta, anulacion) y quermos poder mostrar CUANTO fue ese pago aunque ya
        // este anulado, en vez de dejar el campo vacio.
        var paymentMoneyByPublicId = await _context.Payments
            .IgnoreQueryFilters()
            .Where(p => p.ReservaId == reservaId)
            .Select(p => new { PublicId = p.PublicId.ToString(), p.Amount, p.Currency, p.Method })
            .ToDictionaryAsync(p => p.PublicId, p => p, cancellationToken);

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
                (a.EntityName == "AssistanceBooking" && assistanceIds.Contains(a.EntityId)) ||
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
                        var meaningfulChanges = changesObj.Where(kvp => !IsIgnoredField(log.EntityName, kvp.Key)).ToList();
                        
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
                    // RIESGO CONOCIDO (no se toca en esta tanda, ver item #6 del barrido T5, 2026-07-24):
                    // el auto-audit generico de AppDbContext.OnBeforeSaveChanges guarda el "Create" como
                    // {"Campo": valorCrudo} (sin envolver en {Old,New}), pero aca arriba SIEMPRE se
                    // deserializa como Dictionary<string, Dictionary<string, JsonElement>> — eso hace que
                    // CUALQUIER alta (no solo Pago) caiga siempre en este catch y muestre el texto
                    // generico de abajo en vez del detalle real. Es un bug preexistente y mas grande que
                    // este item puntual (afecta TODAS las entidades, no solo Payment); arreglarlo toca el
                    // auto-audit compartido por todo el sistema, asi que queda FUERA de este cambio chico
                    // y aditivo — reportado para una tanda propia. La correccion de ESTE item (monto y
                    // metodo de pago en el historial) se resuelve mas abajo leyendo el Payment real
                    // directo de la tabla, sin depender de este diff.
                    details.Add("Modificaciones en campos técnicos.");
                }
            }

            // Item #6 (barrido T5, 2026-07-24): monto/moneda/metodo del pago, SOLO para eventos sobre un
            // Payment, leidos de paymentMoneyByPublicId (la tabla real) en vez del diff generico de
            // arriba (que para el "Create" cae siempre en el catch, ver el comentario ahi). Null para
            // cualquier otro tipo de evento (Reserva, Factura, servicios, etc.) — no aplica.
            decimal? paymentAmount = null;
            string? paymentCurrency = null;
            string? paymentMethod = null;
            if (log.EntityName == "Payment" && paymentMoneyByPublicId.TryGetValue(log.EntityId, out var paymentMoney))
            {
                paymentAmount = paymentMoney.Amount;
                paymentCurrency = paymentMoney.Currency;
                paymentMethod = paymentMoney.Method;
            }

            events.Add(new TimelineEventDto
            {
                Timestamp = log.Timestamp,
                Actor = actor,
                EventType = eventType,
                Title = title,
                Details = details.Count > 0 ? string.Join("\n", details) : null,
                RelatedEntityType = log.EntityName,
                Amount = paymentAmount,
                Currency = paymentCurrency,
                PaymentMethod = paymentMethod
            });
        }

        // Tanda 3 (2026-08-18): segunda fuente del timeline, ademas de AuditLogs. ReservaStatusChangeLogs
        // es el rastro auditable oficial de cada cambio de Reserva.Status (escrito por el punto UNICO de
        // transicion, ReservaStatusTransitioner.ApplyAsync) y trae datos que el diff generico de
        // AuditLogs no tenia: el motivo tipeado por el usuario y, en reversiones sin ser Admin, quien
        // autorizo. Por eso reemplaza al evento generico de "Status" (ver IgnoredFields arriba).
        var statusChangeEvents = await BuildStatusChangeEventsAsync(reservaId, cancellationToken);

        // Obra "la ficha del operador no borra la historia" (2026-08-20, punto 4): el timeline de la RESERVA
        // no incluia nada de su circuito de cancelacion (multa del operador, notas de credito). Se agrega ACA
        // (no parseando AuditLog: se lee directo de BookingCancellationLine/BookingCancellationCreditNote, que
        // SI tienen los datos estructurados que hacen falta — motivo, monto, moneda, estado de ARCA).
        var operatorPenaltyEvents = await BuildOperatorPenaltyEventsAsync(reservaId, cancellationToken);
        var creditNoteEvents = await BuildCreditNoteEventsAsync(reservaId, cancellationToken);

        // Fusion de las fuentes, ordenada por fecha de mas nuevo a mas viejo. OrderByDescending de
        // LINQ es ESTABLE (misma clave => se respeta el orden de entrada), asi que concatenar AuditLogs
        // ANTES de ReservaStatusChangeLogs alcanza para que, ante un empate exacto de Timestamp, el
        // evento de AuditLogs quede primero (desempate pedido: "AuditLogs primero").
        return events
            .Concat(statusChangeEvents)
            .Concat(operatorPenaltyEvents)
            .Concat(creditNoteEvents)
            .OrderByDescending(e => e.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Obra "la ficha del operador no borra la historia" (2026-08-20): eventos de la MULTA del operador
    /// (confirmada o cerrada sin multa) de todos los operadores de esta reserva — una reserva puede tener
    /// varios operadores (ADR-025), cada uno con su propia linea de cancelacion.
    ///
    /// <para><b>GAP conocido (documentado, no inventado)</b>: "quien cerro sin multa" (el actor) solo se
    /// persiste en el <see cref="BookingCancellation"/> PADRE (<c>PenaltyConfirmedByUserName</c>), y ese
    /// dato es del operador PRINCIPAL del BC. Para un operador SECUNDARIO (ADR-044 T1, gap ya anotado en
    /// <c>BookingCancellationService</c>) no hay actor persistido a nivel de linea — el evento sale con la
    /// frase generica "Se cerró..." (sin nombre) en vez de inventar un actor que el esquema no tiene.</para>
    ///
    /// <para><b>SIN gate cobranzas.see_cost a proposito (verificado, no supuesto)</b> — el monto de la
    /// multa del operador viaja SIN enmascarar, a diferencia de <c>SupplierService.BuildSupplierPenaltyTimelineEventsAsync</c>
    /// (que SI enmascara: ese metodo lo lee un vendedor con <c>proveedores.view</c>, un permiso MAS chico
    /// que <c>reservas.view</c>+ownership). Este metodo alimenta <c>GET /reservas/{id}/timeline</c>
    /// (<c>ReservasController.cs</c>, <c>[RequirePermission(Permissions.ReservasView)] +
    /// [RequireOwnership(OwnedEntity.Reserva, bypassPermission: Permissions.ReservasViewAll)]</c>) — EXACTAMENTE
    /// el mismo gate que <c>CancellationsController.GetByReserva</c> (<c>CancellationsController.cs:1100-1102</c>),
    /// cuyo propio XML-doc (<c>CancellationsController.cs:1093-1096</c>) dice explicitamente "NO usa
    /// cobranzas.* porque es una vista del vendedor sobre SU reserva". Ese mismo endpoint YA expone, al
    /// MISMO publico y SIN mascara, <c>FiscalLiquidation.OperatorPenaltyAmount</c>
    /// (<c>BookingCancellationService.cs:14347</c>, mapeado por <c>MapToDtoAsync</c> sin ningun chequeo de
    /// permiso adentro — <c>BookingCancellationService.cs:14251-14357</c>), documentado en el dominio como
    /// "Penalidad cobrada por el operador (la retiene de lo devuelto)" (<c>FiscalLiquidation.cs:63-64</c>):
    /// es LA MISMA plata que esta linea muestra desglosada por operador. F-12 (la multa del operador se
    /// traslada 1:1 al cliente via la Nota de Debito, pass-through por default —
    /// <c>CancellationConceptKind.OperatorPenaltyPassThrough</c>) es lo que hace que "cuanto retuvo el
    /// operador" y "cuanto le cobramos al cliente" sean el MISMO numero para el caso comun: el vendedor que
    /// ya ve la ND del cliente no gana nada nuevo viendo este evento. Agregar un gate aca introduciria una
    /// INCONSISTENCIA (el mismo dato tapado en un lado y visible en el otro para el mismo usuario), no una
    /// proteccion real.</para>
    /// </summary>
    private async Task<List<TimelineEventDto>> BuildOperatorPenaltyEventsAsync(int reservaId, CancellationToken cancellationToken)
    {
        var rows = await _context.BookingCancellationLines
            .AsNoTracking()
            .Where(line => line.BookingCancellation.ReservaId == reservaId
                && (line.PenaltyStatus == PenaltyStatus.Confirmed || line.PenaltyStatus == PenaltyStatus.Waived))
            .Select(line => new
            {
                line.PenaltyStatus,
                line.PenaltyAmount,
                line.PenaltyCurrency,
                line.PenaltyConfirmedAt,
                SupplierName = line.Supplier.Name,
                IsPrincipalOperator = line.SupplierId == line.BookingCancellation.SupplierId,
                BcPenaltyConfirmedByUserName = line.BookingCancellation.PenaltyConfirmedByUserName,
                BcCreatedAt = line.BookingCancellation.DraftedAt,
            })
            .ToListAsync(cancellationToken);

        var events = new List<TimelineEventDto>();
        foreach (var row in rows)
        {
            // Fallback de fecha: PenaltyConfirmedAt deberia estar siempre en Confirmed/Waived, pero por las
            // dudas con datos legacy incompletos se usa la fecha de creacion del BC en vez de dejar el
            // evento sin fecha (mejor una fecha aproximada que perder el evento del timeline).
            var timestamp = row.PenaltyConfirmedAt ?? row.BcCreatedAt;

            if (row.PenaltyStatus == PenaltyStatus.Confirmed)
            {
                events.Add(new TimelineEventDto
                {
                    Timestamp = timestamp,
                    Actor = "Sistema",
                    EventType = "OperatorPenaltyConfirmed",
                    Title = row.PenaltyAmount.HasValue
                        ? $"La multa del operador quedó confirmada: {FormatMoney(row.PenaltyAmount.Value, row.PenaltyCurrency)}."
                        : "La multa del operador quedó confirmada.",
                    Details = $"Operador: {row.SupplierName}",
                    RelatedEntityType = "BookingCancellationLine",
                    Amount = row.PenaltyAmount,
                    Currency = string.IsNullOrWhiteSpace(row.PenaltyCurrency) ? null : row.PenaltyCurrency,
                });
            }
            else // Waived
            {
                // Solo el operador PRINCIPAL del BC tiene actor persistido (ver el GAP documentado arriba).
                var actorName = row.IsPrincipalOperator && !string.IsNullOrWhiteSpace(row.BcPenaltyConfirmedByUserName)
                    ? row.BcPenaltyConfirmedByUserName
                    : null;
                events.Add(new TimelineEventDto
                {
                    Timestamp = timestamp,
                    Actor = actorName ?? "Sistema",
                    EventType = "OperatorPenaltyWaived",
                    Title = actorName != null
                        ? $"{actorName} cerró la multa del operador sin cobrar nada."
                        : "Se cerró la multa del operador sin cobrar nada.",
                    Details = $"Operador: {row.SupplierName}",
                    RelatedEntityType = "BookingCancellationLine",
                });
            }
        }

        return events;
    }

    /// <summary>
    /// Obra "la ficha del operador no borra la historia" (2026-08-20): eventos de las notas de credito de
    /// la cancelacion de esta reserva (emitida / rechazada por ARCA), con el texto EXACTO ya escrito en
    /// <c>multiCreditNoteFlow.js</c> (<c>describirNotaPorFactura</c>) para que ambas pantallas digan lo
    /// mismo con las mismas palabras.
    ///
    /// <para><b>GAP conocido (documentado, no inventado)</b>: <see cref="BookingCancellationCreditNote"/>
    /// solo persiste <c>CreatedAt</c> (cuando se creo el Pending) y su <c>Status</c> ACTUAL — no hay una
    /// fecha de "cuando paso a Succeeded/Failed" ni un historial de reintentos. Por eso <b>no se arma el
    /// evento "reintentada"</b> del punto 4.2 de la spec (no hay dato de dominio del que salga): se muestra
    /// UN evento por NC con su estado actual, fechado en <c>CreatedAt</c> (la mejor fecha disponible).</para>
    /// </summary>
    private async Task<List<TimelineEventDto>> BuildCreditNoteEventsAsync(int reservaId, CancellationToken cancellationToken)
    {
        var rows = await _context.BookingCancellationCreditNotes
            .AsNoTracking()
            .Where(note => note.BookingCancellation.ReservaId == reservaId
                && note.Status != BookingCancellationCreditNoteStatus.Pending)
            .Select(note => new
            {
                note.Status,
                note.ArcaErrorMessage,
                note.CreatedAt,
                note.OriginatingInvoice.TipoComprobante,
                note.OriginatingInvoice.PuntoDeVenta,
                note.OriginatingInvoice.NumeroComprobante,
            })
            .ToListAsync(cancellationToken);

        var events = new List<TimelineEventDto>();
        foreach (var row in rows)
        {
            var label = ComprobanteLabel.Format(row.TipoComprobante, row.PuntoDeVenta, row.NumeroComprobante);

            if (row.Status == BookingCancellationCreditNoteStatus.Succeeded)
            {
                events.Add(new TimelineEventDto
                {
                    Timestamp = row.CreatedAt,
                    Actor = "Sistema",
                    EventType = "CreditNoteEmitted",
                    Title = $"{label} — nota de crédito emitida.",
                    RelatedEntityType = "BookingCancellationCreditNote",
                });
            }
            else // Failed
            {
                // P-13/T1: el motivo de ARCA se muestra saneado pero TAL CUAL lo respondio el rechazo, nunca
                // parafraseado (mismo criterio que describirNotaPorFactura en el frontend).
                var motivo = ArcaErrorSanitizer.SanitizeArcaError(row.ArcaErrorMessage);
                events.Add(new TimelineEventDto
                {
                    Timestamp = row.CreatedAt,
                    Actor = "Sistema",
                    EventType = "CreditNoteRejected",
                    Title = string.IsNullOrWhiteSpace(motivo)
                        ? $"{label} — la nota no salió."
                        : $"{label} — la nota no salió. ARCA respondió: «{motivo}».",
                    RelatedEntityType = "BookingCancellationCreditNote",
                });
            }
        }

        return events;
    }

    /// <summary>Formatea un monto con su moneda en texto criollo simple ("$ 45.000" / "USD 500"). Sin moneda = ARS.</summary>
    private static string FormatMoney(decimal amount, string? currency)
    {
        var iso = string.IsNullOrWhiteSpace(currency) ? "ARS" : currency;
        var formatted = amount.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-AR"));
        return iso == "ARS" ? $"$ {formatted}" : $"{iso} {formatted}";
    }

    /// <summary>
    /// Arma un TimelineEventDto por cada fila de ReservaStatusChangeLogs de la reserva: el rastro
    /// auditable de cambios de estado, con motivo y (si aplica) quien autorizo la reversion.
    /// </summary>
    private async Task<List<TimelineEventDto>> BuildStatusChangeEventsAsync(int reservaId, CancellationToken cancellationToken)
    {
        var logs = await _context.ReservaStatusChangeLogs
            .AsNoTracking()
            .Where(log => log.ReservaId == reservaId)
            .ToListAsync(cancellationToken);

        var events = new List<TimelineEventDto>();
        foreach (var log in logs)
        {
            events.Add(new TimelineEventDto
            {
                Timestamp = log.OccurredAt,
                // "Sistema" cuando el cambio lo disparo un proceso automatico (job de lifecycle) sin
                // usuario logueado detras — mismo criterio que el resto del timeline (ver actor mas arriba).
                Actor = !string.IsNullOrWhiteSpace(log.ByUserName) ? log.ByUserName : "Sistema",
                EventType = "StatusChange",
                // Los codigos de estado ("Budget"/"Reserved"/etc.) viajan CRUDOS: no existe todavia un
                // traductor a español en el backend (solo en el frontend, traducirEstadoReserva) y el
                // proyecto ya tiene la convencion de dejar estos enums-string sin traducir en la API
                // (ver el comentario de PaymentMethod en TimelineEventDto). NO se inventa un mapa nuevo
                // aca: el frontend arma la frase legible con FromStatus/ToStatus.
                Title = $"Cambio de estado: {log.FromStatus} → {log.ToStatus}",
                Details = BuildStatusChangeDetails(log),
                RelatedEntityType = "Reserva",
                FromStatus = log.FromStatus,
                ToStatus = log.ToStatus
            });
        }

        return events;
    }

    /// <summary>
    /// Detalle del cambio de estado: el motivo tipeado por el usuario (si lo hubo) y, en reversiones
    /// autorizadas por un supervisor (revert de no-admin), quien la autorizo. Null si no hay nada de eso
    /// (la mayoria de las transiciones forward triviales no llevan motivo).
    /// </summary>
    private static string? BuildStatusChangeDetails(ReservaStatusChangeLog log)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(log.Reason))
            lines.Add(log.Reason.Trim());
        if (!string.IsNullOrWhiteSpace(log.AuthorizedBySuperiorUserName))
            lines.Add($"Autorizó: {log.AuthorizedBySuperiorUserName}");
        return lines.Count > 0 ? string.Join("\n", lines) : null;
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
            "AssistanceBooking" => "una Asistencia",
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
