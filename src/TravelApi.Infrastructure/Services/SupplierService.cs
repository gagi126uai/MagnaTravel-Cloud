using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

public class SupplierService : ISupplierService
{
    // Estados de Reserva en los que un servicio "cuenta" para la cuenta corriente del proveedor.
    // ADR-020 (2026-06-07): InManagement (En gestion) reemplaza al viejo Sold. La deuda real con el
    // proveedor ya filtra por servicio confirmado (CountsForSupplierDebtByType); este conjunto solo
    // define que reservas son "vivas" para el proveedor. ToSettle es la etapa de liquidar con el operador.
    private static readonly string[] ValidReservationStatuses =
    {
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.ToSettle,
        EstadoReserva.Closed
    };

    private readonly AppDbContext _dbContext;
    // B1.15 Fase 0' (CODE-10 / INV-2): IAuditService + ILogger opcionales para no
    // romper unit tests con ctor de 1 arg (SupplierServiceTests). Si se inyectan,
    // SupplierPayments soft-delete escribe AuditLog con UserId/UserName del request.
    private readonly IAuditService? _auditService;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<SupplierService>? _logger;
    // ADR-017 F1b (cuenta corriente del proveedor): resolver de permisos para enmascarar
    // montos de costo/deuda segun cobranzas.see_cost. Opcional (default null) para no
    // romper instancias existentes; sin el, el masking es fail-closed (oculta los montos),
    // igual que CostMasking en RateService/QuoteService/BookingService.
    private readonly IUserPermissionResolver? _permissionResolver;

    public SupplierService(
        AppDbContext dbContext,
        IAuditService? auditService = null,
        Microsoft.AspNetCore.Http.IHttpContextAccessor? httpContextAccessor = null,
        ILogger<SupplierService>? logger = null,
        IUserPermissionResolver? permissionResolver = null)
    {
        _dbContext = dbContext;
        _auditService = auditService;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
        _permissionResolver = permissionResolver;
    }

    // ===================================================================
    // ADR-017 F1b — fix de seguridad SIN flag: la cuenta corriente del
    // proveedor (gateada solo por proveedores.view, permiso que el rol
    // Vendedor tiene seeded) devolvia CurrentBalance/TotalPurchases del
    // proveedor y NetCost por servicio. Con eso un vendedor sin
    // cobranzas.see_cost reconstruia la deuda a operadores y el costo de
    // cada servicio (y de ahi el margen de la agencia).
    //
    // Decision del dueño (2026-06-05, docs/ux/guia-ux-gaston.md): el
    // vendedor SIGUE viendo la lista de proveedores y sus servicios
    // (nombres, fechas, estados, confirmaciones), pero los montos de
    // costo/deuda solo con el permiso. SalePrice NUNCA se enmascara (D1:
    // es un monto de VENTA, no de costo).
    //
    // Lo persistido en DB no se toca: solo se anula en el DTO de salida.
    // ===================================================================

