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
///    por <c>Reserva.ResponsibleUserId == currentUserId</c>.
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
            return new SearchResultsResponse(string.Empty, [], [], []);
        }

        var normalized = query.Trim().ToLowerInvariant();

        // Resolver scope segun permisos del user actual.
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
        var reservasQuery = _dbContext.Reservas
            .AsNoTracking()
            .Include(f => f.Payer)
            .Where(f => f.NumeroReserva.ToLower().Contains(normalized) ||
                f.Name.ToLower().Contains(normalized) ||
                (f.Payer != null && f.Payer.FullName.ToLower().Contains(normalized)));

        if (!hasReservasViewAll)
        {
            // Sin user resoluble => sentinel imposible (no devolver nada).
            var ownerFilter = string.IsNullOrEmpty(currentUserId) ? "__no_user__" : currentUserId;
            reservasQuery = reservasQuery.Where(f => f.ResponsibleUserId == ownerFilter);
        }

        var reservas = await reservasQuery
            .OrderByDescending(f => f.CreatedAt)
            .Take(5)
            .Select(f => new ReservaSearchResult(f.PublicId, f.NumeroReserva, f.Name, f.Status.ToString(), f.Payer != null ? f.Payer.FullName : null))
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
                .Where(p => p.Method.ToLower().Contains(normalized) ||
                    p.Status.ToString().ToLower().Contains(normalized));

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
                    p.Status.ToString(),
                    p.Method,
                    p.Reserva != null ? p.Reserva.NumeroReserva : null))
                .ToListAsync(cancellationToken);
        }

        return new SearchResultsResponse(query, customers, reservas, payments);
    }
}
