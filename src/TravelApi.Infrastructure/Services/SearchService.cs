using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// B1.15 Fase 2a (FIX 3): el search global ahora respeta scope por permiso. Antes
/// devolvia toda reserva, todo payment y todo customer al primer autenticado, lo
/// que rompia la promesa de filter mine en el resto del API.
///
/// Reglas:
///  - Reservas: si el user NO tiene <c>reservas.view_all</c> ni rol Admin, filtramos
///    por <c>Reserva.ResponsibleUserId == currentUserId</c>. Ademas de por numero/nombre de la
///    reserva y nombre del titular, tambien se busca por el nombre del SERVICIO (hotel, vuelo,
///    traslado, paquete, asistencia o generico) — H18, barrido E2E 2026-07-25 — devolviendo la
///    reserva que lo contiene, con el MISMO scope de arriba.
///  - Payments: si el user NO tiene <c>cobranzas.view_all</c> ni rol Admin, filtramos
///    por la reserva contenedora (<c>Payment.Reserva.ResponsibleUserId == currentUserId</c>).
///    Adicionalmente, si NO tiene <c>cobranzas.view</c> base, no devolvemos payments.
///  - Customers: si el user NO tiene <c>clientes.view</c>, no devolvemos customers.
///
/// El user ya paso el gate <c>reservas.view</c> a nivel controller; este filtro
/// ajusta el alcance de cada coleccion segun los permisos efectivos.
/// </summary>
public class SearchService : ISearchService
{
    private readonly AppDbContext _dbContext;
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public SearchService(
        AppDbContext dbContext,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _dbContext = dbContext;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<SearchResultsResponse> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            // Consulta vacia: no hay recorte por permisos que informar, todas las secciones en false.
            return new SearchResultsResponse(string.Empty, [], [], [], new SearchScopeInfo(false, false, false, false));
        }

        var normalized = query.Trim().ToLowerInvariant();

        // Resolver scope segun permisos del user actual. El bypass por rol "Admin" (verificado en la
        // revision de la Tanda 3, 2026-07-23) es el MISMO patron canonico que usa el resto del sistema
        // (PermissionAuthorizationHandler a nivel framework, y ReservaService.GetReservasWithScopeAsync
        // como service equivalente): no es una resolucion divergente, es la fuente unica de "admin bypassea
        // todo" documentada ahi. No se cambia a proposito.
        var httpUser = _httpContextAccessor?.HttpContext?.User;
        var currentUserId = httpUser?.FindFirstValue(ClaimTypes.NameIdentifier);
        var isAdmin = httpUser?.IsInRole("Admin") ?? false;

        var perms = (_permissionResolver is null || string.IsNullOrEmpty(currentUserId))
            ? null
            : await _permissionResolver.GetPermissionsAsync(currentUserId, cancellationToken);

        var hasReservasViewAll = isAdmin || (perms?.Contains(Permissions.ReservasViewAll) ?? false);
        var hasCobranzasView = isAdmin || (perms?.Contains(Permissions.CobranzasView) ?? false);
        var hasCobranzasViewAll = isAdmin || (perms?.Contains(Permissions.CobranzasViewAll) ?? false);
        var hasClientesView = isAdmin || (perms?.Contains(Permissions.ClientesView) ?? false);

        // Customers: si no tiene clientes.view, devolvemos lista vacia (no aplica search).
        // El controller ya valido reservas.view como gate base; clientes es independiente.
        var customers = hasClientesView
            ? await _dbContext.Customers
                .AsNoTracking()
                .Where(c => c.FullName.ToLower().Contains(normalized) ||
                    (c.Email != null && c.Email.ToLower().Contains(normalized)) ||
                    (c.Phone != null && c.Phone.ToLower().Contains(normalized)))
                .OrderBy(c => c.FullName)
                .Take(5)
                .Select(c => new CustomerSearchResult(c.PublicId, c.FullName, c.Email, c.Phone))
                .ToListAsync(cancellationToken)
            : new List<CustomerSearchResult>();