    /// <summary>
    /// Devuelve true si el caller actual puede ver montos de costo/deuda del
    /// proveedor. Admin siempre puede (bypass por rol dentro de CanSeeCostAsync);
    /// sin HttpContext/resolver (tests, jobs) es fail-closed: NO puede.
    /// </summary>
    private Task<bool> CanSeeSupplierCostFiguresAsync(CancellationToken cancellationToken)
        => CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, cancellationToken);

    public async Task<PagedResponse<SupplierListItemDto>> GetSuppliersAsync(SupplierListQuery query, CancellationToken cancellationToken)
    {
        var suppliersQuery = _dbContext.Suppliers.AsNoTracking();

        if (!query.IncludeInactive)
        {
            suppliersQuery = suppliersQuery.Where(supplier => supplier.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            suppliersQuery = suppliersQuery.Where(supplier =>
                supplier.Name.ToLower().Contains(normalized) ||
                supplier.TaxId != null && supplier.TaxId.ToLower().Contains(normalized) ||
                supplier.ContactName != null && supplier.ContactName.ToLower().Contains(normalized) ||
                supplier.Email != null && supplier.Email.ToLower().Contains(normalized) ||
                supplier.Phone != null && supplier.Phone.ToLower().Contains(normalized));
        }

        var itemsQuery = ApplySupplierOrdering(suppliersQuery, query)
            .Select(supplier => new SupplierListItemDto
            {
                PublicId = supplier.PublicId,
                Name = supplier.Name,
                ContactName = supplier.ContactName,
                Email = supplier.Email,
                Phone = supplier.Phone,
                TaxId = supplier.TaxId,
                TaxCondition = supplier.TaxCondition,
                Address = supplier.Address,
                IsActive = supplier.IsActive,
                CurrentBalance = supplier.CurrentBalance,
                CreatedAt = supplier.CreatedAt
            });

        var page = await itemsQuery.ToPagedResponseAsync(query, cancellationToken);

        // ADR-017 F1b: CurrentBalance es la deuda de la agencia con el proveedor.
        // Sin cobranzas.see_cost se anula; el resto del item (nombre, contacto,
        // estado) sigue visible — decision del dueño.
        if (!await CanSeeSupplierCostFiguresAsync(cancellationToken))
        {
            foreach (var item in page.Items)
            {
                item.CurrentBalance = 0m;
            }
        }

        return page;
    }

    public async Task<Supplier> GetSupplierAsync(int id, CancellationToken cancellationToken)
    {
        // ADR-017 F1b: AsNoTracking a proposito. Este metodo solo alimenta el GET
        // de lectura del controller, y para enmascarar CurrentBalance hay que mutar
        // la instancia devuelta — si estuviera trackeada, un SaveChanges posterior
        // en el mismo scope podria persistir el 0 enmascarado a la DB.
        var supplier = await _dbContext.Suppliers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        if (!await CanSeeSupplierCostFiguresAsync(cancellationToken))
        {
            supplier.CurrentBalance = 0m; // deuda con el proveedor: solo con see_cost
        }

        return supplier;
    }

    public async Task<Supplier> CreateSupplierAsync(Supplier supplier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplier.Name))
        {
            throw new ArgumentException("El nombre del proveedor es requerido.");
        }

        supplier.CreatedAt = DateTime.UtcNow;
        supplier.CurrentBalance = 0;

        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return supplier;
    }

    public async Task<Supplier> UpdateSupplierAsync(int id, Supplier supplier, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (existing == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        // B1.15 Fase 0' (CODE-13): bloquear cambios fiscales (TaxId, TaxCondition)
        // cuando hay reservas con factura CAE viva referenciando al proveedor.
        // Otros campos siguen libres (Name/Email/Phone/Address/IsActive/ContactName).
        var fiscalDataChanged =
            !string.Equals(existing.TaxId, supplier.TaxId, StringComparison.Ordinal) ||
            !string.Equals(existing.TaxCondition, supplier.TaxCondition, StringComparison.Ordinal);

        if (fiscalDataChanged)
        {
            var fiscalBlock = await MutationGuards.GetSupplierTaxIdMutationBlockReasonAsync(_dbContext, id, cancellationToken);
            if (fiscalBlock != null)
            {
                throw new InvalidOperationException(fiscalBlock);
            }
        }

        // C29: guard de desactivacion. La regla de negocio operativa es "no se
        // borran proveedores, se desactivan", pero hasta este hotfix se podia
        // marcar IsActive=false aunque el proveedor tuviera reservas activas
        // (Budget/Confirmed/Traveling), dejando estado inconsistente.
        // Solo bloqueamos la transicion true -> false; reactivacion (false -> true)
        // y updates que no tocan IsActive pasan sin chequeo.
        var deactivating = existing.IsActive && !supplier.IsActive;
        if (deactivating)
        {
            var activeReservasCount = await CountActiveReservasForSupplierAsync(id, cancellationToken);
            if (activeReservasCount > 0)
            {
                throw new InvalidOperationException(
                    $"Tiene {activeReservasCount} reservas activas, no se puede desactivar.");
            }
        }

        existing.Name = supplier.Name;
        existing.ContactName = supplier.ContactName;
        existing.Email = supplier.Email;
        existing.Phone = supplier.Phone;
        existing.TaxId = supplier.TaxId;
        existing.TaxCondition = supplier.TaxCondition;
        existing.Address = supplier.Address;
        existing.IsActive = supplier.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // ADR-017 F1b: la respuesta del PUT tambien ecoa CurrentBalance. Un rol con
        // proveedores.edit pero sin cobranzas.see_cost podria leer la deuda por aca.
        // Primero se DESVINCULA la entidad del change tracker y recien despues se
        // anula el monto: si siguiera trackeada, otro SaveChanges en el mismo scope
        // persistiria el 0 enmascarado a la DB (el update real ya quedo guardado arriba).
        if (!await CanSeeSupplierCostFiguresAsync(cancellationToken))
        {
            _dbContext.Entry(existing).State = EntityState.Detached;
            existing.CurrentBalance = 0m;
        }

        return existing;
    }

    /// <summary>
    /// Cuenta cuantas RESERVAS DISTINTAS tienen al menos un booking tipado
    /// (HotelBooking/TransferBooking/PackageBooking/FlightSegment) referenciando
    /// al supplier indicado, filtrando por reservas en estado "activo" segun la
    /// regla de C29 (Budget, Confirmed, Traveling) + Fase D (Sold, ToSettle).
    /// Closed/Cancelled NO cuentan. Con el flag EnableSoldToSettleStates OFF nunca
    /// hay filas Sold/ToSettle, asi que el conjunto efectivo es identico al historico.
    ///
    /// El servicio legacy ServicioReserva queda fuera a proposito (SupplierId
    /// nullable, esta deprecado).
    /// </summary>
    private async Task<int> CountActiveReservasForSupplierAsync(int supplierId, CancellationToken cancellationToken)
    {
        var hotelReservaIds = _dbContext.HotelBookings
            .AsNoTracking()
            .Where(booking => booking.SupplierId == supplierId)
            .Select(booking => booking.ReservaId);

        var transferReservaIds = _dbContext.TransferBookings
            .AsNoTracking()
            .Where(booking => booking.SupplierId == supplierId)
            .Select(booking => booking.ReservaId);

        var packageReservaIds = _dbContext.PackageBookings
            .AsNoTracking()
            .Where(booking => booking.SupplierId == supplierId)
            .Select(booking => booking.ReservaId);

        var flightReservaIds = _dbContext.FlightSegments
            .AsNoTracking()
            .Where(segment => segment.SupplierId == supplierId)
            .Select(segment => segment.ReservaId);

        var assistanceReservaIds = _dbContext.AssistanceBookings
            .AsNoTracking()
            .Where(booking => booking.SupplierId == supplierId)
            .Select(booking => booking.ReservaId);

        var bookedReservaIds = hotelReservaIds
            .Concat(transferReservaIds)
            .Concat(packageReservaIds)
            .Concat(flightReservaIds)
            .Concat(assistanceReservaIds);

        // OJO: este conjunto NO es el mismo que ValidReservationStatuses (incluye las etapas
        // comerciales tempranas y NO incluye Closed). Es el conteo de reservas "vivas" asociadas al
        // proveedor. ADR-020 (2026-06-07): Quotation/Budget/InManagement reemplazan al viejo par
        // Budget/Sold; ToSettle se mantiene. Closed/Cancelled/Lost no cuentan.
        return await _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva =>
                (reserva.Status == EstadoReserva.Quotation
                    || reserva.Status == EstadoReserva.Budget
                    || reserva.Status == EstadoReserva.InManagement
                    || reserva.Status == EstadoReserva.Confirmed
                    || reserva.Status == EstadoReserva.Traveling
                    || reserva.Status == EstadoReserva.ToSettle)
                && bookedReservaIds.Contains(reserva.Id))
            .Select(reserva => reserva.Id)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    public async Task DeleteSupplierAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        // Checks legacy preexistentes.
        var hasServices = await _dbContext.Servicios.AnyAsync(service => service.SupplierId == id, cancellationToken);
        if (hasServices)
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene servicios asociados");
        }

        // IgnoreQueryFilters: hay que ver tambien soft-deleted. Si no, un Admin podria
        // borrar el Supplier y el cascade FK hard-borraria los pagos soft-deleted,
        // perdiendo la auditoria que el soft-delete justamente preserva (B1.15 Fase 0').
        var hasPayments = await _dbContext.SupplierPayments
            .IgnoreQueryFilters()
            .AnyAsync(payment => payment.SupplierId == id, cancellationToken);
        if (hasPayments)
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene pagos registrados");
        }

        // C24: tambien chequear bookings tipados (Hotel/Transfer/Package/Flight).
        // Antes se podia ELIMINAR un supplier con bookings activos porque la FK
        // estaba en Cascade por convencion EF y arrastraba todo. Ahora se bloquea
        // explicitamente desde el servicio (mensaje de negocio claro) y la BD
        // mantiene Restrict como red de seguridad.
        if (await _dbContext.HotelBookings.AnyAsync(booking => booking.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene reservas de hotel asociadas");
        }

        if (await _dbContext.TransferBookings.AnyAsync(booking => booking.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene transfers asociados");
        }

        if (await _dbContext.PackageBookings.AnyAsync(booking => booking.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene paquetes asociados");
        }

        if (await _dbContext.FlightSegments.AnyAsync(segment => segment.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene segmentos de vuelo asociados");
        }

        if (await _dbContext.AssistanceBookings.AnyAsync(booking => booking.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene asistencias asociadas");
        }

        _dbContext.Suppliers.Remove(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecalculateAllBalancesAsync(CancellationToken cancellationToken)
    {
        var suppliers = await _dbContext.Suppliers.ToListAsync(cancellationToken);

        // ADR-021 §15.3: cada proveedor sincroniza escalar surrogate + tabla hija por moneda. Un solo
        // SaveChanges al final (todos los proveedores en la misma transaccion, como antes).
        foreach (var supplier in suppliers)
        {
            await PersistSupplierBalanceAsync(supplier, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateBalanceAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) return;

        // ADR-021 §15.3: escalar surrogate + tabla hija SupplierBalanceByCurrency en la misma SaveChanges.
        await PersistSupplierBalanceAsync(supplier, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SupplierAccountOverviewDto> GetSupplierAccountOverviewAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new SupplierAccountSupplierDto
            {
                PublicId = item.PublicId,
                Name = item.Name,
                ContactName = item.ContactName,
                Email = item.Email,
                Phone = item.Phone,
                TaxId = item.TaxId,
                TaxCondition = item.TaxCondition,
                Address = item.Address,
                IsActive = item.IsActive,
                CurrentBalance = item.CurrentBalance
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        var servicesQuery = BuildSupplierServicesQuery(id);
        var paymentsQuery = BuildSupplierPaymentsQuery(id);

        // P2: misma REGLA OFICIAL UNICA que el resto del calculo de deuda (antes esta era
        // la segunda copia de la lista de estados escrita a mano, que podia discrepar).
        var totalPurchases = await CalculateSupplierConfirmedPurchasesAsync(id, cancellationToken);
        var totalPaid = await paymentsQuery.SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var serviceCount = await servicesQuery.CountAsync(cancellationToken);
        var paymentCount = await paymentsQuery.CountAsync(cancellationToken);

        var overview = new SupplierAccountOverviewDto
        {
            Supplier = supplier,
            Summary = new SupplierAccountSummaryDto
            {
                TotalPurchases = EconomicRulesHelper.RoundCurrency(totalPurchases),
                TotalPaid = EconomicRulesHelper.RoundCurrency(totalPaid),
                Balance = EconomicRulesHelper.RoundCurrency(totalPurchases - totalPaid),
                ServiceCount = serviceCount,
                PaymentCount = paymentCount
            }
        };

        // ADR-017 F1b: TODO el resumen es plata del lado costo/deuda (compras al
        // proveedor, pagos egresos, saldo). Sin cobranzas.see_cost se anula completo;
        // los CONTADORES (cuantos servicios/pagos) no son montos y quedan visibles.
        if (!await CanSeeSupplierCostFiguresAsync(cancellationToken))
        {
            overview.Supplier.CurrentBalance = 0m;
            overview.Summary.TotalPurchases = 0m;
            overview.Summary.TotalPaid = 0m;
            overview.Summary.Balance = 0m;
        }

        return overview;
    }

    public async Task<PagedResponse<SupplierAccountServiceListItemDto>> GetSupplierAccountServicesAsync(
        int id,
        SupplierAccountServicesQuery query,
        CancellationToken cancellationToken)
    {
        var servicesQuery = BuildSupplierServicesQuery(id);

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var normalizedType = query.Type.Trim().ToLowerInvariant();
            servicesQuery = servicesQuery.Where(item => item.Type.ToLower() == normalizedType);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            servicesQuery = servicesQuery.Where(item =>
                item.Type.ToLower().Contains(normalized) ||
                item.Description != null && item.Description.ToLower().Contains(normalized) ||
                item.Confirmation != null && item.Confirmation.ToLower().Contains(normalized) ||
                item.NumeroReserva != null && item.NumeroReserva.ToLower().Contains(normalized) ||
                item.FileName != null && item.FileName.ToLower().Contains(normalized));
        }

        servicesQuery = ApplySupplierAccountServiceOrdering(servicesQuery, query);
        var page = await servicesQuery.ToPagedResponseAsync(query, cancellationToken);

        // ADR-017 F1b: NetCost es el costo del servicio con el proveedor — sin
        // cobranzas.see_cost se anula. SalePrice NO se toca (D1: es venta, el
        // vendedor lo ve siempre). Descripcion/confirmacion/fechas siguen visibles.
        if (!await CanSeeSupplierCostFiguresAsync(cancellationToken))
        {
            foreach (var item in page.Items)
            {
                item.NetCost = 0m;
            }
        }

        return page;
    }

    public async Task<PagedResponse<SupplierPaymentDto>> GetSupplierAccountPaymentsAsync(
        int id,
        SupplierAccountPaymentsQuery query,
        CancellationToken cancellationToken)
    {
        var paymentsQuery = BuildSupplierPaymentsQuery(id);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            paymentsQuery = paymentsQuery.Where(payment =>
                payment.Method.ToLower().Contains(normalized) ||
                payment.Reference != null && payment.Reference.ToLower().Contains(normalized) ||
                payment.Notes != null && payment.Notes.ToLower().Contains(normalized) ||
                payment.NumeroReserva != null && payment.NumeroReserva.ToLower().Contains(normalized) ||
                payment.FileName != null && payment.FileName.ToLower().Contains(normalized));
        }

        paymentsQuery = ApplySupplierPaymentOrdering(paymentsQuery, query);
        var page = await paymentsQuery.ToPagedResponseAsync(query, cancellationToken);

        // ADR-017 F1b: el monto de un pago al proveedor es plata del lado costo/deuda
        // (sumandolos se reconstruye TotalPaid y de ahi la deuda). Sin see_cost se
        // anula; metodo/referencia/fechas siguen visibles.
        await MaskSupplierPaymentAmountsAsync(page.Items, cancellationToken);
        return page;
    }

    public async Task<Guid> AddSupplierPaymentAsync(int id, SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("El monto debe ser mayor a 0");
        }

        var currentDebt = await CalculateSupplierDebt(id, cancellationToken);
        if (request.Amount > currentDebt)
        {
            // ADR-017 F1b: este mensaje NO debe revelar la deuda exacta con el proveedor.
            // SuppliersController traduce esta InvalidOperationException a un mensaje HTTP
            // generico ("No se pudo registrar el pago al proveedor"), asi que cualquier
            // detalle por permiso aca seria codigo muerto que jamas llega al cliente. Para
            // evitar esa incoherencia (y un eventual leak si algun futuro caller surfacea
            // ex.Message), el mensaje es generico para todos. El enmascarado real de montos
            // vive en los DTOs (MaskSupplierPaymentAmountsAsync), no en los mensajes de error.
            throw new InvalidOperationException("El pago excede la deuda actual con el proveedor.");
        }

        int? reservaId = null;
        int? servicioReservaId = null;

        if (!string.IsNullOrWhiteSpace(request.ReservaId))
        {
            reservaId = await _dbContext.Reservas
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ReservaId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ServicioReservaId))
        {
            servicioReservaId = await _dbContext.Servicios
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ServicioReservaId, cancellationToken);
        }

        var payment = new SupplierPayment
        {
            SupplierId = id,
            Amount = request.Amount,
            Method = request.Method ?? "Transfer",
            Reference = request.Reference,
            Notes = request.Notes,
            ReservaId = reservaId,
            ServicioReservaId = servicioReservaId,
            PaidAt = DateTime.UtcNow
        };

        _dbContext.SupplierPayments.Add(payment);

        // ADR-021 §15.3: primero persistimos el pago, despues recalculamos la deuda por moneda y
        // sincronizamos escalar surrogate + tabla hija. El recalculo lee los pagos de la BD (su query
        // filter !IsDeleted excluye los borrados), por eso el pago debe estar guardado antes de
        // recalcular. Para un pago ARS no cruzado el escalar resultante es identico a la cuenta vieja
        // (currentDebt - Amount). Escalar y tabla hija quedan en la misma (segunda) SaveChanges.
        await _dbContext.SaveChangesAsync(cancellationToken);
        await PersistSupplierBalanceAsync(supplier, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return payment.PublicId;
    }

    public async Task UpdateSupplierPaymentAsync(int id, int paymentId, SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(item => item.Id == paymentId && item.SupplierId == id, cancellationToken);
        if (payment == null)
        {
            throw new KeyNotFoundException("Pago no encontrado");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("El monto debe ser mayor a 0");
        }

        var realDebt = await CalculateSupplierDebt(id, cancellationToken);
        var debtPrePayment = realDebt + payment.Amount;

        if (request.Amount > debtPrePayment)
        {
            // ADR-017 F1b: mismo criterio que AddSupplierPaymentAsync — el controller pisa
            // esta excepcion con un mensaje HTTP generico, asi que el mensaje es generico
            // para todos (sin revelar la deuda). El masking de montos vive en los DTOs.
            throw new InvalidOperationException("La modificacion del pago excede la deuda actual con el proveedor.");
        }

        int? reservaId = null;
        if (!string.IsNullOrWhiteSpace(request.ReservaId))
        {
            reservaId = await _dbContext.Reservas
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ReservaId, cancellationToken);
        }

        payment.Amount = request.Amount;
        payment.Method = request.Method ?? payment.Method;
        payment.Reference = request.Reference;
        payment.Notes = request.Notes;
        payment.ReservaId = reservaId;

        // ADR-021 §15.3: persistimos la edicion del pago y recalculamos la deuda por moneda
        // (escalar surrogate + tabla hija). Para un pago ARS no cruzado el escalar resultante es
        // identico a la cuenta vieja (debtPrePayment - Amount). El recalculo lee de la BD, por eso
        // la edicion va antes.
        await _dbContext.SaveChangesAsync(cancellationToken);
        await PersistSupplierBalanceAsync(supplier, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSupplierPaymentAsync(int id, int paymentId, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(item => item.Id == paymentId && item.SupplierId == id, cancellationToken);
        if (payment == null)
        {
            throw new KeyNotFoundException("Pago no encontrado");
        }

        // TODO B1.7: bloquear si el pago cae dentro de un periodo contable cerrado.
        // Hoy NO existe modelo de periodos cerrados; cuando exista, agregar guard
        // aca antes del soft-delete.

        // B1.15 Fase 0' (CODE-10 / INV-2): soft-delete + AuditLog para preservar
        // trazabilidad. Antes era hard-delete: el pago desaparecia y el ajuste de
        // CurrentBalance quedaba sin registro de quien/cuando.
        var (userId, userName) = ResolveCurrentActor();
        payment.IsDeleted = true;
        payment.DeletedAt = DateTime.UtcNow;
        payment.DeletedByUserId = userId;

        // ADR-021 §15.6bis (BUG LATENTE CORREGIDO): antes hacia `currentDebt + payment.Amount`, que
        // suma el AMOUNT DE CAJA al recalculo — esto da el numero correcto SOLO si el pago fue en la
        // misma moneda que la deuda. Con un pago cruzado, lo que hay que devolver a la deuda es el
        // EQUIVALENTE IMPUTADO (ImputedAmount), no el Amount de caja. La forma robusta es no hacer
        // matematica de reversa a mano: soft-deleteamos, guardamos, y RECALCULAMOS. El query filter
        // !IsDeleted excluye el pago borrado, asi la deuda de su moneda imputada vuelve a subir por el
        // monto correcto (self-healing). El persister sincroniza escalar surrogate + tabla hija.
        await _dbContext.SaveChangesAsync(cancellationToken);
        await PersistSupplierBalanceAsync(supplier, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_auditService is not null)
        {
            try
            {
                var details = $"{{\"supplierId\":{id},\"amount\":{payment.Amount},\"method\":\"{payment.Method}\",\"reservaId\":{payment.ReservaId?.ToString() ?? "null"}}}";
                await _auditService.LogBusinessEventAsync(
                    action: "Delete",
                    entityName: "SupplierPayment",
                    entityId: payment.Id.ToString(),
                    details: details,
                    userId: userId,
                    userName: userName,
                    ct: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Audit log failed for SupplierPayment delete. PaymentId={PaymentId} SupplierId={SupplierId}",
                    paymentId, id);
            }
        }
    }

    /// <summary>
    /// Resuelve el actor actual (userId, userName) para auditoria. Si no hay
    /// HttpContext (tests unitarios o jobs), devuelve "System"/"Sistema".
    /// </summary>
    private (string UserId, string UserName) ResolveCurrentActor()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        if (user is null) return ("System", "Sistema");

        var userId = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "System";
        var userName = user.FindFirst("FullName")?.Value
                       ?? user.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                       ?? "Sistema";
        return (userId, userName);
    }

    public async Task<IEnumerable<SupplierPaymentDto>> GetSupplierPaymentsHistoryAsync(int id, CancellationToken cancellationToken)
    {
        var payments = await ApplySupplierPaymentOrdering(
                BuildSupplierPaymentsQuery(id),
                new SupplierAccountPaymentsQuery())
            .ToListAsync(cancellationToken);

        // ADR-017 F1b: mismo criterio que la version paginada (ver
        // GetSupplierAccountPaymentsAsync).
        await MaskSupplierPaymentAmountsAsync(payments, cancellationToken);
        return payments;
    }

    /// <summary>
    /// Anula Amount en los pagos al proveedor si el caller no puede ver montos de
    /// costo/deuda (ADR-017 F1b). Los datos no monetarios del pago quedan intactos.
    /// </summary>
    private async Task MaskSupplierPaymentAmountsAsync(
        IEnumerable<SupplierPaymentDto> payments,
        CancellationToken cancellationToken)
    {
        if (await CanSeeSupplierCostFiguresAsync(cancellationToken)) return;

        foreach (var payment in payments)
        {
            payment.Amount = 0m;
        }
    }

    private IQueryable<SupplierAccountServiceListItemDto> BuildSupplierServicesQuery(int supplierId)
    {
        var flights = _dbContext.FlightSegments
            .AsNoTracking()
            .Where(segment => segment.SupplierId == supplierId && ValidReservationStatuses.Contains(segment.Reserva!.Status))
            .Select(segment => new SupplierAccountServiceListItemDto
            {
                PublicId = segment.PublicId,
                Type = "Vuelo",
                Description = ((segment.AirlineName ?? string.Empty) + " " + (segment.FlightNumber ?? string.Empty) + " (" + (segment.Origin ?? string.Empty) + "-" + (segment.Destination ?? string.Empty) + ")").Trim(),
                Confirmation = segment.PNR ?? segment.TicketNumber,
                NetCost = segment.NetCost,
                SalePrice = segment.SalePrice,
                Currency = segment.Currency, // ADR-021: deuda del proveedor por moneda del servicio
                Date = segment.CreatedAt,
                Status = segment.Status,
                NumeroReserva = segment.Reserva!.NumeroReserva,
                FileName = segment.Reserva!.Name,
                ReservaPublicId = segment.Reserva!.PublicId
            });

        var hotels = _dbContext.HotelBookings
            .AsNoTracking()
            .Where(booking => booking.SupplierId == supplierId && ValidReservationStatuses.Contains(booking.Reserva!.Status))
            .Select(booking => new SupplierAccountServiceListItemDto
            {
                PublicId = booking.PublicId,
                Type = "Hotel",
                Description = ((booking.HotelName ?? string.Empty) + " (" + (booking.City ?? string.Empty) + ")").Trim(),
                Confirmation = booking.ConfirmationNumber,
                NetCost = booking.NetCost,
                SalePrice = booking.SalePrice,
                Currency = booking.Currency, // ADR-021: deuda del proveedor por moneda del servicio
                Date = booking.CreatedAt,
                Status = booking.Status,
                NumeroReserva = booking.Reserva!.NumeroReserva,
                FileName = booking.Reserva!.Name,
                ReservaPublicId = booking.Reserva!.PublicId
            });

        var transfers = _dbContext.TransferBookings
            .AsNoTracking()
            .Where(transfer => transfer.SupplierId == supplierId && ValidReservationStatuses.Contains(transfer.Reserva!.Status))
            .Select(transfer => new SupplierAccountServiceListItemDto
            {
                PublicId = transfer.PublicId,
                Type = "Traslado",
                Description = ((transfer.VehicleType ?? string.Empty) + " (" + (transfer.PickupLocation ?? string.Empty) + " -> " + (transfer.DropoffLocation ?? string.Empty) + ")").Trim(),
                Confirmation = transfer.ConfirmationNumber,
                NetCost = transfer.NetCost,
                SalePrice = transfer.SalePrice,
                Currency = transfer.Currency, // ADR-021: deuda del proveedor por moneda del servicio
                Date = transfer.CreatedAt,
                Status = transfer.Status,
                NumeroReserva = transfer.Reserva!.NumeroReserva,
                FileName = transfer.Reserva!.Name,
                ReservaPublicId = transfer.Reserva!.PublicId
            });

        var packages = _dbContext.PackageBookings
            .AsNoTracking()
            .Where(package => package.SupplierId == supplierId && ValidReservationStatuses.Contains(package.Reserva!.Status))
            .Select(package => new SupplierAccountServiceListItemDto
            {
                PublicId = package.PublicId,
                Type = "Paquete",
                Description = package.PackageName,
                Confirmation = package.ConfirmationNumber,
                NetCost = package.NetCost,
                SalePrice = package.SalePrice,
                Currency = package.Currency, // ADR-021: deuda del proveedor por moneda del servicio
                Date = package.CreatedAt,
                Status = package.Status,
                NumeroReserva = package.Reserva!.NumeroReserva,
                FileName = package.Reserva!.Name,
                ReservaPublicId = package.Reserva!.PublicId
            });

        var assistances = _dbContext.AssistanceBookings
            .AsNoTracking()
            .Where(assistance => assistance.SupplierId == supplierId && ValidReservationStatuses.Contains(assistance.Reserva!.Status))
            .Select(assistance => new SupplierAccountServiceListItemDto
            {
                PublicId = assistance.PublicId,
                Type = "Asistencia",
                Description = ((assistance.PlanType ?? "Seguro") + " (" + (assistance.CoverageZone ?? string.Empty) + ")").Trim(),
                Confirmation = assistance.ConfirmationNumber ?? assistance.PolicyNumber,
                NetCost = assistance.NetCost,
                SalePrice = assistance.SalePrice,
                Currency = assistance.Currency, // ADR-021: deuda del proveedor por moneda del servicio
                Date = assistance.CreatedAt,
                Status = assistance.Status,
                NumeroReserva = assistance.Reserva!.NumeroReserva,
                FileName = assistance.Reserva!.Name,
                ReservaPublicId = assistance.Reserva!.PublicId
            });

        var services = _dbContext.Servicios
            .AsNoTracking()
            .Where(service => service.SupplierId == supplierId && ValidReservationStatuses.Contains(service.Reserva!.Status))
            .Select(service => new SupplierAccountServiceListItemDto
            {
                PublicId = service.PublicId,
                Type = service.ServiceType,
                Description = service.Description ?? service.ServiceType,
                Confirmation = service.ConfirmationNumber,
                NetCost = service.NetCost,
                SalePrice = service.SalePrice,
                Currency = service.Currency, // ADR-021: el generico ahora aporta su moneda (§15.4)
                Date = service.CreatedAt,
                Status = service.Status,
                NumeroReserva = service.Reserva!.NumeroReserva,
                FileName = service.Reserva!.Name,
                ReservaPublicId = service.Reserva!.PublicId
            });

        return flights
            .Concat(hotels)
            .Concat(transfers)
            .Concat(packages)
            .Concat(assistances)
            .Concat(services);
    }

    private IQueryable<SupplierPaymentDto> BuildSupplierPaymentsQuery(int supplierId)
    {
        return _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(payment => payment.SupplierId == supplierId)
            .Select(payment => new SupplierPaymentDto
            {
                PublicId = payment.PublicId,
                Amount = payment.Amount,
                Method = payment.Method,
                PaidAt = payment.PaidAt,
                Reference = payment.Reference,
                Notes = payment.Notes,
                NumeroReserva = payment.Reserva != null ? payment.Reserva.NumeroReserva : null,
                FileName = payment.Reserva != null ? payment.Reserva.Name : null,
                ReservaPublicId = payment.Reserva != null ? (Guid?)payment.Reserva.PublicId : null
            });
    }

    private static IQueryable<Supplier> ApplySupplierOrdering(IQueryable<Supplier> query, SupplierListQuery request)
    {
        var sortBy = (request.SortBy ?? "name").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "createdat" => desc
                ? query.OrderByDescending(supplier => supplier.CreatedAt).ThenByDescending(supplier => supplier.Name)
                : query.OrderBy(supplier => supplier.CreatedAt).ThenBy(supplier => supplier.Name),
            "currentbalance" => desc
                ? query.OrderByDescending(supplier => supplier.CurrentBalance).ThenBy(supplier => supplier.Name)
                : query.OrderBy(supplier => supplier.CurrentBalance).ThenBy(supplier => supplier.Name),
            _ => desc
                ? query.OrderByDescending(supplier => supplier.Name).ThenByDescending(supplier => supplier.CreatedAt)
                : query.OrderBy(supplier => supplier.Name).ThenByDescending(supplier => supplier.CreatedAt)
        };
    }

    private static IQueryable<SupplierAccountServiceListItemDto> ApplySupplierAccountServiceOrdering(
        IQueryable<SupplierAccountServiceListItemDto> query,
        SupplierAccountServicesQuery request)
    {
        var sortBy = (request.SortBy ?? "date").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "netcost" => desc
                ? query.OrderByDescending(item => item.NetCost).ThenByDescending(item => item.Date)
                : query.OrderBy(item => item.NetCost).ThenByDescending(item => item.Date),
            "saleprice" => desc
                ? query.OrderByDescending(item => item.SalePrice).ThenByDescending(item => item.Date)
                : query.OrderBy(item => item.SalePrice).ThenByDescending(item => item.Date),
            _ => desc
                ? query.OrderByDescending(item => item.Date).ThenBy(item => item.Type)
                : query.OrderBy(item => item.Date).ThenBy(item => item.Type)
        };
    }

    private static IQueryable<SupplierPaymentDto> ApplySupplierPaymentOrdering(
        IQueryable<SupplierPaymentDto> query,
        SupplierAccountPaymentsQuery request)
    {
        var sortBy = (request.SortBy ?? "paidAt").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "amount" => desc
                ? query.OrderByDescending(item => item.Amount).ThenByDescending(item => item.PaidAt)
                : query.OrderBy(item => item.Amount).ThenByDescending(item => item.PaidAt),
            _ => desc
                ? query.OrderByDescending(item => item.PaidAt).ThenByDescending(item => item.PublicId)
                : query.OrderBy(item => item.PaidAt).ThenBy(item => item.PublicId)
        };
    }

    // Total de compras CONFIRMADAS a un proveedor: la suma de NetCost de los servicios que
    // generan deuda segun la REGLA OFICIAL UNICA (WorkflowStatusHelper.CountsForSupplierDebtByType:
    // solo confirmados; vuelos por codigo IATA, resto por texto). Es la UNICA definicion de
    // "cuanto le debemos a este proveedor por servicios confirmados": la usan TANTO el resumen
    // de cuenta del proveedor COMO el calculo de deuda, para que no haya dos numeros distintos
    // (antes cada uno tenia su propia lista de estados escrita a mano). Se materializa y filtra
    // en memoria (volumen chico por proveedor) porque la regla por tipo no es traducible a SQL.
    private async Task<decimal> CalculateSupplierConfirmedPurchasesAsync(int supplierId, CancellationToken cancellationToken)
    {
        var rows = await BuildSupplierServicesQuery(supplierId).ToListAsync(cancellationToken);
        return rows
            .Where(r => WorkflowStatusHelper.CountsForSupplierDebtByType(r.Type, r.Status))
            .Sum(r => r.NetCost);
    }

    // ADR-021 §15.4: la deuda con el proveedor SEPARADA por moneda. Mismo filtro oficial de servicios
    // que generan deuda, pero ahora cada NetCost cae en la linea de la moneda del servicio, y cada
    // pago vivo en la moneda a la que se imputa. Lo usa el persister de la tabla hija y el escalar.
    private async Task<IReadOnlyDictionary<string, SupplierDebtLine>> CalculateSupplierDebtPorMonedaAsync(
        int supplierId, CancellationToken cancellationToken)
    {
        var rows = await BuildSupplierServicesQuery(supplierId).ToListAsync(cancellationToken);

        var confirmedPurchases = rows
            .Where(r => WorkflowStatusHelper.CountsForSupplierDebtByType(r.Type, r.Status))
            .Select(r => new SupplierDebtCalculator.ConfirmedPurchase(r.Currency, r.NetCost));

        // El query filter !IsDeleted ya excluye los pagos soft-deleted (AppDbContext), por eso una
        // anulacion es self-healing: el pago borrado deja de sumar y la deuda de su moneda vuelve a subir.
        var paymentRows = await _dbContext.SupplierPayments
            .Where(payment => payment.SupplierId == supplierId)
            .Select(payment => new
            {
                payment.Amount,
                payment.Currency,
                payment.ImputedCurrency,
                payment.ImputedAmount
            })
            .ToListAsync(cancellationToken);

        var payments = paymentRows.Select(p => new SupplierDebtCalculator.SupplierPaymentInput(
            p.Amount, p.Currency, p.ImputedCurrency, p.ImputedAmount));

        return SupplierDebtCalculator.Calculate(confirmedPurchases, payments);
    }

    // Escalar surrogate de la deuda (semaforo §15.3): mono-moneda = identico a la cuenta legacy
    // (compras - pagos); multimoneda = sum(max(0, deuda por moneda)). Lo derivamos del detalle por
    // moneda para que haya UNA sola fuente de la cuenta.
    private async Task<decimal> CalculateSupplierDebt(int supplierId, CancellationToken cancellationToken)
    {
        var porMoneda = await CalculateSupplierDebtPorMonedaAsync(supplierId, cancellationToken);
        return SupplierDebtCalculator.ToSurrogateBalance(porMoneda);
    }

    /// <summary>
    /// ADR-021 §15.3/§B5: UNICO punto de escritura de la deuda de un proveedor. Recalcula por moneda,
    /// persiste el escalar surrogate <c>Supplier.CurrentBalance</c> Y sincroniza la tabla hija
    /// <c>SupplierBalanceByCurrency</c> (upsert por moneda, borrar las ausentes). NO llama a
    /// SaveChanges: lo hace el caller (asi escalar + hija quedan en la misma transaccion).
    /// </summary>
    private async Task PersistSupplierBalanceAsync(Supplier supplier, CancellationToken cancellationToken)
    {
        var porMoneda = await CalculateSupplierDebtPorMonedaAsync(supplier.Id, cancellationToken);

        // 1) Escalar surrogate (semaforo).
        supplier.CurrentBalance = SupplierDebtCalculator.ToSurrogateBalance(porMoneda);

        // 2) Tabla hija: upsert por moneda + borrar las monedas que ya no aplican.
        var existingRows = await _dbContext.SupplierBalanceByCurrency
            .Where(row => row.SupplierId == supplier.Id)
            .ToListAsync(cancellationToken);
        var existingByCurrency = existingRows.ToDictionary(row => row.Currency, StringComparer.Ordinal);

        foreach (var (currency, line) in porMoneda)
        {
            if (existingByCurrency.TryGetValue(currency, out var row))
            {
                row.ConfirmedPurchases = line.ConfirmedPurchases;
                row.TotalPaid = line.TotalPaid;
                row.Balance = line.Balance;
            }
            else
            {
                _dbContext.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency
                {
                    SupplierId = supplier.Id,
                    Currency = currency,
                    ConfirmedPurchases = line.ConfirmedPurchases,
                    TotalPaid = line.TotalPaid,
                    Balance = line.Balance
                });
            }
        }

        foreach (var row in existingRows)
        {
            if (!porMoneda.ContainsKey(row.Currency))
                _dbContext.SupplierBalanceByCurrency.Remove(row);
        }
    }
}
