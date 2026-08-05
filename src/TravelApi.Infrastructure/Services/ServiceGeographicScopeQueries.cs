using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Ámbito geográfico de los servicios de una reserva, contando SOLO los servicios VIVOS (no cancelados).
/// Fix B2 del review de backend (2026-08-05, obra "gate ámbito"): el gate de pasaporte
/// (<see cref="TravelApi.Domain.Helpers.PassportAlertScopeGate"/>) y el chip de menores
/// (<see cref="TravelApi.Domain.Helpers.MinorTravelAuthorizationRules"/>) son consumidores NUEVOS que
/// tienen que mirar el estado DERIVADO sobre las líneas vivas de la reserva — igual que
/// <see cref="UpcomingStartCalculator"/> (F-2). Sin esto:
/// <list type="bullet">
///   <item>un servicio Internacional CANCELADO dejaba el chip de menores prendido para siempre;</item>
///   <item>un ÚNICO servicio Nacional CANCELADO apagaba el aviso de pasaporte (anti-conservador: la
///   reserva en verdad no tiene ningún tramo vivo definido, así que el aviso debería seguir sonando).</item>
/// </list>
///
/// <para><b>OJO — a propósito NO se usa para el semáforo de DNI</b> (<c>ReservaService.ResolveServiceGeographicScopeContextAsync</c>,
/// campo <c>HasDomesticService</c>): ese consumidor ya estaba deployado en PROD mirando TODOS los
/// servicios sin filtrar por estado, y este fix no lo toca (compat con lo firmado 2026-08-03) — el
/// semáforo de DNI queda "ciego" al estado del servicio, es una inconsistencia deliberada y documentada,
/// no un descuido.</para>
/// </summary>
public readonly record struct LiveServiceGeographicScope(
    bool HasAnyService,
    bool HasInternationalService,
    bool HasUndefinedScopeService)
{
    /// <summary>Reserva sin ningún servicio vivo con ámbito (o directamente sin servicios): el gate de
    /// pasaporte queda ABIERTO igual (comportamiento conservador), y el chip de menores no se prende.</summary>
    public static readonly LiveServiceGeographicScope Empty = new(
        HasAnyService: false, HasInternationalService: false, HasUndefinedScopeService: false);
}

public static class ServiceGeographicScopeQueries
{
    /// <summary>
    /// Resuelve el ámbito geográfico VIVO de un LOTE de reservas en dos consultas (una por tabla de
    /// servicio), sin importar cuántas reservas se pidan — evita el N+1 de consultar reserva por reserva
    /// (lo usa <c>AlertService.ComputePassportExpiriesAsync</c>, que ya arma sus <c>reservaIds</c> en
    /// lote). El filtro de "vivo" es EXACTAMENTE el mismo que usa <see cref="UpcomingStartCalculator"/>
    /// para "primer inicio" (mismo criterio F-2 en todo el repo, no se inventa uno nuevo acá).
    /// </summary>
    public static async Task<Dictionary<int, LiveServiceGeographicScope>> ResolveLiveScopeForReservasAsync(
        AppDbContext db, IReadOnlyCollection<int> reservaIds, CancellationToken ct)
    {
        if (reservaIds.Count == 0)
        {
            return new Dictionary<int, LiveServiceGeographicScope>();
        }

        // Servicio genérico: mismo filtro "vivo" que UpcomingStartCalculator (Status != "Cancelado").
        var genericScopes = await db.Servicios
            .Where(s => s.ReservaId != null && reservaIds.Contains(s.ReservaId.Value) && s.Status != "Cancelado")
            .Select(s => new { ReservaId = s.ReservaId!.Value, s.GeographicScope })
            .ToListAsync(ct);

        // Vuelo real: mismo filtro "vivo" que UpcomingStartCalculator (UN/UC/HX/NO = cancelado por IATA).
        var flightScopes = await db.FlightSegments
            .Where(f => reservaIds.Contains(f.ReservaId)
                        && f.Status != "UN" && f.Status != "UC" && f.Status != "HX" && f.Status != "NO")
            .Select(f => new { f.ReservaId, f.GeographicScope })
            .ToListAsync(ct);

        var hasAny = new HashSet<int>();
        var hasInternational = new HashSet<int>();
        var hasUndefined = new HashSet<int>();

        foreach (var row in genericScopes)
        {
            AccumulateScope(row.ReservaId, row.GeographicScope, hasAny, hasInternational, hasUndefined);
        }
        foreach (var row in flightScopes)
        {
            AccumulateScope(row.ReservaId, row.GeographicScope, hasAny, hasInternational, hasUndefined);
        }

        var result = new Dictionary<int, LiveServiceGeographicScope>();
        foreach (var reservaId in reservaIds)
        {
            result[reservaId] = new LiveServiceGeographicScope(
                HasAnyService: hasAny.Contains(reservaId),
                HasInternationalService: hasInternational.Contains(reservaId),
                HasUndefinedScopeService: hasUndefined.Contains(reservaId));
        }

        return result;
    }

    private static void AccumulateScope(
        int reservaId,
        ServiceGeographicScope scope,
        HashSet<int> hasAny,
        HashSet<int> hasInternational,
        HashSet<int> hasUndefined)
    {
        hasAny.Add(reservaId);
        if (scope == ServiceGeographicScope.International)
        {
            hasInternational.Add(reservaId);
        }
        if (scope == ServiceGeographicScope.Undefined)
        {
            hasUndefined.Add(reservaId);
        }
    }
}