        // Reservas: si no tiene view_all, filtrar por owner.
        //
        // H18 (barrido E2E 2026-07-25, decision firmada de Gaston): el buscador global TAMBIEN busca
        // por el nombre de un SERVICIO (hotel, vuelo, traslado, paquete, asistencia o el generico) y
        // devuelve la RESERVA que lo contiene — nunca abre el servicio suelto, el buscador global
        // siempre lista reservas. Al agregar estas condiciones DENTRO del mismo Where de reservasQuery
        // (en vez de armar una consulta aparte), el scope de permisos que se aplica dos lineas mas abajo
        // (owner-only sin reservas.view_all) alcanza AUTOMATICAMENTE a los matches por servicio: es
        // literalmente el mismo filtro de reserva, con mas condiciones OR adentro (T-10, mismo scope).
        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Include(f => f.Payer)
            .Where(f => f.NumeroReserva.ToLower().Contains(normalized) ||
                f.Name.ToLower().Contains(normalized) ||
                (f.Payer != null && f.Payer.FullName.ToLower().Contains(normalized)) ||
                f.HotelBookings.Any(h => h.HotelName.ToLower().Contains(normalized)) ||
                f.FlightSegments.Any(v => v.ProductName != null && v.ProductName.ToLower().Contains(normalized)) ||
                f.TransferBookings.Any(t => t.ProductName != null && t.ProductName.ToLower().Contains(normalized)) ||
                f.PackageBookings.Any(p => p.PackageName.ToLower().Contains(normalized)) ||
                f.AssistanceBookings.Any(a => a.PlanType != null && a.PlanType.ToLower().Contains(normalized)) ||
                f.Servicios.Any(s => s.Description != null && s.Description.ToLower().Contains(normalized)));

        if (!hasReservasViewAll)
        {
            // Sin user resoluble => sentinel imposible (no devolver nada).
            var ownerFilter = string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId;
            reservasQuery = reservasQuery.Where(f => f.ResponsibleUserId == ownerFilter);
        }

        var reservas = await reservasQuery
            .OrderByDescending(f => f.CreatedAt)
            .Take(5)
            .Select(f => new ReservaSearchResult(f.PublicId, f.NumeroReserva, f.Name, f.Status, f.Payer != null ? f.Payer.FullName : null))
            .ToListAsync(cancellationToken);

        // Payments: requiere cobranzas.view base; si no tiene cobranzas.view_all, filtrar
        // por reserva.ResponsibleUserId.
        List<PaymentSearchResult> payments;
        if (!hasCobranzasView)
        {
            payments = new List<PaymentSearchResult>();
        }
        else
        {
            var paymentsQuery = _dbContext.Payments
                .AsNoTracking()
                .Include(p => p.Reserva)
                // OJO: Payment.Status es string; el .ToString() que habia aca era un no-op que
                // Npgsql NO puede traducir a SQL ("Translation of method 'object.ToString' failed")
                // y tiraba 500 en PROD para CUALQUIER busqueda (hotfix 2026-07-25). InMemory lo
                // toleraba, por eso los tests unit daban verde: la red real es el test de
                // integracion contra Postgres.
                .Where(p => p.Method.ToLower().Contains(normalized) ||
                    p.Status.ToLower().Contains(normalized));

            if (!hasCobranzasViewAll)
            {
                var ownerFilter = string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId;
                paymentsQuery = paymentsQuery.Where(p => p.Reserva != null && p.Reserva.ResponsibleUserId == ownerFilter);
            }

            payments = await paymentsQuery
                .OrderByDescending(p => p.PaidAt)
                .Take(5)
                .Select(p => new PaymentSearchResult(
                    p.PublicId,
                    p.Amount,
                    p.Status,
                    p.Method,
                    p.Reserva != null ? p.Reserva.NumeroReserva : null))
                .ToListAsync(cancellationToken);
        }

        // FIX #39: señal estructurada por seccion (P-8/T-13) — el front decide como avisar
        // "esto es lo tuyo, puede haber mas" sin tener que adivinar a partir de una lista vacia.
        var scope = new SearchScopeInfo(
            CustomersHidden: !hasClientesView,
            ReservasScopedToOwn: !hasReservasViewAll,
            PaymentsHidden: !hasCobranzasView,
            PaymentsScopedToOwn: hasCobranzasView && !hasCobranzasViewAll);

        return new SearchResultsResponse(query, customers, reservas, payments, scope);
    }
}
