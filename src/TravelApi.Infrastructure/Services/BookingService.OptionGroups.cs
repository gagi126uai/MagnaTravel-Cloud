using System.Data;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Opciones A/B/C (decisión #1 firmada del dueño, 2026-08-11/12): "resolver un grupo de opciones" —
/// el vendedor eligió cuál de las alternativas quedó (ej. "Hotel A") y las demás (ej. "Hotel B",
/// "Hotel C") se borran. Es el ÚNICO camino nuevo de esta obra que escribe: el guard que rechaza
/// "el cliente aceptó" con grupos ambiguos vive en <c>ReservaService.EnsureNoAmbiguousOptionGroupsAsync</c>
/// y los totales que no duplican plata viven en <see cref="TravelApi.Domain.Reservations.ReservaMoneyCalculator"/>.
///
/// <para><b>Fix B1 (review de seguridad, 2026-08-12)</b>: el guard "el cliente aceptó" (Presupuesto -&gt;
/// En gestión) solo corre UNA vez. Sin nada más, un servicio nuevo cargado DESPUÉS con el mismo
/// <c>OptionGroup</c> (reserva ya En gestión/Confirmada) reintroducía un grupo ambiguo que ya nunca se
/// re-validaba, y el motor automático podía confirmar la reserva igual (mirando solo "todo resuelto",
/// no "sin ambigüedad") — venta escondida (ninguna de las opciones del grupo suma a TotalSale). La
/// solución elegida es la más simple y la más coherente con el negocio: las opciones A/B/C SOLO se
/// pueden cargar/tocar mientras la reserva es Presupuesto (Quotation/Budget). Fuera de esa etapa, un
/// intento de setear/cambiar <c>OptionGroup</c> se rechaza acá mismo, en el punto de entrada
/// (<see cref="EnsureOptionGroupOnlySetDuringPresupuestoAsync"/>), en vez de tener que re-validar
/// ambigüedad en cada lugar que podría introducirla. El "cinturón" (defensa en profundidad) vive en
/// <c>ReservaAutoStateService.EvaluateAndApplyAsync</c>: aunque este candado tuviera un agujero, el motor
/// automático tampoco confirma una reserva con un grupo todavía ambiguo.</para>
/// </summary>
public partial class BookingService
{
    /// <summary>
    /// Un miembro (vivo) de un grupo de opciones. Incluye el snapshot de plata (SalePrice/NetCost/
    /// Currency) SOLO para el rastro auditable (PR-12) — <b>nunca sale en la respuesta de la API</b>: la
    /// API devuelve <see cref="RemovedOptionGroupServiceDto"/>, que no tiene montos (evita filtrar costo
    /// a un caller sin <c>cobranzas.see_cost</c>).
    /// </summary>
    private sealed record OptionGroupMember(
        string ServiceType, int Id, Guid PublicId, string Label,
        decimal SalePrice, decimal NetCost, string? Currency);

    public async Task<ResolveOptionGroupResultDto> ResolveOptionGroupAsync(
        string reservaPublicIdOrLegacyId, ResolveOptionGroupRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);

        var normalizedGroup = OptionGroupRules.Normalize(req.OptionGroup);
        if (normalizedGroup is null)
        {
            throw new ArgumentException("Indicá el grupo de opciones a resolver.");
        }

        if (string.IsNullOrWhiteSpace(req.WinnerServicePublicId))
        {
            throw new ArgumentException("Indicá cuál de las opciones quedó.");
        }

        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes de cualquier otra cosa —
        // mismo orden que Create/Update/Delete de servicios individuales.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(
            reservaId, ReservaEditAuthorizationOperations.ServiceDeleted, "OptionGroup", null, normalizedGroup, ct);

        var winnerId = await ResolveOptionGroupServiceIdAsync(req.WinnerServiceType, req.WinnerServicePublicId, ct);
        var (userId, userName) = GetActor();

        // Mejora TOCTOU (review de seguridad, 2026-08-12): la LECTURA de "quienes son los miembros vivos
        // del grupo ahora mismo" se movió ADENTRO de la transacción Serializable (antes quedaba afuera,
        // antes de abrir la transacción). Si dos resoluciones del MISMO grupo corren en paralelo, Postgres
        // detecta la superposición de lecturas/escrituras y una de las dos transacciones falla con un
        // conflicto de serialización en vez de dejar el grupo en un estado inconsistente (ej. las dos
        // "ganan" con ganadores distintos). Contra InMemory (tests unitarios, sin transacciones reales) el
        // cuerpo corre directo — mismo criterio que el resto del archivo.
        if (!_db.Database.IsRelational())
        {
            return await ResolveOptionGroupBodyAsync(reservaId, normalizedGroup, req.WinnerServiceType, winnerId, userId, userName, ct);
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await ResolveOptionGroupBodyAsync(reservaId, normalizedGroup, req.WinnerServiceType, winnerId, userId, userName, ct);
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>
    /// Cuerpo real de la resolución: lee los miembros VIVOS del grupo (fresco, dentro de la transacción),
    /// valida que el ganador siga siendo parte de ese grupo, borra a los demás y audita. Separado de
    /// <see cref="ResolveOptionGroupAsync"/> para poder correr EXACTAMENTE el mismo código con o sin
    /// transacción real (Postgres vs InMemory).
    /// </summary>
    private async Task<ResolveOptionGroupResultDto> ResolveOptionGroupBodyAsync(
        int reservaId, string normalizedGroup, string winnerServiceType, int winnerId,
        string? userId, string? userName, CancellationToken ct)
    {
        var allMembers = await LoadOptionGroupMembersAsync(reservaId, normalizedGroup, ct);
        if (allMembers.Count == 0)
        {
            throw new KeyNotFoundException("No hay servicios cargados en ese grupo de opciones.");
        }

        var winner = allMembers.FirstOrDefault(m =>
            string.Equals(m.ServiceType, winnerServiceType, StringComparison.OrdinalIgnoreCase) && m.Id == winnerId);
        if (winner is null)
        {
            // Mensaje criollo: puede pasar si alguien ya resolvió el grupo desde otra pestaña (o una
            // resolución concurrente ganó primero), o si el servicio elegido no pertenece a este grupo.
            // No hace falta distinguir el motivo exacto: el front vuelve a pedir el estado actual.
            throw new ArgumentException("Ese servicio no pertenece a este grupo de opciones (puede que ya se haya resuelto).");
        }

        var losers = allMembers.Where(m => m != winner).ToList();

        // Idempotente: si ya no queda más que el ganador (alguien resolvió el grupo antes, o nunca hubo
        // ambigüedad real), no hay nada que borrar ni que auditar.
        if (losers.Count == 0)
        {
            return new ResolveOptionGroupResultDto(
                normalizedGroup, winner.ServiceType, winner.PublicId, winner.Label,
                Array.Empty<RemovedOptionGroupServiceDto>());
        }

        foreach (var loser in losers)
        {
            await DeleteOptionGroupMemberAsync(reservaId, loser, ct);
        }
        await WriteResolveOptionGroupAuditAsync(reservaId, normalizedGroup, winner, losers, userId, userName, ct);

        return new ResolveOptionGroupResultDto(
            normalizedGroup, winner.ServiceType, winner.PublicId, winner.Label,
            losers.Select(l => new RemovedOptionGroupServiceDto(l.ServiceType, l.Label)).ToList());
    }

    /// <summary>Resuelve el id interno del servicio ganador según su tipo (mismo publicId-o-legacy-id que el resto de la API).</summary>
    private Task<int> ResolveOptionGroupServiceIdAsync(string serviceType, string publicIdOrLegacyId, CancellationToken ct)
        => serviceType switch
        {
            AssignmentServiceType.Hotel => ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct),
            AssignmentServiceType.Flight => ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct),
            AssignmentServiceType.Transfer => ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct),
            AssignmentServiceType.Package => ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct),
            AssignmentServiceType.Assistance => ResolveRequiredIdAsync<AssistanceBooking>(publicIdOrLegacyId, ct),
            _ => throw new ArgumentException("Ese tipo de servicio no es válido."),
        };

    /// <summary>
    /// Junta los servicios VIVOS (no cancelados) de los 5 tipos tipados que pertenecen al grupo pedido.
    /// Trae primero todo lo que tenga <c>OptionGroup</c> cargado en la reserva (consulta chica: la
    /// inmensa mayoría de las reservas no usa opciones) y filtra por nombre de grupo EN MEMORIA con el
    /// mismo criterio case-insensitive que <see cref="OptionGroupRules"/> — así nunca se desalinea con
    /// la regla que decide "ambiguo" en otros lugares.
    /// </summary>
    private async Task<List<OptionGroupMember>> LoadOptionGroupMembersAsync(int reservaId, string normalizedGroup, CancellationToken ct)
    {
        var members = new List<OptionGroupMember>();

        var hotels = await _db.Set<HotelBooking>().AsNoTracking()
            .Where(h => h.ReservaId == reservaId && h.OptionGroup != null)
            .Select(h => new { h.Id, h.PublicId, h.OptionGroup, h.Status, h.HotelName, h.SalePrice, h.NetCost, h.Currency })
            .ToListAsync(ct);
        members.AddRange(hotels
            .Where(h => BelongsToGroup(h.OptionGroup, normalizedGroup) && IsLive(WorkflowStatusHelper.MapGenericStatus(h.Status)))
            .Select(h => new OptionGroupMember(
                AssignmentServiceType.Hotel, h.Id, h.PublicId, ServiceLabelHelper.WithPrefix("Hotel", h.HotelName, "sin nombre"),
                h.SalePrice, h.NetCost, h.Currency)));

        var flights = await _db.Set<FlightSegment>().AsNoTracking()
            .Where(f => f.ReservaId == reservaId && f.OptionGroup != null)
            .Select(f => new { f.Id, f.PublicId, f.OptionGroup, f.Status, f.ProductName, f.AirlineCode, f.FlightNumber, f.SalePrice, f.NetCost, f.Currency })
            .ToListAsync(ct);
        members.AddRange(flights
            .Where(f => BelongsToGroup(f.OptionGroup, normalizedGroup) && IsLive(WorkflowStatusHelper.MapFlightStatus(f.Status)))
            .Select(f => new OptionGroupMember(
                AssignmentServiceType.Flight, f.Id, f.PublicId,
                $"Vuelo {ServiceDisplayName.ForFlight(f.ProductName, f.AirlineCode, f.FlightNumber)}",
                f.SalePrice, f.NetCost, f.Currency)));

        var transfers = await _db.Set<TransferBooking>().AsNoTracking()
            .Where(t => t.ReservaId == reservaId && t.OptionGroup != null)
            .Select(t => new { t.Id, t.PublicId, t.OptionGroup, t.Status, t.ProductName, t.PickupLocation, t.DropoffLocation, t.VehicleType, t.SalePrice, t.NetCost, t.Currency })
            .ToListAsync(ct);
        members.AddRange(transfers
            .Where(t => BelongsToGroup(t.OptionGroup, normalizedGroup) && IsLive(WorkflowStatusHelper.MapGenericStatus(t.Status)))
            .Select(t => new OptionGroupMember(
                AssignmentServiceType.Transfer, t.Id, t.PublicId,
                ServiceLabelHelper.WithPrefix(
                    "Traslado", ServiceDisplayName.ForTransfer(t.ProductName, t.PickupLocation, t.DropoffLocation, t.VehicleType), "sin nombre"),
                t.SalePrice, t.NetCost, t.Currency)));

        var packages = await _db.Set<PackageBooking>().AsNoTracking()
            .Where(p => p.ReservaId == reservaId && p.OptionGroup != null)
            .Select(p => new { p.Id, p.PublicId, p.OptionGroup, p.Status, p.PackageName, p.Destination, p.SalePrice, p.NetCost, p.Currency })
            .ToListAsync(ct);
        members.AddRange(packages
            .Where(p => BelongsToGroup(p.OptionGroup, normalizedGroup) && IsLive(WorkflowStatusHelper.MapGenericStatus(p.Status)))
            .Select(p => new OptionGroupMember(
                AssignmentServiceType.Package, p.Id, p.PublicId,
                ServiceLabelHelper.WithPrefix("Paquete", ServiceDisplayName.ForPackage(p.PackageName, p.Destination), "sin nombre"),
                p.SalePrice, p.NetCost, p.Currency)));

        var assistances = await _db.Set<AssistanceBooking>().AsNoTracking()
            .Where(a => a.ReservaId == reservaId && a.OptionGroup != null)
            .Select(a => new { a.Id, a.PublicId, a.OptionGroup, a.Status, a.PlanType, a.SalePrice, a.NetCost, a.Currency })
            .ToListAsync(ct);
        members.AddRange(assistances
            .Where(a => BelongsToGroup(a.OptionGroup, normalizedGroup) && IsLive(WorkflowStatusHelper.MapGenericStatus(a.Status)))
            .Select(a => new OptionGroupMember(
                AssignmentServiceType.Assistance, a.Id, a.PublicId, ServiceLabelHelper.WithPrefix("Asistencia", a.PlanType, "sin nombre"),
                a.SalePrice, a.NetCost, a.Currency)));

        return members;
    }

    private static bool BelongsToGroup(string? rawOptionGroup, string normalizedGroup)
        => string.Equals(OptionGroupRules.Normalize(rawOptionGroup), normalizedGroup, StringComparison.OrdinalIgnoreCase);

    private static bool IsLive(string workflowStatus) => WorkflowStatusHelper.CountsForQuotedTotal(workflowStatus);

    /// <summary>Borra UN miembro perdedor reusando el Delete*Async de su tipo — mismos guards y misma limpieza de asignaciones que borrar el servicio a mano.</summary>
    private Task DeleteOptionGroupMemberAsync(int reservaId, OptionGroupMember member, CancellationToken ct)
        => member.ServiceType switch
        {
            AssignmentServiceType.Hotel => DeleteHotelAsync(reservaId, member.Id, ct),
            AssignmentServiceType.Flight => DeleteFlightAsync(reservaId, member.Id, ct),
            AssignmentServiceType.Transfer => DeleteTransferAsync(reservaId, member.Id, ct),
            AssignmentServiceType.Package => DeletePackageAsync(reservaId, member.Id, ct),
            AssignmentServiceType.Assistance => DeleteAssistanceAsync(reservaId, member.Id, ct),
            _ => throw new ArgumentException("Ese tipo de servicio no es válido."),
        };

    /// <summary>
    /// Rastro auditable de la resolución (PR-12: nada se borra sin rastro). Un solo evento por
    /// resolución (no uno por servicio borrado), con el nombre del grupo, el ganador y la lista de
    /// perdedores.
    ///
    /// <para><b>Fix B2 (review de seguridad, 2026-08-12)</b>: el borrado es FÍSICO (no soft-delete) — sin
    /// el <c>PublicId</c> y el snapshot de plata (SalePrice/NetCost/Currency) de cada perdedor, el rastro
    /// de auditoría quedaba mudo sobre CUÁNTO valía lo que se borró y no permitía ubicar la fila exacta
    /// si alguna vez hace falta reconstruir el historial. Esto viaja SOLO en el <c>details</c> del audit
    /// log (visible para quien audite, no en la respuesta de la API — <see cref="RemovedOptionGroupServiceDto"/>
    /// no lleva montos).</para>
    /// </summary>
    private async Task WriteResolveOptionGroupAuditAsync(
        int reservaId, string optionGroup, OptionGroupMember winner, IReadOnlyList<OptionGroupMember> losers,
        string? userId, string? userName, CancellationToken ct)
    {
        if (_auditService is null) return;

        var reserva = await _db.Set<Reserva>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            optionGroup,
            winner = new { winner.ServiceType, winner.PublicId, winner.Label },
            removed = losers.Select(l => new
            {
                l.ServiceType,
                l.PublicId,
                l.Label,
                l.SalePrice,
                l.NetCost,
                Currency = TravelApi.Domain.Entities.Monedas.Normalizar(l.Currency)
            }).ToList()
        });

        await _auditService.LogBusinessEventAsync(
            action: AuditActions.OptionGroupResolved,
            entityName: AuditActions.ReservaEntityName,
            entityId: reserva?.PublicId.ToString() ?? reservaId.ToString(),
            details: details,
            userId: userId ?? string.Empty,
            userName: userName,
            ct: ct);
    }

    // ============================================================================================
    // Fix B1(a) (review de seguridad, 2026-08-12): las opciones A/B/C SOLO se definen en Presupuesto.
    // ============================================================================================

    /// <summary>
    /// Mensaje único para el usuario cuando intenta setear/cambiar un <c>OptionGroup</c> fuera de
    /// Presupuesto (Cotización/Presupuesto). Mismo texto en los 5 tipos de servicio.
    /// </summary>
    public const string OptionGroupOnlyDuringPresupuestoMessage =
        "Las opciones se definen mientras es presupuesto; elegí una y cargá la definitiva.";

    /// <summary>
    /// Version PURA (sin DB): la usan los 5 <c>Create*WithCatalogAsync</c>, que YA tienen la <c>Reserva</c>
    /// cargada en memoria (<c>file.Status</c>) — evita una query de mas por cada alta. No-op si
    /// <paramref name="requestedOptionGroup"/> viene vacio/null: eso significa "no estoy tocando el
    /// grupo" (alta de un servicio normal, sin opciones).
    ///
    /// <para><b>Por que aca y no re-validar ambiguedad en cada lugar</b>: la reserva ya paso por el guard
    /// "el cliente acepto" (<c>ReservaService.EnsureNoAmbiguousOptionGroupsAsync</c>), que solo corre UNA
    /// vez al avanzar de Presupuesto. Si se permitiera cargar/cambiar un grupo DESPUES de esa transicion,
    /// un grupo ambiguo podria reaparecer sin que nada lo vuelva a chequear antes de que el motor
    /// automatico confirme la reserva — la venta quedaria escondida (ninguna opcion del grupo suma a
    /// TotalSale, ver <see cref="TravelApi.Domain.Reservations.ReservaMoneyCalculator"/>). Prohibir el
    /// candado de raiz es mas simple y mas coherente con el negocio: opciones = decision de presupuesto,
    /// una vez que el cliente acepto no hay "opciones", hay UNA venta.</para>
    /// </summary>
    private static void EnsureOptionGroupOnlySetDuringPresupuesto(string? reservaStatus, string? requestedOptionGroup)
    {
        var normalized = OptionGroupRules.Normalize(requestedOptionGroup);
        if (normalized is null) return; // no esta seteando ningun grupo: nada que validar.

        bool isPresupuestoStage =
            string.Equals(reservaStatus, EstadoReserva.Quotation, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reservaStatus, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase);

        if (!isPresupuestoStage)
        {
            throw new ArgumentException(OptionGroupOnlyDuringPresupuestoMessage);
        }
    }

    /// <summary>
    /// Version con DB: la usan los 5 <c>Update*Async</c> (no tienen el <c>Status</c> de la reserva en
    /// memoria en el punto donde se aplica el anti-clobber de <c>OptionGroup</c>). Delega la regla en la
    /// version pura de arriba.
    /// </summary>
    private async Task EnsureOptionGroupOnlySetDuringPresupuestoAsync(int reservaId, string? requestedOptionGroup, CancellationToken ct)
    {
        var normalized = OptionGroupRules.Normalize(requestedOptionGroup);
        if (normalized is null) return; // no esta seteando ningun grupo: nada que validar (evita la query).

        var status = await _db.Set<Reserva>().AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => r.Status)
            .FirstOrDefaultAsync(ct);

        EnsureOptionGroupOnlySetDuringPresupuesto(status, requestedOptionGroup);
    }

    // ============================================================================================
    // Micro-ronda review (2026-08-12): guard de longitud amistoso. Sin esto, un OptionGroup/OptionLabel
    // mas largo que la columna (varchar(60)/varchar(5)) revienta con un error crudo de Npgsql ("value
    // too long for type character varying(60)") -> 500 generico. Mismo criterio que EnsureStarRatingInRange:
    // el motor frena ANTES de llegar a la base, con un mensaje pensado para el usuario.
    // ============================================================================================

    private const int MaxOptionGroupLength = 60; // FlightSegment.OptionGroup y hermanos: [MaxLength(60)]
    private const int MaxOptionLabelLength = 5; // FlightSegment.OptionLabel y hermanos: [MaxLength(5)]

    public const string OptionGroupTooLongMessage = "El nombre del grupo de opciones es demasiado largo.";
    public const string OptionLabelTooLongMessage = "La etiqueta de la opción es demasiado larga.";

    /// <summary>
    /// Rechaza un <c>OptionGroup</c>/<c>OptionLabel</c> mas largo que la columna. Vale para los 10
    /// write-paths (5 Create + 5 Update) de los 5 tipos de servicio — mismo texto en todos.
    /// </summary>
    private static void EnsureOptionGroupFieldLengths(string? optionGroup, string? optionLabel)
    {
        if (optionGroup != null && optionGroup.Trim().Length > MaxOptionGroupLength)
        {
            throw new ArgumentException(OptionGroupTooLongMessage);
        }

        if (optionLabel != null && optionLabel.Trim().Length > MaxOptionLabelLength)
        {
            throw new ArgumentException(OptionLabelTooLongMessage);
        }
    }
}
