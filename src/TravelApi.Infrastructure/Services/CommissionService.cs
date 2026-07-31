using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class CommissionService : ICommissionService
{
    private readonly AppDbContext _dbContext;

    public CommissionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<object>> GetAllRulesAsync(CancellationToken cancellationToken)
    {
        var rules = await _dbContext.CommissionRules
            .Include(r => r.Supplier)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.Supplier != null ? r.Supplier.Name : "")
            .ToListAsync(cancellationToken);

        return rules.Select(r => new
        {
            r.Id,
            SupplierPublicId = r.Supplier != null ? (Guid?)r.Supplier.PublicId : null,
            SupplierName = r.Supplier?.Name,
            r.ServiceType,
            r.CommissionPercent,
            r.Priority,
            r.IsActive,
            r.Description,
            r.CreatedAt
        });
    }

    public async Task<CommissionRuleDto> CreateRuleAsync(CreateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        // Obra "cada campo acepta solo lo que va en ese campo" (2026-07-31, TANDA 1): el porcentaje es
        // plata (con el se devenga la comision del vendedor), asi que se frena antes de tocar la base.
        EnsureCommissionPercentIsValid(request.CommissionPercent);

        int? supplierId = null;
        if (!string.IsNullOrWhiteSpace(request.SupplierId))
        {
            supplierId = await _dbContext.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(request.SupplierId, cancellationToken);

            if (!supplierId.HasValue)
                throw new ArgumentException("Proveedor no encontrado.");
        }

        // Verificar si ya existe una regla igual
        var existing = await _dbContext.CommissionRules
            .FirstOrDefaultAsync(r => 
                r.SupplierId == supplierId && 
                r.ServiceType == request.ServiceType &&
                r.IsActive, cancellationToken);

        if (existing != null)
            throw new ArgumentException("Ya existe una regla con ese proveedor y tipo de servicio");

        var rule = new CommissionRule
        {
            SupplierId = supplierId,
            ServiceType = request.ServiceType,
            CommissionPercent = request.CommissionPercent,
            Priority = request.Priority,
            Description = request.Description,
            IsActive = true
        };

        _dbContext.CommissionRules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return await BuildRuleDtoAsync(rule, cancellationToken);
    }

    public async Task<CommissionRuleDto?> UpdateRuleAsync(int id, UpdateCommissionRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return null;

        // Obra "cada campo acepta solo lo que va en ese campo" (2026-07-31, TANDA 1). Aca NO se aplica el
        // criterio "solo si cambia" de las fichas: el porcentaje llega siempre como numero desde el unico
        // formulario que edita reglas, no hay dato legacy de texto libre que pueda quedar trabado.
        EnsureCommissionPercentIsValid(request.CommissionPercent);

        rule.CommissionPercent = request.CommissionPercent;
        rule.Description = request.Description;
        rule.Priority = request.Priority;
        rule.IsActive = request.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return await BuildRuleDtoAsync(rule, cancellationToken);
    }

    /// <summary>
    /// Arma la respuesta de una regla con los MISMOS campos que devuelve el listado
    /// (<see cref="GetAllRulesAsync"/>), para que la pantalla vea siempre la misma forma.
    ///
    /// <para>El proveedor se busca aparte porque el alta/edicion no lo trae cargado (no hace falta un
    /// Include para grabar). Es una consulta puntual por clave primaria, solo cuando la regla tiene
    /// proveedor: sin proveedor no se consulta nada.</para>
    /// </summary>
    private async Task<CommissionRuleDto> BuildRuleDtoAsync(CommissionRule rule, CancellationToken cancellationToken)
    {
        Guid? supplierPublicId = null;
        string? supplierName = null;

        if (rule.SupplierId.HasValue)
        {
            var supplier = await _dbContext.Suppliers
                .AsNoTracking()
                .Where(s => s.Id == rule.SupplierId.Value)
                .Select(s => new { s.PublicId, s.Name })
                .FirstOrDefaultAsync(cancellationToken);

            supplierPublicId = supplier?.PublicId;
            supplierName = supplier?.Name;
        }

        return new CommissionRuleDto
        {
            Id = rule.Id,
            SupplierPublicId = supplierPublicId,
            SupplierName = supplierName,
            ServiceType = rule.ServiceType,
            CommissionPercent = rule.CommissionPercent,
            Priority = rule.Priority,
            IsActive = rule.IsActive,
            Description = rule.Description,
            CreatedAt = rule.CreatedAt
        };
    }

    /// <summary>
    /// Obra "cada campo acepta solo lo que va en ese campo" (2026-07-31, TANDA 1): el porcentaje de una
    /// regla de comision tiene que estar entre 0 y 100.
    ///
    /// <para><b>ValidationException y no ArgumentException a proposito</b>: el alta de regla en
    /// <c>CommissionsController</c> atrapa <c>ArgumentException</c> y la reemplaza por un generico
    /// ("No se pudo crear la regla de comision."), asi que el admin nunca leeria el motivo. La
    /// <c>ValidationException</c> de DataAnnotations pasa de largo ese catch y el
    /// <c>GlobalExceptionHandler</c> la traduce a un 400 con el mensaje real — el mismo camino que ya usa
    /// la configuracion de la agencia.</para>
    /// </summary>
    private static void EnsureCommissionPercentIsValid(decimal commissionPercent)
    {
        if (!CommissionPercentValidator.IsValid(commissionPercent))
        {
            throw new ValidationException(CommissionPercentValidator.InvalidPercentMessage);
        }
    }

    public async Task<bool> DeleteRuleAsync(int id, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules.FindAsync(new object[] { id }, cancellationToken);
        if (rule == null)
            return false;

        _dbContext.CommissionRules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<decimal> CalculateCommissionAsync(int? supplierId, string? serviceType, CancellationToken cancellationToken)
    {
        var rule = await _dbContext.CommissionRules
            .Where(r => r.IsActive)
            .Where(r => 
                // Regla exacta (proveedor + servicio)
                (r.SupplierId == supplierId && r.ServiceType == serviceType) ||
                // Solo proveedor
                (r.SupplierId == supplierId && r.ServiceType == null) ||
                // Solo servicio
                (r.SupplierId == null && r.ServiceType == serviceType) ||
                // Default (aplica a todos)
                (r.SupplierId == null && r.ServiceType == null)
            )
            // ADR-026 (M3 review): desempate estable por Id. Con dos reglas de igual Priority,
            // Postgres no garantiza orden sin ORDER BY secundario -> la regla elegida (y por ende
            // la comision que se persiste como plata) seria no determinista. El mismo desempate va
            // en el espejo en memoria de CommissionAccrualPersister para que el % sea reproducible.
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (rule == null)
        {
            var settings = await _dbContext.AgencySettings.FirstOrDefaultAsync(cancellationToken);
            return settings?.DefaultCommissionPercent ?? 10m;
        }

        return rule.CommissionPercent;
    }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (hallazgo #1): lista paginada de comisiones de vendedor devengadas. Solo
    /// lectura (las filas las escribe <c>CommissionAccrualPersister</c> en el recalculo de plata). Filtra
    /// por vendedor / estado / periodo de devengo. Join a la reserva para mostrar numero + publicId.
    /// </summary>
    public async Task<PagedResponse<CommissionAccrualDto>> GetAccrualsAsync(
        CommissionAccrualsQuery query, CancellationToken cancellationToken)
    {
        // Base: todas las comisiones, con la reserva para exponer su numero/publicId (un solo JOIN, sin N+1).
        var accruals = _dbContext.CommissionAccruals
            .AsNoTracking()
            .Include(accrual => accrual.Reserva)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.SellerUserId))
            accruals = accruals.Where(accrual => accrual.SellerUserId == query.SellerUserId);

        if (!string.IsNullOrWhiteSpace(query.Status))
            accruals = accruals.Where(accrual => accrual.Status == query.Status);

        if (query.From.HasValue)
            accruals = accruals.Where(accrual => accrual.CreatedAt >= query.From.Value);

        if (query.To.HasValue)
            accruals = accruals.Where(accrual => accrual.CreatedAt <= query.To.Value);

        int totalCount = await accruals.CountAsync(cancellationToken);

        int page = query.GetNormalizedPage();
        int pageSize = query.GetNormalizedPageSize();
        bool descending = query.IsSortDescending();

        // Orden: por defecto por fecha de devengo (mas reciente arriba). Tie-break por Id para que la
        // paginacion sea estable.
        accruals = descending
            ? accruals.OrderByDescending(accrual => accrual.CreatedAt).ThenByDescending(accrual => accrual.Id)
            : accruals.OrderBy(accrual => accrual.CreatedAt).ThenBy(accrual => accrual.Id);

        var pageRows = await accruals
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(accrual => new CommissionAccrualDto
            {
                PublicId = accrual.PublicId,
                SellerUserId = accrual.SellerUserId,
                SellerName = accrual.SellerName,
                ReservaPublicId = accrual.Reserva != null ? accrual.Reserva.PublicId : Guid.Empty,
                ReservaNumber = accrual.Reserva != null ? accrual.Reserva.NumeroReserva : string.Empty,
                Currency = accrual.Currency,
                Amount = accrual.Amount,
                RatePercent = accrual.RatePercent,
                Status = accrual.Status,
                CreatedAt = accrual.CreatedAt,
                UpdatedAt = accrual.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        return PagedResponse<CommissionAccrualDto>.Create(pageRows, page, pageSize, totalCount);
    }

    /// <summary>
    /// Auditoria ERP 2026-06-13 (decision del dueño): resumen mensual de comisiones por vendedor. Filtra los
    /// devengos cuyo CreatedAt cae en el mes pedido y los agrupa por vendedor + moneda, sumando montos. Solo
    /// lectura; admin-only (lo gatea el controller). Devuelve un resumen vacio (sin vendedores) si no hubo
    /// devengo en el mes.
    /// </summary>
    public async Task<CommissionMonthlySummaryDto> GetMonthlySummaryAsync(
        int year, int month, CancellationToken cancellationToken)
    {
        // Validamos el periodo antes de armar la ventana: un mes fuera de 1..12 o un año disparatado es un
        // pedido invalido del cliente, no un resultado vacio (el controller mapea ArgumentException a 400).
        if (month < 1 || month > 12)
            throw new ArgumentException("El mes debe estar entre 1 y 12.", nameof(month));
        if (year < 2000 || year > 2100)
            throw new ArgumentException("El año esta fuera de rango.", nameof(year));

        // Ventana [primero del mes, primero del mes siguiente) en UTC. CreatedAt se persiste en UTC, asi que
        // comparamos contra limites UTC. Usamos rango half-open (>= inicio, < fin) para no depender de la
        // precision del timestamp ni arriesgar incluir/excluir el ultimo instante del mes.
        var monthStartUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var nextMonthStartUtc = monthStartUtc.AddMonths(1);

        // Traemos solo lo necesario (vendedor, moneda, monto) de las filas del mes con devengo positivo.
        // Las filas en 0 (tope cero por cancelacion / saldo positivo) NO se cuentan: no son comision real.
        var rows = await _dbContext.CommissionAccruals
            .AsNoTracking()
            .Where(accrual => accrual.CreatedAt >= monthStartUtc
                && accrual.CreatedAt < nextMonthStartUtc
                && accrual.Amount > 0m)
            .Select(accrual => new
            {
                accrual.SellerUserId,
                accrual.SellerName,
                accrual.Currency,
                accrual.Amount,
            })
            .ToListAsync(cancellationToken);

        // Agrupamos en memoria (el set por mes es chico): primero por vendedor, dentro por moneda.
        var sellers = new List<CommissionSellerMonthlyTotalDto>();
        foreach (var sellerGroup in rows.GroupBy(row => row.SellerUserId))
        {
            var totalsByCurrency = sellerGroup
                .GroupBy(row => row.Currency)
                .Select(currencyGroup => new CommissionCurrencyTotalDto
                {
                    Currency = currencyGroup.Key,
                    Amount = currencyGroup.Sum(row => row.Amount),
                })
                .OrderBy(total => total.Currency)
                .ToList();

            sellers.Add(new CommissionSellerMonthlyTotalDto
            {
                SellerUserId = sellerGroup.Key,
                // El nombre snapshot puede variar entre filas (raro); tomamos el primero no vacio.
                SellerName = sellerGroup
                    .Select(row => row.SellerName)
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                TotalsByCurrency = totalsByCurrency,
            });
        }

        // Orden estable para el front: por nombre de vendedor (cae al Id si no hay nombre).
        sellers = sellers
            .OrderBy(seller => seller.SellerName ?? seller.SellerUserId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CommissionMonthlySummaryDto
        {
            Year = year,
            Month = month,
            Sellers = sellers,
        };
    }
}
