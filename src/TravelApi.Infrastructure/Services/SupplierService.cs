using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

public class SupplierService : ISupplierService
{
    // ADR-022 §4.10 (fix #4): estados de Reserva "vivos" para la cuenta corriente del proveedor. FUENTE
    // UNICA en Domain (SupplierDebtCalculator.ValidReservationStatuses), compartida con SupplierDebtPersister
    // para que la deuda sea identica salga el numero del servicio del proveedor o del persister generico.
    // Antes estaba duplicada aca y en el persister, con riesgo de divergencia silenciosa.
    private static readonly string[] ValidReservationStatuses = SupplierDebtCalculator.ValidReservationStatuses;

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

        // ADR-021: resolver y validar el bloque de moneda/TC server-side (no confiar en el front). Para un
        // pago ARS no cruzado todo queda en null = identico al legacy.
        var currency = ResolvePaymentCurrencyBlock(request);

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
            // ADR-022 §4 P4: esta es la validacion GLOBAL (tope general contra toda la deuda del
            // proveedor). Si el pago viene imputado a una reserva, ademas se valida que no exceda la
            // deuda de ese proveedor EN ESA RESERVA y EN ESA MONEDA (ResolveImputationAsync).
            throw new InvalidOperationException("El pago excede la deuda actual con el proveedor.");
        }

        // ADR-022 §4 P4: la imputacion del pago. O una reserva concreta (validada) o anticipo "a cuenta".
        var (reservaId, servicioReservaId) = await ResolveSupplierPaymentImputationAsync(
            id, request, currency, cancellationToken);

        var payment = new SupplierPayment
        {
            SupplierId = id,
            Amount = request.Amount,
            Currency = currency.Currency,
            ImputedCurrency = currency.ImputedCurrency,
            ExchangeRate = currency.ExchangeRate,
            ExchangeRateSource = currency.ExchangeRateSource,
            ExchangeRateAt = currency.ExchangeRateAt,
            ImputedAmount = currency.ImputedAmount,
            Method = request.Method ?? "Transfer",
            Reference = request.Reference,
            Notes = request.Notes,
            ReservaId = reservaId,
            ServicioReservaId = servicioReservaId,
            PaidAt = DateTime.UtcNow
        };

        _dbContext.SupplierPayments.Add(payment);

        // ADR-022 §4.4: el asiento de caja (Expense) se escribe en la MISMA SaveChanges que el pago al
        // proveedor. Moneda = la REAL del egreso (SupplierPayment.Currency), nunca la imputada.
        var (ledgerUserId, ledgerUserName) = ResolveCurrentActor();
        var ledgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForSupplierPayment(
            payment, ledgerUserId, ledgerUserName);
        _dbContext.CashLedgerEntries.Add(ledgerEntry);

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

        // ADR-021: resolver/validar el bloque de moneda igual que en el alta.
        var currency = ResolvePaymentCurrencyBlock(request);

        var realDebt = await CalculateSupplierDebt(id, cancellationToken);
        var debtPrePayment = realDebt + payment.Amount;

        if (request.Amount > debtPrePayment)
        {
            // ADR-017 F1b: mismo criterio que AddSupplierPaymentAsync — el controller pisa
            // esta excepcion con un mensaje HTTP generico, asi que el mensaje es generico
            // para todos (sin revelar la deuda). El masking de montos vive en los DTOs.
            throw new InvalidOperationException("La modificacion del pago excede la deuda actual con el proveedor.");
        }

        // ADR-022 §4 P4: re-resolver la imputacion (reserva concreta o anticipo a cuenta), excluyendo el
        // propio pago de la deuda restante de la reserva (su monto viejo no debe contarse como "ya pagado").
        var (reservaId, servicioReservaId) = await ResolveSupplierPaymentImputationAsync(
            id, request, currency, cancellationToken, excludePaymentId: payment.Id);

        payment.Amount = request.Amount;
        payment.Currency = currency.Currency;
        payment.ImputedCurrency = currency.ImputedCurrency;
        payment.ExchangeRate = currency.ExchangeRate;
        payment.ExchangeRateSource = currency.ExchangeRateSource;
        payment.ExchangeRateAt = currency.ExchangeRateAt;
        payment.ImputedAmount = currency.ImputedAmount;
        payment.Method = request.Method ?? payment.Method;
        payment.Reference = request.Reference;
        payment.Notes = request.Notes;
        payment.ReservaId = reservaId;
        payment.ServicioReservaId = servicioReservaId;

        // ADR-022 §4.5: editar el monto = reversa del asiento viejo + asiento nuevo (orden estricto:
        // marcar viejo IsReversed ANTES de insertar). El libro conserva viejo (-) -> reversa (+) -> nuevo.
        await ReverseLiveSupplierPaymentLedgerEntryAsync(payment.Id, cancellationToken);
        var (updUserId, updUserName) = ResolveCurrentActor();
        var newLedgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForSupplierPayment(
            payment, updUserId, updUserName);
        _dbContext.CashLedgerEntries.Add(newLedgerEntry);

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

        // ADR-022 §4.5: anular el pago NO borra su asiento: se marca IsReversed=true y se inserta su
        // reversa (Income que netea el Expense original). El libro conserva la historia.
        await ReverseLiveSupplierPaymentLedgerEntryAsync(payment.Id, cancellationToken);

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

    /// <summary>
    /// ADR-022 §4.5: marca el asiento vigente de un SupplierPayment como revertido e inserta su reversa,
    /// en el orden estricto del indice unico parcial (marcar viejo IsReversed=true ANTES de Add de la
    /// reversa). NO hace SaveChanges — lo hace el caller. Si el pago no tiene asiento vigente (legacy sin
    /// backfill todavia), no hace nada.
    /// </summary>
    private async Task ReverseLiveSupplierPaymentLedgerEntryAsync(int supplierPaymentId, CancellationToken cancellationToken)
    {
        var live = await _dbContext.CashLedgerEntries
            .FirstOrDefaultAsync(
                e => e.SupplierPaymentId == supplierPaymentId && !e.IsReversal && !e.IsReversed,
                cancellationToken);
        if (live is null) return;

        var (userId, userName) = ResolveCurrentActor();
        live.IsReversed = true;
        var reversal = TravelApi.Domain.Helpers.CashLedgerEntryFactory.Reverse(
            live, DateTime.UtcNow, userId, userName);
        _dbContext.CashLedgerEntries.Add(reversal);
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
                Currency = payment.Currency,
                Method = payment.Method,
                PaidAt = payment.PaidAt,
                Reference = payment.Reference,
                Notes = payment.Notes,
                NumeroReserva = payment.Reserva != null ? payment.Reserva.NumeroReserva : null,
                FileName = payment.Reserva != null ? payment.Reserva.Name : null,
                ReservaPublicId = payment.Reserva != null ? (Guid?)payment.Reserva.PublicId : null,
                // ADR-022 §4 P4: sin reserva = anticipo "a cuenta" (incluye el legacy con ReservaId null).
                IsAdvanceToAccount = payment.ReservaId == null
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
    /// ADR-021 + ADR-022 §4 P4: valida y resuelve el bloque de moneda/TC de un pago a proveedor. Es la unica
    /// fuente server-side (no confiar en el front). Para un pago ARS no cruzado el bloque queda en null.
    /// Lanza <see cref="ArgumentException"/> (el controller la traduce a 400) si el bloque es incoherente.
    /// </summary>
    private static PaymentCurrencyResolver.Resolved ResolvePaymentCurrencyBlock(SupplierPaymentRequest request)
    {
        var roundedAmount = EconomicRulesHelper.RoundCurrency(request.Amount);
        return PaymentCurrencyResolver.Resolve(
            amount: roundedAmount,
            rawCurrency: request.Currency,
            rawImputedCurrency: request.ImputedCurrency,
            exchangeRate: request.ExchangeRate,
            exchangeRateSource: request.ExchangeRateSource,
            exchangeRateAt: request.ExchangeRateAt,
            imputedAmount: request.ImputedAmount,
            round: EconomicRulesHelper.RoundCurrency);
    }

    /// <summary>
    /// ADR-022 §4 P4: resuelve la imputacion de un pago a proveedor. Dos caminos validos y mutuamente
    /// excluyentes:
    /// <list type="bullet">
    /// <item><b>A reserva concreta</b> (<c>request.ReservaId</c> seteado, sin el flag a-cuenta): la reserva
    ///   debe existir, tener servicios de ESTE proveedor, y el equivalente imputado del pago no puede
    ///   exceder la deuda de este proveedor EN ESA RESERVA y EN ESA MONEDA (validacion adicional al tope
    ///   global del proveedor que ya corrio el caller).</item>
    /// <item><b>Anticipo "a cuenta"</b> (<c>request.IsAdvanceToAccount</c> = true, sin reserva): no se imputa
    ///   a ninguna reserva; vale solo el tope global. Es la opcion explicita del dueño (decision #2).</item>
    /// </list>
    /// Legacy sin reserva y sin el flag tambien se tolera (queda "a cuenta" implicito, no se migra).
    /// Devuelve los ids internos resueltos (reserva + servicio) para volcar en el <c>SupplierPayment</c>.
    /// <paramref name="excludePaymentId"/> excluye el propio pago al editar (su monto no debe contarse como
    /// "ya pagado" al validar la deuda restante de la reserva).
    /// </summary>
    private async Task<(int? ReservaId, int? ServicioReservaId)> ResolveSupplierPaymentImputationAsync(
        int supplierId,
        SupplierPaymentRequest request,
        PaymentCurrencyResolver.Resolved currency,
        CancellationToken cancellationToken,
        int? excludePaymentId = null)
    {
        bool hasReserva = !string.IsNullOrWhiteSpace(request.ReservaId);

        // No se puede pedir imputar a una reserva Y marcar anticipo a cuenta a la vez: son caminos opuestos.
        if (hasReserva && request.IsAdvanceToAccount)
        {
            throw new ArgumentException(
                "Un pago no puede imputarse a una reserva y marcarse como anticipo a cuenta a la vez.");
        }

        if (!hasReserva)
        {
            // Anticipo a cuenta (explicito o legacy): sin reserva ni servicio. Vale solo el tope global.
            return (null, null);
        }

        // ----- Imputado a una reserva concreta: validamos existencia, pertenencia y deuda por moneda -----
        var reservaId = await _dbContext.Reservas
            .AsNoTracking()
            .ResolveInternalIdAsync(request.ReservaId!, cancellationToken);
        if (!reservaId.HasValue)
        {
            throw new KeyNotFoundException("Reserva no encontrada");
        }

        int? servicioReservaId = null;
        if (!string.IsNullOrWhiteSpace(request.ServicioReservaId))
        {
            servicioReservaId = await _dbContext.Servicios
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ServicioReservaId, cancellationToken);
        }

        // La reserva tiene que tener al menos un servicio de ESTE proveedor (no se puede imputar un pago a
        // una reserva con la que el proveedor no tiene relacion).
        var supplierDebtInReserva = await CalculateSupplierDebtInReservaAsync(
            supplierId, reservaId.Value, excludePaymentId, cancellationToken);
        if (supplierDebtInReserva.Count == 0)
        {
            throw new InvalidOperationException(
                "La reserva no tiene servicios de este proveedor para imputar el pago.");
        }

        // La moneda a la que se imputa el pago: la imputada si cruzo, si no la propia del pago.
        string imputedCurrency = currency.ImputedCurrency ?? currency.Currency;
        decimal imputedAmount = currency.ImputedAmount ?? EconomicRulesHelper.RoundCurrency(request.Amount);

        supplierDebtInReserva.TryGetValue(imputedCurrency, out var debtLine);
        decimal debtInCurrency = debtLine?.Balance ?? 0m;

        // No exceder la deuda de este proveedor EN ESA RESERVA y EN ESA MONEDA. Si la moneda imputada no
        // tiene deuda en la reserva (debtInCurrency == 0), tambien se rechaza: no se imputa plata a una
        // moneda donde no se debe nada en esa reserva.
        if (imputedAmount > debtInCurrency)
        {
            throw new InvalidOperationException(
                "El pago excede la deuda de este proveedor en la reserva y la moneda indicadas.");
        }

        return (reservaId, servicioReservaId);
    }

    /// <summary>
    /// ADR-022 §4 P4: deuda de un proveedor SEPARADA por moneda PERO acotada a UNA reserva. Mismo motor que
    /// <see cref="CalculateSupplierDebtPorMonedaAsync"/> (compras confirmadas - pagos imputados, por moneda),
    /// solo que tanto los servicios como los pagos se filtran por <paramref name="reservaId"/>.
    /// <paramref name="excludePaymentId"/> saca el pago que se esta editando del "ya pagado".
    /// </summary>
    private async Task<IReadOnlyDictionary<string, SupplierDebtLine>> CalculateSupplierDebtInReservaAsync(
        int supplierId, int reservaId, int? excludePaymentId, CancellationToken cancellationToken)
    {
        // Servicios de este proveedor en ESTA reserva, por tipo/estado/moneda. Reusar el calculador puro
        // mantiene la cuenta identica a la global, solo que acotada a la reserva.
        var serviceRows = await BuildSupplierServiceDebtRowsInReservaAsync(supplierId, reservaId, cancellationToken);
        var confirmedPurchases = serviceRows
            .Where(r => WorkflowStatusHelper.CountsForSupplierDebtByType(r.Type, r.Status))
            .Select(r => new SupplierDebtCalculator.ConfirmedPurchase(r.Currency, r.NetCost));

        var paymentRows = await _dbContext.SupplierPayments
            .Where(p => p.SupplierId == supplierId
                        && p.ReservaId == reservaId
                        && (excludePaymentId == null || p.Id != excludePaymentId.Value))
            .Select(p => new { p.Amount, p.Currency, p.ImputedCurrency, p.ImputedAmount })
            .ToListAsync(cancellationToken);

        var payments = paymentRows.Select(p => new SupplierDebtCalculator.SupplierPaymentInput(
            p.Amount, p.Currency, p.ImputedCurrency, p.ImputedAmount));

        return SupplierDebtCalculator.Calculate(confirmedPurchases, payments);
    }

    /// <summary>
    /// Proyecta (Type, Status, NetCost, Currency) de los servicios de un proveedor en UNA reserva. Recorre
    /// los 5 tipos tipados + el generico, igual que <c>BuildSupplierServicesQuery</c>, pero filtrando por la
    /// reserva. Se usa solo para la validacion de deuda por reserva (P4).
    /// </summary>
    private async Task<List<(string Type, string Status, decimal NetCost, string Currency)>>
        BuildSupplierServiceDebtRowsInReservaAsync(int supplierId, int reservaId, CancellationToken cancellationToken)
    {
        var result = new List<(string Type, string Status, decimal NetCost, string Currency)>();

        // Currency se normaliza (null -> ARS) igual que en el calculador, para que la moneda nunca sea null.
        var flights = await _dbContext.FlightSegments.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(cancellationToken);
        result.AddRange(flights.Select(s => ("Vuelo", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var hotels = await _dbContext.HotelBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(cancellationToken);
        result.AddRange(hotels.Select(s => ("Hotel", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var transfers = await _dbContext.TransferBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(cancellationToken);
        result.AddRange(transfers.Select(s => ("Traslado", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var packages = await _dbContext.PackageBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(cancellationToken);
        result.AddRange(packages.Select(s => ("Paquete", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var assistances = await _dbContext.AssistanceBookings.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.Status, s.NetCost, s.Currency }).ToListAsync(cancellationToken);
        result.AddRange(assistances.Select(s => ("Asistencia", s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        var generics = await _dbContext.Servicios.AsNoTracking()
            .Where(s => s.SupplierId == supplierId && s.ReservaId == reservaId)
            .Select(s => new { s.ServiceType, s.Status, s.NetCost, s.Currency }).ToListAsync(cancellationToken);
        result.AddRange(generics.Select(s => (s.ServiceType ?? string.Empty, s.Status, s.NetCost, Monedas.Normalizar(s.Currency))));

        return result;
    }

    /// <summary>
    /// Auditoria ERP hallazgo #4 (2026-06-12): deuda con el proveedor DESGLOSADA POR EXPEDIENTE (reserva) y
    /// por moneda, mas el bucket de anticipos "a cuenta". El dueño concilia con los mayoristas por
    /// expediente, no solo por el total global.
    ///
    /// <para><b>Reconciliacion (invariante clave)</b>: este desglose NO reimplementa el calculo de deuda;
    /// reusa exactamente el mismo universo de servicios que el total global (<see cref="BuildSupplierServicesQuery"/>,
    /// ya filtrado por estados de reserva vivos + regla por tipo confirmado) y el mismo motor puro
    /// (<see cref="SupplierDebtCalculator"/>). Las compras se reparten por reserva; los pagos se reparten en
    /// (imputados a esa reserva) + (anticipos sin reserva). Por construccion, por cada moneda:
    /// suma(saldos por reserva) + suma(anticipos a cuenta) == total global. Hay un test de reconciliacion
    /// que lo verifica. NO se crea una segunda formula que pueda divergir (leccion ADR-023: una sola fuente).</para>
    ///
    /// <para><b>Masking</b>: la deuda al proveedor es COSTO. Sin <c>cobranzas.see_cost</c> los montos
    /// (compras, pagado, saldo, anticipos, totales) se anulan a 0; la estructura (que reservas, en que
    /// monedas) y la identidad de la reserva siguen visibles, igual que la caja y el resto de la cuenta.</para>
    /// </summary>
    public async Task<SupplierDebtByReservaDto> GetSupplierDebtByReservaAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.PublicId, s.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        // 1) COMPRAS CONFIRMADAS por reserva. Reusamos la MISMA query que alimenta el total global (ya
        //    filtrada por estados de reserva vivos), y nos quedamos solo con las que generan deuda por la
        //    regla oficial por tipo. Cada fila trae su ReservaPublicId/NumeroReserva/FileName y su moneda.
        var serviceRows = await BuildSupplierServicesQuery(id).ToListAsync(cancellationToken);
        var confirmedServiceRows = serviceRows
            .Where(row => WorkflowStatusHelper.CountsForSupplierDebtByType(row.Type, row.Status))
            .ToList();

        // 2) PAGOS del proveedor: traemos lo minimo para imputar (monto/moneda/imputado + a que reserva).
        //    El query filter !IsDeleted ya excluye los pagos anulados (igual que el calculo global), asi que
        //    una anulacion es self-healing y el desglose sigue reconciliando.
        var paymentRows = await _dbContext.SupplierPayments
            .Where(payment => payment.SupplierId == id)
            .Select(payment => new SupplierPaymentImputationRow(
                payment.ReservaId,
                payment.Reserva != null ? (Guid?)payment.Reserva.PublicId : null,
                payment.Reserva != null ? payment.Reserva.NumeroReserva : null,
                payment.Reserva != null ? payment.Reserva.Name : null,
                payment.Amount,
                payment.Currency,
                payment.ImputedCurrency,
                payment.ImputedAmount))
            .ToListAsync(cancellationToken);

        var result = BuildSupplierDebtByReserva(supplier.PublicId, supplier.Name, confirmedServiceRows, paymentRows);

        // Masking see_cost: sin permiso, la estructura queda visible pero todos los montos en 0.
        if (!await CanSeeSupplierCostFiguresAsync(cancellationToken))
        {
            MaskSupplierDebtByReservaAmounts(result);
        }

        return result;
    }

    /// <summary>
    /// Fila minima de un pago al proveedor para imputarlo en el desglose por reserva. Si <see cref="ReservaId"/>
    /// es null el pago es un anticipo "a cuenta" (incluye el legacy sin reserva).
    /// </summary>
    private readonly record struct SupplierPaymentImputationRow(
        int? ReservaId,
        Guid? ReservaPublicId,
        string? NumeroReserva,
        string? FileName,
        decimal Amount,
        string? Currency,
        string? ImputedCurrency,
        decimal? ImputedAmount);

    /// <summary>
    /// Arma el DTO de deuda por reserva a partir de las compras confirmadas y los pagos ya materializados.
    /// Funcion pura sobre listas en memoria (sin EF): construye una linea por reserva (con sus monedas),
    /// el bucket de anticipos a cuenta, y los totales globales de reconciliacion. Separada del metodo
    /// publico para poder razonar la reconciliacion sin la capa de acceso a datos.
    /// </summary>
    private static SupplierDebtByReservaDto BuildSupplierDebtByReserva(
        Guid supplierPublicId,
        string supplierName,
        IReadOnlyList<SupplierAccountServiceListItemDto> confirmedServiceRows,
        IReadOnlyList<SupplierPaymentImputationRow> paymentRows)
    {
        // --- Compras confirmadas agrupadas por reserva (y dentro, por moneda) ---
        // Por cada reserva guardamos su identidad (numero/nombre) y las compras por moneda.
        var reservaPurchases = new Dictionary<Guid, ReservaDebtAccumulator>();

        foreach (var row in confirmedServiceRows)
        {
            // Un servicio sin ReservaPublicId no deberia pasar el filtro de estados (siempre tiene reserva),
            // pero por las dudas lo ignoramos para no crear una entrada fantasma.
            if (row.ReservaPublicId is not Guid reservaPublicId) continue;

            if (!reservaPurchases.TryGetValue(reservaPublicId, out var accumulator))
            {
                accumulator = new ReservaDebtAccumulator(reservaPublicId, row.NumeroReserva, row.FileName);
                reservaPurchases[reservaPublicId] = accumulator;
            }

            string currency = Monedas.Normalizar(row.Currency);
            accumulator.AddPurchase(currency, row.NetCost);
        }

        // --- Pagos: imputados a una reserva concreta vs anticipos a cuenta ---
        // Anticipos: pagos sin ReservaId. Una linea por moneda imputada.
        var advancesByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var payment in paymentRows)
        {
            // El pago imputa a la moneda imputada si cruzo, si no a su propia moneda; el monto imputado es
            // ImputedAmount si cruzo, si no el Amount de caja. Misma regla que SupplierDebtCalculator.
            string imputedCurrency = Monedas.Normalizar(payment.ImputedCurrency ?? payment.Currency);
            decimal imputedAmount = payment.ImputedAmount ?? payment.Amount;

            if (payment.ReservaId is null || payment.ReservaPublicId is not Guid paymentReservaPublicId)
            {
                // Anticipo "a cuenta": no atado a un expediente.
                advancesByCurrency.TryGetValue(imputedCurrency, out var currentAdvance);
                advancesByCurrency[imputedCurrency] = currentAdvance + imputedAmount;
                continue;
            }

            // Pago imputado a una reserva. Puede ser una reserva que NO tiene compras confirmadas vivas
            // (p.ej. quedo Closed o sus servicios se cancelaron despues de pagar): igual debe aparecer para
            // que la reconciliacion cierre, asi que la creamos si no existe usando los datos del propio pago.
            if (!reservaPurchases.TryGetValue(paymentReservaPublicId, out var accumulator))
            {
                accumulator = new ReservaDebtAccumulator(
                    paymentReservaPublicId, payment.NumeroReserva, payment.FileName);
                reservaPurchases[paymentReservaPublicId] = accumulator;
            }

            accumulator.AddPayment(imputedCurrency, imputedAmount);
        }

        // --- Materializar el DTO ---
        var dto = new SupplierDebtByReservaDto
        {
            SupplierPublicId = supplierPublicId,
            SupplierName = supplierName
        };

        // Totales globales de reconciliacion: se acumulan sumando TODAS las lineas por moneda (reservas +
        // anticipos). Por construccion esto iguala compras totales - pagos totales por moneda, que es el
        // mismo numero que el calculo global de la cuenta corriente.
        var globalByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var accumulator in reservaPurchases.Values)
        {
            var reservaLine = new SupplierDebtReservaLineDto
            {
                ReservaPublicId = accumulator.ReservaPublicId,
                NumeroReserva = accumulator.NumeroReserva,
                FileName = accumulator.FileName
            };

            foreach (var (currency, line) in accumulator.ToLines())
            {
                reservaLine.Currencies.Add(new SupplierDebtCurrencyLineDto
                {
                    Currency = currency,
                    ConfirmedPurchases = EconomicRulesHelper.RoundCurrency(line.ConfirmedPurchases),
                    TotalPaid = EconomicRulesHelper.RoundCurrency(line.TotalPaid),
                    Balance = EconomicRulesHelper.RoundCurrency(line.Balance)
                });

                AccumulateCurrency(globalByCurrency, currency, line.Balance);
            }

            dto.Reservas.Add(reservaLine);
        }

        foreach (var (currency, amount) in advancesByCurrency)
        {
            // Un anticipo es plata pagada al proveedor sin compra que la respalde todavia: en la cuenta es
            // un saldo a FAVOR de la agencia, por eso resta a la deuda global (saldo negativo de esa moneda).
            dto.AdvancesToAccount.Add(new SupplierDebtCurrencyAmountDto
            {
                Currency = currency,
                Amount = EconomicRulesHelper.RoundCurrency(amount)
            });

            AccumulateCurrency(globalByCurrency, currency, -amount);
        }

        foreach (var (currency, amount) in globalByCurrency)
        {
            dto.GlobalTotals.Add(new SupplierDebtCurrencyAmountDto
            {
                Currency = currency,
                Amount = EconomicRulesHelper.RoundCurrency(amount)
            });
        }

        return dto;
    }

    /// <summary>Acumula <paramref name="amount"/> en la moneda indicada (crea la entrada si no existe).</summary>
    private static void AccumulateCurrency(Dictionary<string, decimal> byCurrency, string currency, decimal amount)
    {
        byCurrency.TryGetValue(currency, out var current);
        byCurrency[currency] = current + amount;
    }

    /// <summary>
    /// Acumulador mutable de compras y pagos de un proveedor en UNA reserva, agrupados por moneda. Solo se
    /// usa dentro de <see cref="BuildSupplierDebtByReserva"/> para armar las lineas por moneda de la reserva.
    /// </summary>
    private sealed class ReservaDebtAccumulator
    {
        public Guid ReservaPublicId { get; }
        public string? NumeroReserva { get; }
        public string? FileName { get; }

        private readonly Dictionary<string, decimal> _purchasesByCurrency = new(StringComparer.Ordinal);
        private readonly Dictionary<string, decimal> _paidByCurrency = new(StringComparer.Ordinal);

        public ReservaDebtAccumulator(Guid reservaPublicId, string? numeroReserva, string? fileName)
        {
            ReservaPublicId = reservaPublicId;
            NumeroReserva = numeroReserva;
            FileName = fileName;
        }

        public void AddPurchase(string currency, decimal netCost)
        {
            _purchasesByCurrency.TryGetValue(currency, out var current);
            _purchasesByCurrency[currency] = current + netCost;
        }

        public void AddPayment(string currency, decimal imputedAmount)
        {
            _paidByCurrency.TryGetValue(currency, out var current);
            _paidByCurrency[currency] = current + imputedAmount;
        }

        /// <summary>Devuelve una <see cref="SupplierDebtLine"/> por cada moneda presente en compras o pagos.</summary>
        public IEnumerable<KeyValuePair<string, SupplierDebtLine>> ToLines()
        {
            var currencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in _purchasesByCurrency.Keys) currencies.Add(key);
            foreach (var key in _paidByCurrency.Keys) currencies.Add(key);

            foreach (var currency in currencies)
            {
                _purchasesByCurrency.TryGetValue(currency, out var purchases);
                _paidByCurrency.TryGetValue(currency, out var paid);
                yield return new KeyValuePair<string, SupplierDebtLine>(
                    currency, new SupplierDebtLine(currency, purchases, paid));
            }
        }
    }

    /// <summary>
    /// Anula a 0 todos los montos del desglose por reserva (compras, pagado, saldo, anticipos, totales) cuando
    /// el caller no puede ver costos (ADR-017 F1b). La estructura (reservas, monedas) y la identidad quedan.
    /// </summary>
    private static void MaskSupplierDebtByReservaAmounts(SupplierDebtByReservaDto dto)
    {
        foreach (var reserva in dto.Reservas)
        {
            foreach (var currencyLine in reserva.Currencies)
            {
                currencyLine.ConfirmedPurchases = 0m;
                currencyLine.TotalPaid = 0m;
                currencyLine.Balance = 0m;
            }
        }

        foreach (var advance in dto.AdvancesToAccount)
        {
            advance.Amount = 0m;
        }

        foreach (var total in dto.GlobalTotals)
        {
            total.Amount = 0m;
        }
    }

    /// <summary>
    /// ADR-021 §15.3/§B5 + ADR-022 §4.10: UNICO punto de escritura de la deuda de un proveedor. Recalcula
    /// por moneda, persiste el escalar surrogate <c>Supplier.CurrentBalance</c> Y sincroniza la tabla hija
    /// <c>SupplierBalanceByCurrency</c>. NO llama a SaveChanges: lo hace el caller.
    ///
    /// <para>Delega en <see cref="SupplierDebtPersister"/> (helper sin estado), para que el numero sea
    /// EXACTAMENTE el mismo se invoque desde aca o desde <c>ReservaService</c> (fix P1: el servicio
    /// generico desincronizaba la deuda porque solo este service la recalculaba). El <paramref name="supplier"/>
    /// que recibe ya esta tracked; el persister lo re-resuelve por Id y obtiene la misma instancia tracked,
    /// asi que el escalar se escribe sobre el mismo objeto.</para>
    /// </summary>
    private Task PersistSupplierBalanceAsync(Supplier supplier, CancellationToken cancellationToken)
        => SupplierDebtPersister.PersistAsync(_dbContext, supplier.Id, cancellationToken);
}
