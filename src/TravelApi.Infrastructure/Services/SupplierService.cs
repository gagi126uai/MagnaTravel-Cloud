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

    /// <summary>
    /// SEC-1 (TANDA 1): ¿el caller actual puede ver los DATOS DE TESORERIA de un pago al operador (metodo,
    /// referencia)? Es un permiso DISTINTO de ver montos de costo: el extracto del proveedor esta gateado por
    /// <c>proveedores.view</c> (lo ve, p.ej., un vendedor), pero el metodo/referencia de un pago son datos de
    /// tesoreria y solo deben mostrarse con <see cref="Permissions.TesoreriaSupplierPayments"/> (mismo permiso
    /// que protege la solapa de pagos; <c>debt-by-reserva</c> tampoco expone metodo/referencia por pago).
    ///
    /// <para>Mismo criterio que <see cref="CostMasking.CanSeeCostAsync"/>: Admin pasa por rol; sin
    /// HttpContext/resolver (tests, jobs) es FAIL-CLOSED (no puede), para no filtrar por accidente.</para>
    /// </summary>
    private async Task<bool> CanSeeSupplierPaymentDetailsAsync(CancellationToken cancellationToken)
    {
        var user = _httpContextAccessor?.HttpContext?.User;

        // Admin bypass por rol.
        if (user?.IsInRole("Admin") ?? false) return true;

        // Sin resolver o sin user resoluble: fail-closed.
        if (_permissionResolver is null || user is null) return false;

        var userId = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return false;

        var permissions = await _permissionResolver.GetPermissionsAsync(userId, cancellationToken);
        return permissions.Contains(Permissions.TesoreriaSupplierPayments);
    }

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
                DefaultCurrency = supplier.DefaultCurrency,
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

        // ADR-041 TANDA 5: el plazo de pago es opcional, pero si viene no puede ser negativo (un plazo
        // negativo no tiene sentido y daria un vencimiento sugerido anterior a la compra).
        ValidateDefaultPaymentTermDays(supplier.DefaultPaymentTermDays);

        // Rediseño alta de operador (2026-06-28): la moneda por defecto debe ser una soportada (ARS/USD).
        // Si viene vacia se resuelve a ARS; se guarda la forma canonica en mayuscula.
        supplier.DefaultCurrency = ValidateAndNormalizeDefaultCurrency(supplier.DefaultCurrency);

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

        // ADR-041 TANDA 5: validar el plazo de pago antes de tocar la entidad (si viene, >= 0).
        ValidateDefaultPaymentTermDays(supplier.DefaultPaymentTermDays);

        // Rediseño alta de operador (2026-06-28): la moneda por defecto SOLO se toca si el request realmente
        // trae una. Los forms de edicion existentes (SupplierFormModal y la solapa "Datos") NO mandan
        // defaultCurrency, asi que tratar el null/vacio como "poner ARS" RESETEARIA en silencio la moneda
        // elegida (un operador en USD pasaria a ARS al editar, p.ej., el telefono = perdida de dato). Por eso:
        //   - request con valor  -> se valida (debe ser soportada) y se asigna normalizada (validate-before-mutate);
        //   - request null/vacio -> se DEJA la moneda existente intacta (no se asigna nada).
        // Distinto del alta: ahi el null/vacio SI defaultea a ARS porque un proveedor nuevo necesita una moneda.
        bool defaultCurrencyProvided = !string.IsNullOrWhiteSpace(supplier.DefaultCurrency);
        string? normalizedDefaultCurrency = defaultCurrencyProvided
            ? ValidateAndNormalizeDefaultCurrency(supplier.DefaultCurrency)
            : null;

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
        // ADR-041 TANDA 5: plazo de pago por defecto (null = se borra el plazo = sin vencimiento sugerido).
        existing.DefaultPaymentTermDays = supplier.DefaultPaymentTermDays;
        // Rediseño alta de operador (2026-06-28): solo se pisa la moneda si el request trajo una (ver arriba);
        // si no vino, se preserva la existente para no resetearla a ARS al editar otros campos.
        if (defaultCurrencyProvided)
        {
            existing.DefaultCurrency = normalizedDefaultCurrency;
        }

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
    /// ADR-041 TANDA 5: el plazo de pago por defecto del proveedor es opcional (null = sin plazo), pero si
    /// se manda no puede ser negativo. Un plazo negativo daria un vencimiento sugerido ANTERIOR a la compra,
    /// que no tiene sentido de negocio. Lanza <see cref="ArgumentException"/> (el controller la mapea a 400).
    /// </summary>
    private static void ValidateDefaultPaymentTermDays(int? defaultPaymentTermDays)
    {
        if (defaultPaymentTermDays.HasValue && defaultPaymentTermDays.Value < 0)
        {
            throw new ArgumentException("El plazo de pago al proveedor no puede ser negativo.");
        }
    }

    /// <summary>
    /// Rediseño alta de operador (2026-06-28): valida y normaliza la moneda por defecto del proveedor.
    /// No se confia en el front: la moneda DEBE ser una de las soportadas (<see cref="Monedas.Soportadas"/>,
    /// hoy ARS/USD). Vacio/null = el form no la mando -> se resuelve a ARS (la moneda por defecto del sistema).
    /// Un valor no soportado se rechaza con un mensaje de negocio en espanol (sin exponer el valor ni strings
    /// internos). Devuelve la forma canonica en mayuscula (tolera "usd" del front).
    /// </summary>
    private static string ValidateAndNormalizeDefaultCurrency(string? defaultCurrency)
    {
        // Vacio = el front no eligio moneda -> ARS por defecto (no es un error de validacion).
        if (string.IsNullOrWhiteSpace(defaultCurrency))
        {
            return Monedas.ARS;
        }

        if (!Monedas.EsSoportada(defaultCurrency))
        {
            // Mensaje generico de negocio: NO incluye el valor recibido ni el catalogo interno de monedas.
            throw new ArgumentException("La moneda por defecto del proveedor no es válida.");
        }

        return Monedas.Normalizar(defaultCurrency);
    }

    /// <summary>
    /// Cuenta cuantas RESERVAS DISTINTAS tienen al menos un booking tipado
    /// (HotelBooking/TransferBooking/PackageBooking/FlightSegment) referenciando
    /// al supplier indicado, filtrando por reservas en estado "activo" (vivas):
    /// Quotation/Budget/InManagement/Confirmed/Traveling. Closed/Cancelled/Lost NO cuentan.
    /// ADR-036 (2026-06-21): se quito ToSettle (estado eliminado).
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
        // Budget/Sold. ADR-036 (2026-06-21): se quito ToSettle (estado eliminado). Closed/Cancelled/Lost
        // no cuentan.
        return await _dbContext.Reservas
            .AsNoTracking()
            .Where(reserva =>
                (reserva.Status == EstadoReserva.Quotation
                    || reserva.Status == EstadoReserva.Budget
                    || reserva.Status == EstadoReserva.InManagement
                    || reserva.Status == EstadoReserva.Confirmed
                    || reserva.Status == EstadoReserva.Traveling)
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
                DefaultCurrency = item.DefaultCurrency,
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

        // TANDA 1 (fix de monedas mezcladas): el saldo REAL por moneda se lee de la proyeccion ya
        // materializada SupplierBalanceByCurrency (NO se recalcula a mano). El resumen escalar de arriba
        // sigue existiendo por compatibilidad, pero SUMA todas las monedas en un solo numero y solo es fiel
        // mono-moneda; el front debe consumir BalancesByCurrency para el saldo correcto (la plata no cruza
        // ARS/USD). Orden alfabetico estable (ARS antes que USD) coherente con el resto de la cuenta.
        var balancesByCurrency = await _dbContext.SupplierBalanceByCurrency
            .AsNoTracking()
            .Where(row => row.SupplierId == id)
            .OrderBy(row => row.Currency)
            .Select(row => new SupplierAccountBalanceByCurrencyDto
            {
                Currency = row.Currency,
                ConfirmedPurchases = row.ConfirmedPurchases,
                TotalPaid = row.TotalPaid,
                Balance = row.Balance
            })
            .ToListAsync(cancellationToken);

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
            },
            BalancesByCurrency = balancesByCurrency
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

            // El saldo por moneda tambien es costo/deuda: se anulan los montos pero se conserva la
            // estructura (que monedas existen), igual que el resto de la cuenta del proveedor.
            foreach (var line in overview.BalancesByCurrency)
            {
                line.ConfirmedPurchases = 0m;
                line.TotalPaid = 0m;
                line.Balance = 0m;
            }
        }

        return overview;
    }

    /// <summary>
    /// TANDA 1 (cuenta corriente del proveedor): EXTRACTO de la Cuenta por Pagar como libro mayor, separado
    /// por moneda y con saldo corriente. NO crea una segunda verdad: reusa EXACTAMENTE el mismo universo de
    /// compras confirmadas que la deuda materializada (mismo <see cref="BuildSupplierServicesQuery"/> + regla
    /// oficial por tipo <c>CountsForSupplierDebtByType</c> + gate CommissionOnly), los mismos pagos vivos (el
    /// query filter <c>!IsDeleted</c> excluye los anulados) y la MISMA primitiva de imputacion del calculador.
    /// Por construccion, el saldo de cierre de cada moneda coincide con <c>SupplierBalanceByCurrency.Balance</c>
    /// (hay un test invariante que lo verifica).
    ///
    /// <para><b>Masking</b>: el extracto es COSTO (deuda con el operador). Sin <c>cobranzas.see_cost</c> los
    /// montos se anulan a 0 y <c>AmountsVisible</c> viene false; la estructura (movimientos, fechas, monedas)
    /// sigue visible, igual que el resto de la cuenta del proveedor.</para>
    /// </summary>
    public async Task<SupplierAccountStatementDto> GetSupplierAccountStatementAsync(int id, CancellationToken cancellationToken)
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

        // CARGOS = compras CONFIRMADAS que cuentan como deuda. Mismo universo + misma regla por tipo + mismo
        // gate CommissionOnly que el persister de la deuda (un proveedor intermediacion no genera compras).
        var generatesPurchaseDebt = await SupplierGeneratesPurchaseDebtAsync(id, cancellationToken);
        var serviceRows = generatesPurchaseDebt
            ? await BuildSupplierServicesQuery(id).ToListAsync(cancellationToken)
            : new List<SupplierAccountServiceListItemDto>();
        var confirmedServiceRows = serviceRows
            .Where(row => WorkflowStatusHelper.CountsForSupplierDebtByType(row.Type, row.Status))
            .ToList();

        // ABONOS = pagos vivos al operador. El query filter !IsDeleted ya excluye los anulados (igual que la
        // deuda), por eso una anulacion es self-healing y el extracto sigue cerrando con el saldo.
        var paymentRows = await _dbContext.SupplierPayments
            .Where(payment => payment.SupplierId == id)
            .Select(payment => new
            {
                payment.PublicId,
                payment.PaidAt,
                payment.Method,
                payment.Reference,
                payment.Amount,
                payment.Currency,
                payment.ImputedCurrency,
                payment.ImputedAmount
            })
            .ToListAsync(cancellationToken);

        // SEC-1: el metodo/referencia de un pago son datos de TESORERIA. El extracto lo ve cualquiera con
        // proveedores.view (p.ej. un vendedor), pero esos detalles solo se exponen con tesoreria.supplier_payments.
        // Sin el permiso, la linea de pago conserva fecha/moneda/monto pero con descripcion generica y sin
        // referencia (igual que debt-by-reserva, que tampoco expone metodo/referencia por pago).
        bool canSeePaymentDetails = await CanSeeSupplierPaymentDetailsAsync(cancellationToken);

        // Armamos las lineas planas usando las factories del builder, que derivan moneda/monto del abono con
        // la primitiva de imputacion del calculador (single truth). El builder agrupa por moneda y acumula.
        var inputLines = new List<SupplierAccountStatementInputLine>();

        foreach (var row in confirmedServiceRows)
        {
            inputLines.Add(SupplierAccountStatementBuilder.PurchaseLine(
                date: row.Date,
                description: BuildPurchaseDescription(row),
                documentRef: row.NumeroReserva,
                currency: row.Currency,
                netCost: row.NetCost,
                sourcePublicId: row.PublicId));
        }

        foreach (var payment in paymentRows)
        {
            var paymentInput = new SupplierDebtCalculator.SupplierPaymentInput(
                payment.Amount, payment.Currency, payment.ImputedCurrency, payment.ImputedAmount);

            // Con permiso de tesoreria: metodo como descripcion (o fallback) y referencia como documento.
            // Sin permiso: descripcion generica y sin referencia (no se filtran datos de tesoreria).
            string description = canSeePaymentDetails && !string.IsNullOrWhiteSpace(payment.Method)
                ? payment.Method
                : "Pago al operador";
            string? documentRef = canSeePaymentDetails ? payment.Reference : null;

            inputLines.Add(SupplierAccountStatementBuilder.PaymentLine(
                date: payment.PaidAt,
                description: description,
                documentRef: documentRef,
                payment: paymentInput,
                sourcePublicId: payment.PublicId));
        }

        var statement = SupplierAccountStatementBuilder.Build(inputLines);

        // Pasos B/C cuenta del operador (2026-06-29): ademas de la CAJA, derivamos el "Circuito de cancelacion"
        // (multa retenida + reembolso recibido) y el receivable "me tiene que devolver" (Y) del estado de las
        // cancelaciones del operador, y combinamos todo en el calculador economico para producir los DOS numeros
        // ("Le debo X" / "Me tiene que devolver Y") por moneda. El extracto de caja de arriba NO se toca: la
        // invariante extracto<->proyeccion sigue intacta. El circuito vive en un bloque SEPARADO.
        var circuit = await TravelApi.Infrastructure.Reservations.SupplierCancellationCircuitReader.LoadAsync(
            _dbContext, id, cancellationToken, _logger);

        var cashClosingByCurrency = statement.Currencies.ToDictionary(
            block => block.Currency, block => block.ClosingBalance, StringComparer.Ordinal);

        var reconciliation = SupplierAccountReconciliationBuilder.Build(
            cashClosingByCurrency, circuit.CircuitLines, circuit.ReceivableByCurrency);

        // Bug de lectura (2026-07-03): el "Prepago" del calculador economico es el sobrepago BRUTO (no sabe cuanto
        // saldo a favor ya se APLICO a otras reservas). Cargamos las aplicaciones VIVAS de saldo a favor del
        // operador y se las damos al mapper como lineas del extracto: mueven el saldo economico hacia 0 y hacen que
        // el "Saldo a favor" mostrado en el header baje exactamente hasta lo que queda por gastar (== pool).
        // El reconciler sigue usando el Prepago BRUTO para mintear el pool: NO se toca esa fuente.
        var creditApplicationLines = await LoadLiveSupplierCreditApplicationLinesAsync(id, cancellationToken);

        bool canSeeCost = await CanSeeSupplierCostFiguresAsync(cancellationToken);
        return MapSupplierAccountStatement(
            supplier.PublicId, supplier.Name, statement, reconciliation, creditApplicationLines, canSeeCost);
    }

    /// <summary>
    /// Una aplicacion VIVA de saldo a favor con el operador, lista para intercalar como linea del extracto
    /// economico. <see cref="Amount"/> es POSITIVO y entra como CARGO (+): reduce el sobrepago (mueve el saldo
    /// economico hacia 0), igual que las lineas de circuito. No es una linea de CAJA.
    /// </summary>
    private readonly record struct SupplierCreditApplicationStatementLine(
        DateTime Date,
        string Currency,
        decimal Amount,
        string? TargetReservaNumber,
        Guid ApplicationPublicId);

    /// <summary>
    /// Lee las aplicaciones VIVAS (Kind=Applied sin su contra-fila Reversed) de saldo a favor de ESTE operador.
    /// Una reversa deja neteada a cero su aplicacion (ni el Applied ni el Reversed aparecen), asi que revertir una
    /// aplicacion la hace desaparecer del extracto de forma simetrica. Proyeccion unica (sin N+1): trae el numero
    /// de la reserva destino para describir la linea.
    /// </summary>
    private async Task<List<SupplierCreditApplicationStatementLine>> LoadLiveSupplierCreditApplicationLinesAsync(
        int supplierId, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.SupplierCreditApplications
            .AsNoTracking()
            .Where(a => a.Entry.SupplierId == supplierId
                     && a.Kind == SupplierCreditApplicationKind.Applied
                     && !_dbContext.SupplierCreditApplications.Any(r => r.ReversesApplicationId == a.Id))
            .Select(a => new
            {
                a.Entry.Currency,
                a.Amount,
                AppliedAt = a.CreatedAt,
                a.PublicId,
                TargetReservaNumber = a.TargetReserva != null ? a.TargetReserva.NumeroReserva : null,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new SupplierCreditApplicationStatementLine(
                Date: r.AppliedAt,
                Currency: Monedas.Normalizar(r.Currency),
                Amount: r.Amount,
                TargetReservaNumber: r.TargetReservaNumber,
                ApplicationPublicId: r.PublicId))
            .ToList();
    }

    /// <summary>Texto legible de una linea de compra del extracto: "Tipo: descripcion" (sin costo, eso va aparte).</summary>
    private static string BuildPurchaseDescription(SupplierAccountServiceListItemDto row)
    {
        if (string.IsNullOrWhiteSpace(row.Description))
            return row.Type;
        return $"{row.Type}: {row.Description}".Trim();
    }

    /// <summary>
    /// Insumo intermedio del MERGE del extracto: un movimiento (de caja o de circuito) listo para ordenar
    /// cronologicamente y acumular el saldo corriente UNICO. <see cref="OrderGroup"/> es el desempate estable
    /// ante misma fecha: 0 = caja, 1 = circuito (primero la caja, despues el circuito). El signo lo pone la
    /// columna (<see cref="Charge"/> suma, <see cref="Credit"/> resta), igual que el resto del extracto.
    /// </summary>
    private readonly record struct MergedStatementLine(
        DateTime Date,
        int OrderGroup,
        string Kind,
        string Description,
        string? DocumentRef,
        Guid? SourcePublicId,
        string Currency,
        decimal Charge,
        decimal Credit);

    /// <summary>
    /// Mapea el extracto del dominio (value object puro) al DTO de salida.
    ///
    /// <para><b>Saldo unico (2026-06-30)</b>: en vez de mostrar la caja arriba y el circuito de cancelacion en
    /// un bloque aparte con saldo 0, FUSIONA ambos en una sola secuencia cronologica por moneda y recalcula un
    /// running balance de corrido. Ese saldo unico CIERRA en el saldo ECONOMICO (caja + multa retenida +
    /// reembolso recibido = <c>EconomicClosingBalance</c>), que reconcilia con los dos numeros del header. El
    /// saldo de SOLO caja (el que iguala la proyeccion <c>SupplierBalanceByCurrency.Balance</c>) se sigue
    /// exponiendo aparte en <c>CashClosingBalance</c> para el invariante.</para>
    ///
    /// <para>Redondea los montos a 2 decimales (coherente con la columna decimal(18,2) de la proyeccion). Si el
    /// caller no puede ver costos, anula los montos (cargo/abono/saldo) a 0 pero conserva la estructura
    /// (movimientos, fechas, monedas).</para>
    /// </summary>
    private static SupplierAccountStatementDto MapSupplierAccountStatement(
        Guid supplierPublicId,
        string supplierName,
        SupplierAccountStatement statement,
        SupplierAccountReconciliation reconciliation,
        IReadOnlyList<SupplierCreditApplicationStatementLine> creditApplicationLines,
        bool canSeeCost)
    {
        var dto = new SupplierAccountStatementDto
        {
            SupplierPublicId = supplierPublicId,
            SupplierName = supplierName,
            AmountsVisible = canSeeCost
        };

        // Indexamos los bloques economicos por moneda para colgar los dos numeros + el circuito a cada moneda.
        var econByCurrency = reconciliation.Currencies.ToDictionary(b => b.Currency, StringComparer.Ordinal);

        // Aplicaciones vivas de saldo a favor agrupadas por moneda (2026-07-03). Preservamos el orden de llegada
        // (el loader ya las trajo listas) para intercalarlas cronologicamente en el extracto.
        var creditAppsByCurrency = creditApplicationLines
            .GroupBy(l => l.Currency, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Una BC con receivable o multa (o una aplicacion de saldo a favor) puede generar una moneda que la CAJA
        // no tiene (caso de borde). Unimos: primero las monedas de caja (en su orden), despues las economicas y
        // las de aplicaciones que no aparezcan en caja.
        var cashCurrencies = statement.Currencies.Select(b => b.Currency).ToList();
        var orderedCurrencies = new List<string>(cashCurrencies);
        foreach (var econ in reconciliation.Currencies)
        {
            if (!cashCurrencies.Contains(econ.Currency))
                orderedCurrencies.Add(econ.Currency);
        }
        foreach (var appCurrency in creditAppsByCurrency.Keys)
        {
            if (!orderedCurrencies.Contains(appCurrency))
                orderedCurrencies.Add(appCurrency);
        }

        var cashByCurrency = statement.Currencies.ToDictionary(b => b.Currency, StringComparer.Ordinal);

        foreach (var currency in orderedCurrencies)
        {
            econByCurrency.TryGetValue(currency, out var econ);
            cashByCurrency.TryGetValue(currency, out var cashBlock);
            creditAppsByCurrency.TryGetValue(currency, out var currencyAppLines);

            // Total de saldo a favor APLICADO en esta moneda: baja el sobrepago mostrado. Cada aplicacion es un
            // cargo (+) que acerca el saldo economico a 0.
            decimal appliedCreditTotal = currencyAppLines?.Sum(l => l.Amount) ?? 0m;

            // Cara economica AJUSTADA por las aplicaciones. El calculador economico (builder) devuelve el sobrepago
            // BRUTO (no conoce las aplicaciones): aca le restamos lo ya aplicado para que los DOS numeros del header
            // reflejen lo que realmente queda. Por construccion, el "Saldo a favor" resultante == pool RemainingBalance
            // (el reconciler mintea el pool = Prepago bruto; cada aplicacion drena RemainingBalance sin tocar el bruto).
            decimal economicWithApplications = (econ?.EconomicClosingBalance ?? 0m) + appliedCreditTotal;
            decimal receivableY = econ?.TheyOweMe ?? 0m;
            decimal economicPlusReceivable = economicWithApplications + receivableY;
            decimal iTheyOweAdjusted = economicPlusReceivable > 0m ? economicPlusReceivable : 0m;
            decimal prepaymentAdjusted = economicPlusReceivable < 0m ? -economicPlusReceivable : 0m;

            var blockDto = new SupplierAccountStatementCurrencyBlockDto
            {
                Currency = currency,
                // CashClosingBalance = eco de la proyeccion (SupplierBalanceByCurrency.Balance). NO lo tocan las
                // aplicaciones (no hubo movimiento de caja): preserva el invariante extracto-caja <-> proyeccion.
                CashClosingBalance = canSeeCost ? EconomicRulesHelper.RoundCurrency(econ?.CashClosingBalance ?? 0m) : 0m,
                // EconomicClosingBalance ahora incluye las aplicaciones (caja + circuito + saldo a favor aplicado),
                // para que siga coincidiendo con el saldo unico del pie del extracto y con el header.
                EconomicClosingBalance = canSeeCost ? EconomicRulesHelper.RoundCurrency(economicWithApplications) : 0m,
                TheyOweMe = canSeeCost ? EconomicRulesHelper.RoundCurrency(receivableY) : 0m,
                ITheyOwe = canSeeCost ? EconomicRulesHelper.RoundCurrency(iTheyOweAdjusted) : 0m,
                Prepayment = canSeeCost ? EconomicRulesHelper.RoundCurrency(prepaymentAdjusted) : 0m,
            };

            var mergedLines = BuildMergedLines(cashBlock, econ, currencyAppLines);

            // Recalculamos el saldo corriente UNICO sobre la secuencia mergeada (cargo suma, abono resta),
            // arrancando de 0. Trabajamos con los montos crudos (sin redondear) y redondeamos SOLO al exponer,
            // igual que la caja lo hacia antes: el running acumula fiel y cada snapshot se redondea al mostrar.
            decimal runningBalance = 0m;
            foreach (var line in mergedLines)
            {
                runningBalance += line.Charge;
                runningBalance -= line.Credit;

                blockDto.Lines.Add(new SupplierAccountStatementLineDto
                {
                    Date = line.Date,
                    Kind = line.Kind,
                    Description = line.Description,
                    DocumentRef = line.DocumentRef,
                    SourcePublicId = line.SourcePublicId,
                    Currency = line.Currency,
                    Charge = canSeeCost ? EconomicRulesHelper.RoundCurrency(line.Charge) : 0m,
                    Credit = canSeeCost ? EconomicRulesHelper.RoundCurrency(line.Credit) : 0m,
                    RunningBalance = canSeeCost ? EconomicRulesHelper.RoundCurrency(runningBalance) : 0m
                });
            }

            // Saldo MOSTRADO = saldo corriente de la ultima linea de la secuencia mergeada = saldo economico.
            // Por construccion es CashClosing + multa + reembolso == EconomicClosingBalance (verificado por test).
            blockDto.ClosingBalance = canSeeCost ? EconomicRulesHelper.RoundCurrency(runningBalance) : 0m;

            dto.Currencies.Add(blockDto);
        }

        return dto;
    }

    /// <summary>
    /// Fusiona las lineas de CAJA (compras / pagos) con las del CIRCUITO de cancelacion (multa retenida +
    /// reembolso recibido) de UNA moneda en una sola secuencia cronologica.
    ///
    /// <para><b>Orden</b>: por fecha ascendente. Desempate estable y deterministico: ante misma fecha va primero
    /// la linea de caja (<c>OrderGroup=0</c>) y despues la de circuito (<c>OrderGroup=1</c>); dentro del mismo
    /// grupo y fecha se respeta el orden de origen (OrderBy de LINQ es estable). Asi el saldo corriente es
    /// reproducible corrida a corrida.</para>
    ///
    /// <para><b>Signo</b>: las lineas de circuito son un CARGO (+) —tanto la multa retenida como el reembolso
    /// recibido neutralizan el pago negativo que dejo la anulacion—, por eso se cargan en <c>Charge</c> con su
    /// <c>Amount</c> tal cual (positivo). No se les cambia el signo: solo pasan a ACUMULAR en el running balance
    /// unico (antes se mostraban con saldo 0 en un bloque aparte).</para>
    /// </summary>
    private static List<MergedStatementLine> BuildMergedLines(
        SupplierAccountStatementCurrencyBlock? cashBlock,
        SupplierAccountReconciliationCurrencyBlock? econ,
        List<SupplierCreditApplicationStatementLine>? creditApplicationLines)
    {
        var merged = new List<MergedStatementLine>();

        if (cashBlock != null)
        {
            foreach (var line in cashBlock.Lines)
            {
                merged.Add(new MergedStatementLine(
                    Date: line.Date,
                    OrderGroup: 0, // caja primero ante misma fecha
                    Kind: line.Kind,
                    Description: line.Description,
                    DocumentRef: line.DocumentRef,
                    SourcePublicId: line.SourcePublicId,
                    Currency: line.Currency,
                    Charge: line.Charge,
                    Credit: line.Credit));
            }
        }

        if (econ != null)
        {
            foreach (var line in econ.CircuitLines)
            {
                merged.Add(new MergedStatementLine(
                    Date: line.Date,
                    OrderGroup: 1, // circuito despues de la caja ante misma fecha
                    Kind: line.Kind,
                    Description: line.Description,
                    DocumentRef: line.DocumentRef,
                    SourcePublicId: line.SourcePublicId,
                    Currency: line.Currency,
                    Charge: line.Amount, // circuito = cargo (+): multa retenida / reembolso recibido
                    Credit: 0m));
            }
        }

        if (creditApplicationLines != null)
        {
            foreach (var line in creditApplicationLines)
            {
                // Descripcion legible: a que reserva se aplico el saldo a favor (el nº de reserva no es dato de
                // costo, se muestra igual que el resto de la estructura). Sin numero, texto generico.
                string description = string.IsNullOrWhiteSpace(line.TargetReservaNumber)
                    ? "Saldo a favor aplicado"
                    : $"Saldo a favor aplicado a la reserva N° {line.TargetReservaNumber}";

                merged.Add(new MergedStatementLine(
                    Date: line.Date,
                    OrderGroup: 2, // saldo a favor aplicado despues de caja y circuito ante misma fecha
                    Kind: SupplierAccountStatementLineKinds.CreditApplied,
                    Description: description,
                    DocumentRef: line.TargetReservaNumber,
                    SourcePublicId: line.ApplicationPublicId,
                    Currency: line.Currency,
                    Charge: line.Amount, // aplicar saldo a favor = cargo (+): reduce el sobrepago
                    Credit: 0m));
            }
        }

        // OrderBy de LINQ es estable: ante igual (Date, OrderGroup) preserva el orden de insercion (caja en el
        // orden del extracto; circuito y aplicaciones en el orden que los devolvieron sus readers).
        return merged
            .OrderBy(line => line.Date)
            .ThenBy(line => line.OrderGroup)
            .ToList();
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

        // ADR-041 TANDA 5: vencimiento sugerido por linea. El plazo vive en el maestro del proveedor (todas
        // las filas son de ESTE proveedor), asi que lo leemos UNA vez y derivamos la fecha por servicio.
        // Si el proveedor no tiene plazo, las lineas quedan sin vencimiento (null) = comportamiento actual.
        var defaultPaymentTermDays = await _dbContext.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.Id == id)
            .Select(supplier => supplier.DefaultPaymentTermDays)
            .FirstOrDefaultAsync(cancellationToken);

        foreach (var item in page.Items)
        {
            item.SuggestedDueDate = SupplierDebtCalculator.DeriveSuggestedDueDate(item.Date, defaultPaymentTermDays);
        }

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

        // (2026-06-26, decision del dueño) Se ELIMINO el viejo tope GLOBAL que comparaba el monto contra
        // ToSurrogateBalance: ese surrogate SUMA max(0,saldo) de ARS + USD en un solo numero, asi que un pago en
        // una moneda se medía contra deuda mezclada de TODAS (el bug de cruce de monedas). Las validaciones REALES
        // viven en ResolveSupplierPaymentImputationAsync, ESTRICTAMENTE por moneda y sin mezclar:
        //   - imputado a una RESERVA: no excede la deuda de ese proveedor EN ESA RESERVA y EN ESA MONEDA;
        //   - imputado a un SERVICIO: coincide la moneda y no supera el costo pendiente de ESE servicio;
        //   - anticipo "a cuenta" (seña/prepago): SIN tope superior — puede quedar como saldo a favor con el
        //     operador EN SU MONEDA (reservar cupo pagando antes). La aislacion por moneda la garantiza
        //     SupplierDebtCalculator: un pago solo afecta el bucket de su propia moneda imputada, nunca otro.
        var imputation = await ResolveSupplierPaymentImputationAsync(
            id, request, currency, cancellationToken);

        // ADR-044 T3b Decision 3 (2026-07-10): si este pago liquida el documento de un cargo del operador
        // FacturadaAparte puntual, resolvemos y validamos ESE cargo ANTES de persistir (fail-fast, mismo criterio
        // que el resto del alta): tiene que existir, ser de ESTE proveedor y estar en FacturadaAparte (Retenida
        // se liquida por el reembolso del operador, no por un pago nuestro).
        BookingCancellationLineOperatorCharge? settledCharge = null;
        if (request.SettlesOperatorChargePublicId.HasValue)
        {
            settledCharge = await _dbContext.BookingCancellationLineOperatorCharges
                .Include(c => c.BookingCancellationLine)
                .FirstOrDefaultAsync(
                    c => c.PublicId == request.SettlesOperatorChargePublicId.Value
                      && c.BookingCancellationLine.SupplierId == id,
                    cancellationToken)
                ?? throw new KeyNotFoundException("El cargo del operador indicado no existe para este proveedor.");

            if (settledCharge.CollectionMode != PenaltyCollectionMode.FacturadaAparte)
                throw new ArgumentException(
                    "Este pago solo puede liquidar un cargo del operador facturado aparte.", nameof(request));

            // S2 (bloqueante security, 2026-07-10): RED DURA anti doble-liquidacion. Si el cargo YA fue liquidado
            // por otro pago vivo, rechazamos — sin esto se podia pagar dos veces el mismo cargo al operador y
            // generar dos ajustes de diferencia de cambio.
            if (settledCharge.SettledBySupplierPaymentId.HasValue)
                throw new ArgumentException(
                    "Ese cargo del operador ya se pagó. Si el pago anterior era incorrecto, eliminalo primero.",
                    nameof(request));
        }

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
            ReservaId = imputation.ReservaId,
            ServicioReservaId = imputation.ServicioReservaId,
            ServiceRecordKind = imputation.ServiceRecordKind,
            ServicePublicId = imputation.ServicePublicId,
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
        // (deuda previa - Amount). Escalar y tabla hija quedan en la misma (segunda) SaveChanges.
        await _dbContext.SaveChangesAsync(cancellationToken);

        // ADR-044 T3b Decision 3: el pago YA tiene Id real (SaveChanges recien commiteo) — recien aca podemos
        // marcar el cargo como liquidado (S2: red anti doble-liquidacion) y registrar el ajuste de diferencia de
        // cambio de tesoreria del cargo que liquida (si corresponde). Sin efecto en el ajuste si el cargo no
        // necesito conversion o el pago no fue cruzado (ver el motor para el detalle).
        if (settledCharge is not null)
        {
            settledCharge.SettledBySupplierPaymentId = payment.Id;
            await TreasuryFxAdjustmentEngine.RegisterForInvoicedChargeAsync(
                _dbContext, settledCharge, payment, _logger, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await PersistSupplierBalanceAsync(supplier, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // ADR-041 TANDA 3: si este pago dejo SOBREPAGO con el operador en alguna moneda, el excedente se
        // materializa como SALDO A FAVOR consumible (SupplierCreditEntry). El recalculo de arriba ya dejo el
        // Balance negativo committed; el reconciler lo lee y crea/ajusta el pool. No mueve la deuda (la caja
        // ya conto el pago completo en TotalPaid): solo le da cara consumible al balance negativo.
        await ReconcileSupplierCreditAsync(supplier.Id, sourceSupplierPaymentId: payment.Id, cancellationToken);

        return payment.PublicId;
    }

    /// <summary>
    /// ADR-041 TANDA 3: mantiene el pool de saldo a favor con el operador (<c>SupplierCreditEntry</c>) en sync
    /// con el sobrepago derivado, por moneda. Se llama DESPUES de persistir la deuda del proveedor (el balance
    /// ya esta committed). Sin auditoria/actor (jobs/tests) igual reconcilia el pool. Ver
    /// <see cref="TravelApi.Infrastructure.Reservations.SupplierCreditReconciler"/>.
    /// </summary>
    private Task ReconcileSupplierCreditAsync(int supplierId, int? sourceSupplierPaymentId, CancellationToken cancellationToken)
    {
        var (userId, userName) = ResolveCurrentActor();
        return TravelApi.Infrastructure.Reservations.SupplierCreditReconciler.ReconcileAsync(
            _dbContext, supplierId, sourceSupplierPaymentId, userId, userName, _auditService, cancellationToken);
    }

    /// <summary>
    /// ADR-041 TANDA 3: corre un cuerpo de escritura DENTRO de una transaccion (solo en provider relacional).
    /// Lo usan la edicion y la baja de un pago: si el reconciler del saldo a favor LANZA (porque la edicion
    /// destruiria un saldo ya aplicado a otra reserva), la transaccion revierte y el pago NO queda modificado
    /// a medias. En InMemory (tests) no hay transaccion: el mismo cuerpo corre sin envoltura (el throw se
    /// propaga igual y los tests de bloqueo lo validan). Mismo patron que <c>ClientCreditService</c>.
    /// </summary>
    private async Task RunInWriteTransactionAsync(Func<Task> body, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.IsRelational())
        {
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
                await body();
                await transaction.CommitAsync(cancellationToken);
            });
        }
        else
        {
            await body();
        }
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

        // (2026-06-26, decision del dueño) Igual que en el alta: se ELIMINO el tope GLOBAL con surrogate mezclado
        // (era el bug de cruce de monedas). Las validaciones reales (por reserva / por servicio / por moneda) viven
        // en ResolveSupplierPaymentImputationAsync; el anticipo a cuenta no tiene tope superior (prepago/seña).
        // excludePaymentId saca el monto VIEJO del propio pago de la deuda restante, para no contarlo como "ya
        // pagado" al validar la imputacion a reserva/servicio (la edicion de un anticipo no usa cap).
        var imputation = await ResolveSupplierPaymentImputationAsync(
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
        payment.ReservaId = imputation.ReservaId;
        payment.ServicioReservaId = imputation.ServicioReservaId;
        payment.ServiceRecordKind = imputation.ServiceRecordKind;
        payment.ServicePublicId = imputation.ServicePublicId;

        // ADR-022 §4.5: editar el monto = reversa del asiento viejo + asiento nuevo (orden estricto:
        // marcar viejo IsReversed ANTES de insertar). El libro conserva viejo (-) -> reversa (+) -> nuevo.
        await ReverseLiveSupplierPaymentLedgerEntryAsync(payment.Id, cancellationToken);
        var (updUserId, updUserName) = ResolveCurrentActor();
        var newLedgerEntry = TravelApi.Domain.Helpers.CashLedgerEntryFactory.ForSupplierPayment(
            payment, updUserId, updUserName);
        _dbContext.CashLedgerEntries.Add(newLedgerEntry);

        // ADR-021 §15.3: persistimos la edicion del pago y recalculamos la deuda por moneda
        // (escalar surrogate + tabla hija). Para un pago ARS no cruzado el escalar resultante es
        // identico a la cuenta vieja (deuda previa - Amount). El recalculo lee de la BD, por eso
        // la edicion va antes. ADR-041: todo dentro de UNA transaccion con el reconciler, para que si la
        // edicion destruiria un saldo a favor ya aplicado, revierta entero (el pago NO queda a medias).
        await RunInWriteTransactionAsync(async () =>
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await PersistSupplierBalanceAsync(supplier, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // ADR-041 TANDA 3: editar el monto/moneda del pago pudo CRECER o REDUCIR el sobrepago. El reconciler
            // crea o drena el saldo a favor para que el pool siga reflejando el sobrepago. Si la edicion bajaria
            // el sobrepago por debajo de lo ya aplicado a otra reserva, lanza (no se destruye un saldo consumido).
            await ReconcileSupplierCreditAsync(supplier.Id, sourceSupplierPaymentId: payment.Id, cancellationToken);
        }, cancellationToken);
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

        // S2 (bloqueante security, 2026-07-10): al eliminar el pago, LIMPIAMOS la marca de liquidacion del/los
        // cargo(s) que este pago habia liquidado, para que puedan volver a pagarse con el pago correcto. Sin
        // esto, un pago mal cargado dejaria el cargo bloqueado para siempre.
        var settledCharges = await _dbContext.BookingCancellationLineOperatorCharges
            .Where(c => c.SettledBySupplierPaymentId == payment.Id)
            .ToListAsync(cancellationToken);
        foreach (var settledCharge in settledCharges)
            settledCharge.SettledBySupplierPaymentId = null;

        // ADR-044 T3b Decision 3 (M4, 2026-07-10): si este pago liquidaba un cargo FacturadaAparte con ajuste de
        // diferencia de cambio VIGENTE, queda superseded (historia intacta, NO se borra). Sin reemplazo automatico:
        // si despues se registra el pago correcto, ESE alta crea la fila nueva sola (RegisterForInvoicedChargeAsync).
        await TreasuryFxAdjustmentEngine.SupersedeForVoidedOriginAsync(
            _dbContext, cancellationToken, voidedSupplierPaymentId: payment.Id);

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
        // ADR-041: la baja del pago + recalculo + reconciliacion del saldo a favor van en UNA transaccion. Si el
        // saldo a favor ya se aplico a otra reserva, el reconciler lanza y la baja entera revierte.
        await RunInWriteTransactionAsync(async () =>
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await PersistSupplierBalanceAsync(supplier, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // ADR-041 TANDA 3: borrar el pago redujo (o elimino) el sobrepago. El reconciler drena el saldo a
            // favor que ya no esta respaldado. Si ese saldo ya se aplico a otra reserva, lanza y bloquea la baja
            // (hay que revertir la aplicacion primero). No hay pago de origen para un drenaje -> sourcePaymentId null.
            await ReconcileSupplierCreditAsync(supplier.Id, sourceSupplierPaymentId: null, cancellationToken);
        }, cancellationToken);

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
                IsAdvanceToAccount = payment.ReservaId == null,
                // ADR-036 4c: si el pago se imputo a un servicio puntual, viaja el par (recordKind, publicId).
                ServiceRecordKind = payment.ServiceRecordKind,
                ServicePublicId = payment.ServicePublicId
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
        // (2026-06-26) CommissionOnly no genera deuda de compra: total de compras confirmadas = 0 (intermediacion,
        // la agencia no compra). Mismo gate que el resto de la cuenta del proveedor.
        if (!await SupplierGeneratesPurchaseDebtAsync(supplierId, cancellationToken))
            return 0m;

        var rows = await BuildSupplierServicesQuery(supplierId).ToListAsync(cancellationToken);
        return rows
            .Where(r => WorkflowStatusHelper.CountsForSupplierDebtByType(r.Type, r.Status))
            .Sum(r => r.NetCost);
    }

    /// <summary>
    /// (2026-06-26) ¿Los servicios de este proveedor generan deuda de compra (Cuenta por Pagar)? Lee su
    /// <see cref="Supplier.InvoicingMode"/> y delega la regla en el dominio
    /// (<see cref="SupplierDebtCalculator.SupplierGeneratesPurchaseDebt"/>): reseller (TotalToCustomer) si,
    /// intermediacion (CommissionOnly) no. Se consulta SOLO la columna del modo (no toda la entidad) por moneda.
    /// Lo usan todas las lecturas de la deuda del proveedor para que el numero coincida con el persister.
    /// </summary>
    private async Task<bool> SupplierGeneratesPurchaseDebtAsync(int supplierId, CancellationToken cancellationToken)
    {
        var invoicingMode = await _dbContext.Suppliers
            .AsNoTracking()
            .Where(s => s.Id == supplierId)
            .Select(s => s.InvoicingMode)
            .FirstOrDefaultAsync(cancellationToken);
        return SupplierDebtCalculator.SupplierGeneratesPurchaseDebt(invoicingMode);
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
    private async Task<SupplierPaymentImputationResult> ResolveSupplierPaymentImputationAsync(
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

        // ADR-036 4c: si se pide imputar a un servicio concreto, OBLIGA a tener reserva (un servicio vive
        // dentro de una reserva). Validamos el formato del recordKind antes de tocar la base.
        bool hasService = !string.IsNullOrWhiteSpace(request.ServicePublicId);
        string? normalizedServiceKind = null;
        if (hasService)
        {
            if (!hasReserva)
            {
                throw new ArgumentException(
                    "Para imputar el pago a un servicio hay que indicar tambien su reserva.");
            }
            normalizedServiceKind = ServicePaymentRecordKinds.Normalize(request.ServiceRecordKind);
            if (normalizedServiceKind is null)
            {
                throw new ArgumentException("El tipo de servicio para imputar el pago no es valido.");
            }
        }

        if (!hasReserva)
        {
            // Anticipo "a cuenta" / seña (explicito o legacy): sin reserva ni servicio.
            // (2026-06-26, decision del dueño) Es un PREPAGO GENUINO: SIN tope superior. El operador puede recibir
            // una seña por adelantado (reservar cupo pagando antes), incluso por ENCIMA de la deuda actual en esa
            // moneda; el excedente queda como SALDO A FAVOR con ese operador EN ESA MONEDA. La UNICA regla dura es
            // NO MEZCLAR MONEDAS: un anticipo en USD jamas reduce/afecta la deuda en ARS (ni viceversa). Esa
            // aislacion la garantiza estructuralmente SupplierDebtCalculator: el pago imputa SOLO al bucket de su
            // moneda imputada (ImputedCurrency ?? Currency), nunca a otra. Por eso aca NO hay ninguna validacion de
            // tope (se elimino el viejo gate por moneda y el tope global con surrogate mezclado, que era el bug).
            return SupplierPaymentImputationResult.AdvanceToAccount;
        }

        // ----- Imputado a una reserva concreta: validamos existencia, pertenencia y deuda por moneda -----
        var reservaId = await _dbContext.Reservas
            .AsNoTracking()
            .ResolveInternalIdAsync(request.ReservaId!, cancellationToken);
        if (!reservaId.HasValue)
        {
            throw new KeyNotFoundException("Reserva no encontrada");
        }

        // Camino legacy del servicio generico (ServicioReservaId): se conserva tal cual estaba.
        int? servicioReservaId = null;
        if (!string.IsNullOrWhiteSpace(request.ServicioReservaId))
        {
            servicioReservaId = await _dbContext.Servicios
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ServicioReservaId, cancellationToken);
        }

        // ADR-036 4c: resolver y validar la referencia polimorfica al servicio (recordKind + publicId).
        // El servicio debe existir, pertenecer a ESTE proveedor y estar en ESTA reserva. Si no, se rechaza
        // (no se imputa un pago a un servicio de otro operador / otra reserva).
        Guid? servicePublicId = null;
        if (hasService)
        {
            var resolvedService = await ResolveServiceForSupplierPaymentAsync(
                supplierId, reservaId.Value, normalizedServiceKind!, request.ServicePublicId!, cancellationToken);
            servicePublicId = resolvedService.PublicId;

            // (2026-06-26) Cuando el pago se imputa a UN servicio concreto se valida que la moneda del pago
            // COINCIDA con la del costo del servicio (un servicio en USD se paga en USD). El TOPE por monto se
            // ELIMINO (decision del dueño): un pago al servicio PUEDE exceder su costo pendiente; el excedente
            // queda como saldo a favor con el operador en esa moneda (misma logica que el pago imputado a la
            // reserva). La moneda no se mezcla nunca (la imputacion respeta la moneda del pago).
            EnsureServicePaymentCurrencyMatchesService(resolvedService, currency);
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
        string imputedCurrency = Monedas.Normalizar(currency.ImputedCurrency ?? currency.Currency);

        // (2026-06-26, decision del dueño) El pago imputado a una reserva PUEDE EXCEDER la deuda de esa reserva en
        // su moneda: el excedente queda como SALDO A FAVOR con el operador EN ESA MONEDA (igual que un anticipo "a
        // cuenta"). Se ELIMINO el tope superior por monto. La aislacion por moneda es estructural: el pago imputa
        // SOLO al bucket de su moneda en SupplierDebtCalculator, asi que un pago USD nunca toca la deuda ARS; el
        // excedente se refleja como balance NEGATIVO de esa moneda tanto en la linea de la reserva
        // (BuildSupplierDebtByReserva) como en el total del operador (la reconciliacion por moneda se mantiene).
        //
        // SE MANTIENE la coherencia de moneda (analoga al match de moneda del pago por servicio): la moneda
        // imputada debe ser una en la que la reserva TIENE actividad con este operador. Imputar un pago en una
        // moneda con la que la reserva no opera (p.ej. USD a una reserva que solo tiene costos ARS) se rechaza:
        // para un prepago en otra moneda esta el pago "a cuenta" (sin reserva), que no se cruza con la reserva.
        if (!supplierDebtInReserva.ContainsKey(imputedCurrency))
        {
            throw new InvalidOperationException(
                "El pago no coincide con ninguna moneda de la deuda de este proveedor en la reserva.");
        }

        return new SupplierPaymentImputationResult(
            reservaId, servicioReservaId, hasService ? normalizedServiceKind : null, servicePublicId);
    }

    /// <summary>
    /// Resultado de resolver la imputacion de un pago a proveedor: a que reserva, que servicio generico
    /// legacy y (ADR-036 4c) que servicio concreto polimorfico (recordKind + publicId). Todos null = anticipo.
    /// </summary>
    private readonly record struct SupplierPaymentImputationResult(
        int? ReservaId, int? ServicioReservaId, string? ServiceRecordKind, Guid? ServicePublicId)
    {
        public static SupplierPaymentImputationResult AdvanceToAccount => new(null, null, null, null);
    }

    /// <summary>
    /// ADR-036 4c: resuelve un servicio por (recordKind, publicId) y valida que exista, sea de ESTE proveedor
    /// y de ESTA reserva. Devuelve el PublicId confirmado (id polimorfico que se persiste en el pago) junto con
    /// el COSTO y la MONEDA del servicio, que luego se usan para validar moneda y tope por servicio.
    /// Lanza <see cref="InvalidOperationException"/> si el servicio no cumple (el controller lo traduce a 400).
    /// </summary>
    private async Task<ResolvedServiceForPayment> ResolveServiceForSupplierPaymentAsync(
        int supplierId, int reservaId, string recordKind, string servicePublicIdRaw, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(servicePublicIdRaw, out var servicePublicId))
        {
            throw new ArgumentException("El identificador del servicio no es valido.");
        }

        // Cada tipo vive en su propia tabla; consultamos solo la que corresponde al recordKind. La condicion
        // es siempre la misma: mismo PublicId, mismo proveedor, misma reserva. Traemos costo + moneda (no solo
        // existencia) para validar despues moneda/tope por servicio. Todas las ramas proyectan la MISMA forma
        // anonima { NetCost, Currency }, asi el switch unifica el tipo. AsNoTracking (solo leemos para validar).
        var match = recordKind switch
        {
            ServicePaymentRecordKinds.Flight => await _dbContext.FlightSegments.AsNoTracking()
                .Where(s => s.PublicId == servicePublicId && s.SupplierId == supplierId && s.ReservaId == reservaId)
                .Select(s => new { s.NetCost, s.Currency }).FirstOrDefaultAsync(cancellationToken),
            ServicePaymentRecordKinds.Hotel => await _dbContext.HotelBookings.AsNoTracking()
                .Where(s => s.PublicId == servicePublicId && s.SupplierId == supplierId && s.ReservaId == reservaId)
                .Select(s => new { s.NetCost, s.Currency }).FirstOrDefaultAsync(cancellationToken),
            ServicePaymentRecordKinds.Transfer => await _dbContext.TransferBookings.AsNoTracking()
                .Where(s => s.PublicId == servicePublicId && s.SupplierId == supplierId && s.ReservaId == reservaId)
                .Select(s => new { s.NetCost, s.Currency }).FirstOrDefaultAsync(cancellationToken),
            ServicePaymentRecordKinds.Package => await _dbContext.PackageBookings.AsNoTracking()
                .Where(s => s.PublicId == servicePublicId && s.SupplierId == supplierId && s.ReservaId == reservaId)
                .Select(s => new { s.NetCost, s.Currency }).FirstOrDefaultAsync(cancellationToken),
            ServicePaymentRecordKinds.Assistance => await _dbContext.AssistanceBookings.AsNoTracking()
                .Where(s => s.PublicId == servicePublicId && s.SupplierId == supplierId && s.ReservaId == reservaId)
                .Select(s => new { s.NetCost, s.Currency }).FirstOrDefaultAsync(cancellationToken),
            // El generico guarda SupplierId nullable; exigimos que coincida igual.
            ServicePaymentRecordKinds.Generic => await _dbContext.Servicios.AsNoTracking()
                .Where(s => s.PublicId == servicePublicId && s.SupplierId == supplierId && s.ReservaId == reservaId)
                .Select(s => new { s.NetCost, s.Currency }).FirstOrDefaultAsync(cancellationToken),
            _ => null
        };

        if (match is null)
        {
            throw new InvalidOperationException(
                "El servicio indicado no existe, no es de este proveedor o no pertenece a la reserva.");
        }

        // La moneda del servicio se normaliza (null -> ARS) igual que en la vista de estado por servicio.
        return new ResolvedServiceForPayment(servicePublicId, match.NetCost, Monedas.Normalizar(match.Currency));
    }

    /// <summary>Servicio resuelto para imputar un pago: su PublicId + el costo y la moneda contra los que se valida.</summary>
    private readonly record struct ResolvedServiceForPayment(Guid PublicId, decimal NetCost, string Currency);

    /// <summary>
    /// (2026-06-26) Valida que un pago imputado a UN servicio concreto sea EN LA MONEDA del costo del servicio
    /// (un servicio en USD se paga en USD, o con un pago cruzado cuyo equivalente imputado es USD). Es la unica
    /// validacion por servicio: el TOPE por monto se elimino (decision del dueño) — un pago PUEDE exceder el costo
    /// del servicio y el excedente queda como saldo a favor con el operador en esa moneda. Lanza
    /// <see cref="InvalidOperationException"/> (el controller la traduce a 400 con un mensaje generico).
    /// </summary>
    private static void EnsureServicePaymentCurrencyMatchesService(
        ResolvedServiceForPayment service,
        PaymentCurrencyResolver.Resolved currency)
    {
        // Moneda EFECTIVAMENTE imputada al servicio: la imputada si el pago cruzo, si no la propia del pago.
        string imputedCurrency = Monedas.Normalizar(currency.ImputedCurrency ?? currency.Currency);

        if (!string.Equals(imputedCurrency, service.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "La moneda del pago no coincide con la del costo del servicio.");
        }
    }

    /// <summary>
    /// ADR-022 §4 P4: deuda de un proveedor SEPARADA por moneda PERO acotada a UNA reserva. Mismo motor puro
    /// (<see cref="SupplierDebtCalculator"/>: compras confirmadas - pagos imputados, por moneda) que el resto de
    /// la cuenta del proveedor, solo que tanto los servicios como los pagos se filtran por <paramref name="reservaId"/>.
    /// <paramref name="excludePaymentId"/> saca el pago que se esta editando del "ya pagado".
    /// </summary>
    private async Task<IReadOnlyDictionary<string, SupplierDebtLine>> CalculateSupplierDebtInReservaAsync(
        int supplierId, int reservaId, int? excludePaymentId, CancellationToken cancellationToken)
    {
        // Servicios de este proveedor en ESTA reserva, por tipo/estado/moneda. Reusar el calculador puro
        // mantiene la cuenta identica a la global, solo que acotada a la reserva.
        // (2026-06-26) CommissionOnly -> deuda de compra CERO tambien por reserva (intermediacion): sin compras
        // confirmadas no se puede imputar un pago a la reserva de ese operador (no hay nada que pagar). Mismo
        // gate que el calculo global, asi la validacion de pago por reserva coincide con la deuda materializada.
        IEnumerable<SupplierDebtCalculator.ConfirmedPurchase> confirmedPurchases;
        if (await SupplierGeneratesPurchaseDebtAsync(supplierId, cancellationToken))
        {
            var serviceRows = await BuildSupplierServiceDebtRowsInReservaAsync(supplierId, reservaId, cancellationToken);
            confirmedPurchases = serviceRows
                .Where(r => WorkflowStatusHelper.CountsForSupplierDebtByType(r.Type, r.Status))
                .Select(r => new SupplierDebtCalculator.ConfirmedPurchase(r.Currency, r.NetCost))
                .ToList();
        }
        else
        {
            confirmedPurchases = Array.Empty<SupplierDebtCalculator.ConfirmedPurchase>();
        }

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
        // (2026-06-26) CommissionOnly (intermediacion) no genera deuda de compra: ninguna reserva acumula
        // Cuenta por Pagar por sus servicios. Dejamos las compras confirmadas vacias (los pagos/anticipos que
        // existieran siguen apareciendo y reconcilian igual). Mismo gate que el calculo global por moneda.
        var generatesPurchaseDebt = await SupplierGeneratesPurchaseDebtAsync(id, cancellationToken);
        var serviceRows = generatesPurchaseDebt
            ? await BuildSupplierServicesQuery(id).ToListAsync(cancellationToken)
            : new List<SupplierAccountServiceListItemDto>();
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

        // ADR-041 TANDA 3: saldo a favor del operador APLICADO a reservas (neto Applied - Reversed), por reserva
        // y moneda. Baja la deuda-por-reserva del destino SIN mover caja. Traemos la identidad de la reserva
        // destino para poder crear su linea aunque no tenga compras/pagos vivos (caso raro pero reconcilia igual).
        var creditApplicationRows = await _dbContext.SupplierCreditApplications
            .Where(application => application.Entry.SupplierId == id)
            .Select(application => new SupplierCreditApplicationRow(
                application.Kind,
                application.Amount,
                application.Entry.Currency,
                application.TargetReserva!.PublicId,
                application.TargetReserva!.NumeroReserva,
                application.TargetReserva!.Name))
            .ToListAsync(cancellationToken);

        var result = BuildSupplierDebtByReserva(
            supplier.PublicId, supplier.Name, confirmedServiceRows, paymentRows, creditApplicationRows);

        // Masking see_cost: sin permiso, la estructura queda visible pero todos los montos en 0.
        if (!await CanSeeSupplierCostFiguresAsync(cancellationToken))
        {
            MaskSupplierDebtByReservaAmounts(result);
        }

        return result;
    }

    /// <summary>
    /// ADR-041 TANDA 3: fila minima de una aplicacion/reversa de saldo a favor del operador para el desglose por
    /// reserva. <see cref="Kind"/> da el signo economico (Applied baja la deuda destino; Reversed la repone).
    /// </summary>
    private readonly record struct SupplierCreditApplicationRow(
        SupplierCreditApplicationKind Kind,
        decimal Amount,
        string? Currency,
        Guid ReservaPublicId,
        string? NumeroReserva,
        string? FileName);

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
        IReadOnlyList<SupplierPaymentImputationRow> paymentRows,
        IReadOnlyList<SupplierCreditApplicationRow> creditApplicationRows)
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

        // --- Saldo a favor del operador APLICADO a reservas (ADR-041 TANDA 3) ---
        // Neto por reserva+moneda (Applied suma, Reversed resta). Baja la deuda-por-reserva del destino sin
        // mover caja. Total por moneda para el bucket de reconciliacion + el offset que mantiene GlobalTotals.
        var creditAppliedByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var application in creditApplicationRows)
        {
            string currency = Monedas.Normalizar(application.Currency);
            // Una Reversed deshace una Applied: signo opuesto. Asi el neto refleja lo realmente aplicado.
            decimal signedAmount = application.Kind == SupplierCreditApplicationKind.Applied
                ? application.Amount
                : -application.Amount;

            // La reserva destino tiene servicios del operador (lo valida el apply), pero por si quedo sin
            // compras vivas, la creamos para que su linea aparezca y la reconciliacion cierre.
            if (!reservaPurchases.TryGetValue(application.ReservaPublicId, out var accumulator))
            {
                accumulator = new ReservaDebtAccumulator(
                    application.ReservaPublicId, application.NumeroReserva, application.FileName);
                reservaPurchases[application.ReservaPublicId] = accumulator;
            }

            accumulator.AddCreditApplied(currency, signedAmount);
            AccumulateCurrency(creditAppliedByCurrency, currency, signedAmount);
        }

        // --- Materializar el DTO ---
        var dto = new SupplierDebtByReservaDto
        {
            SupplierPublicId = supplierPublicId,
            SupplierName = supplierName
        };

        // Totales globales de reconciliacion: se acumulan sumando TODAS las lineas por moneda (reservas +
        // anticipos + saldo a favor aplicado). Por construccion esto iguala compras totales - pagos totales por
        // moneda (caja), que es el mismo numero que el calculo global de la cuenta corriente: una aplicacion de
        // saldo a favor es NETO-CERO a nivel global (baja la reserva destino y se suma de vuelta en el offset).
        var globalByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var accumulator in reservaPurchases.Values)
        {
            var reservaLine = new SupplierDebtReservaLineDto
            {
                ReservaPublicId = accumulator.ReservaPublicId,
                NumeroReserva = accumulator.NumeroReserva,
                FileName = accumulator.FileName
            };

            foreach (var line in accumulator.ToLines())
            {
                reservaLine.Currencies.Add(new SupplierDebtCurrencyLineDto
                {
                    Currency = line.Currency,
                    ConfirmedPurchases = EconomicRulesHelper.RoundCurrency(line.ConfirmedPurchases),
                    TotalPaid = EconomicRulesHelper.RoundCurrency(line.TotalPaid),
                    CreditApplied = EconomicRulesHelper.RoundCurrency(line.CreditApplied),
                    Balance = EconomicRulesHelper.RoundCurrency(line.Balance)
                });

                AccumulateCurrency(globalByCurrency, line.Currency, line.Balance);
            }

            dto.Reservas.Add(reservaLine);
        }

        // Offset: el saldo a favor aplicado bajo la deuda de las reservas destino; lo sumamos de vuelta al
        // global para que GlobalTotals siga igualando la deuda de CAJA (compras - pagos). El bucket
        // CreditAppliedFromBalance lo expone explicito para la reconciliacion del front.
        foreach (var (currency, amount) in creditAppliedByCurrency)
        {
            if (amount == 0m) continue;
            dto.CreditAppliedFromBalance.Add(new SupplierDebtCurrencyAmountDto
            {
                Currency = currency,
                Amount = EconomicRulesHelper.RoundCurrency(amount)
            });
            AccumulateCurrency(globalByCurrency, currency, amount);
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

        // ADR-041 TANDA 3: saldo a favor del operador aplicado a esta reserva en una moneda (neto). Baja la
        // deuda exigible sin ser caja, por eso va en su propio bucket (no se mezcla con TotalPaid = caja real).
        private readonly Dictionary<string, decimal> _creditAppliedByCurrency = new(StringComparer.Ordinal);

        public void AddCreditApplied(string currency, decimal amount)
        {
            _creditAppliedByCurrency.TryGetValue(currency, out var current);
            _creditAppliedByCurrency[currency] = current + amount;
        }

        /// <summary>
        /// Devuelve una linea por cada moneda presente en compras, pagos o saldo a favor aplicado. El saldo
        /// (<c>Balance</c>) ya descuenta el saldo a favor aplicado: <c>compras - pagado - creditoAplicado</c>.
        /// </summary>
        public IEnumerable<(string Currency, decimal ConfirmedPurchases, decimal TotalPaid, decimal CreditApplied, decimal Balance)> ToLines()
        {
            var currencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in _purchasesByCurrency.Keys) currencies.Add(key);
            foreach (var key in _paidByCurrency.Keys) currencies.Add(key);
            foreach (var key in _creditAppliedByCurrency.Keys) currencies.Add(key);

            foreach (var currency in currencies)
            {
                _purchasesByCurrency.TryGetValue(currency, out var purchases);
                _paidByCurrency.TryGetValue(currency, out var paid);
                _creditAppliedByCurrency.TryGetValue(currency, out var creditApplied);
                decimal balance = purchases - paid - creditApplied;
                yield return (currency, purchases, paid, creditApplied, balance);
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
                currencyLine.CreditApplied = 0m;
                currencyLine.Balance = 0m;
            }
        }

        foreach (var advance in dto.AdvancesToAccount)
        {
            advance.Amount = 0m;
        }

        foreach (var creditApplied in dto.CreditAppliedFromBalance)
        {
            creditApplied.Amount = 0m;
        }

        foreach (var total in dto.GlobalTotals)
        {
            total.Amount = 0m;
        }
    }

    // ===================================================================================================
    // ADR-036 punto 4c (2026-06-23): estado "pagado al operador" POR SERVICIO de una reserva.
    // ===================================================================================================

    /// <summary>
    /// Estado de pago al operador de todos los servicios de una reserva. Por cada servicio (de las 6 tablas)
    /// suma los pagos VIVOS al operador imputados a ese servicio puntual (ADR-036 4c) y deriva paid/partial/
    /// unpaid contra su costo. Los montos respetan el masking see_cost; el estado lo ven todos.
    /// </summary>
    public async Task<ReservaSupplierPaymentStatusDto> GetReservaSupplierPaymentStatusAsync(
        int reservaId, CancellationToken cancellationToken)
    {
        var reserva = await _dbContext.Reservas
            .AsNoTracking()
            .Where(r => r.Id == reservaId)
            .Select(r => new { r.PublicId, r.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (reserva == null)
        {
            throw new KeyNotFoundException("Reserva no encontrada");
        }

        // H4 (2026-06-24): la deuda con el operador / el aviso "operador impago" SOLO tiene sentido en estados
        // donde un servicio se concreto con el operador (firme + vivo/finalizado): {InManagement, Confirmed,
        // Traveling, Closed}. En PRE-VENTA (Cotizacion/Presupuesto) nada se concreto: un presupuesto no genera
        // deuda con proveedores (regla G2). En los TERMINALES SIN deuda operativa (Perdida = no se compro (G1);
        // Anulada / Esperando reembolso = la deuda se resuelve por el circuito de cancelacion/refund) tampoco
        // aplica. En esos estados devolvemos la lista de servicios VACIA: el front no muestra ningun chip "impago"
        // (antes reportaba TODOS como "unpaid", que era el bug — un presupuesto aparecia con el operador "impago").
        //
        // Fuente unica de la regla: el MISMO conjunto que ya usa el calculo de la cuenta corriente del proveedor
        // (SupplierDebtCalculator.ValidReservationStatuses, via el alias ValidReservationStatuses de esta clase).
        // Asi este endpoint de "estado de pago por servicio" y la deuda agregada del proveedor coinciden: una
        // reserva que NO suma a la deuda del operador tampoco reporta servicios impagos.
        var supplierDebtApplies = ValidReservationStatuses
            .Any(s => string.Equals(s, reserva.Status, StringComparison.OrdinalIgnoreCase));
        if (!supplierDebtApplies)
        {
            return new ReservaSupplierPaymentStatusDto
            {
                ReservaPublicId = reserva.PublicId,
                AmountsVisible = await CanSeeSupplierCostFiguresAsync(cancellationToken),
                // Services queda vacio: en este estado no se reporta pago/deuda con el operador.
            };
        }

        // 1) Todos los servicios de la reserva (6 tablas), con su proveedor, costo, moneda y estado.
        var serviceRows = await BuildReservaServiceRowsAsync(reservaId, cancellationToken);

        // 2) Pagos vivos al operador imputados a UN servicio de esta reserva. El query filter !IsDeleted ya
        //    excluye los anulados (self-healing). Sumamos el equivalente imputado por servicio (ImputedAmount
        //    si el pago cruzo moneda, si no el Amount), igual criterio que el resto de la cuenta del proveedor.
        var paymentRows = await _dbContext.SupplierPayments
            .Where(p => p.ReservaId == reservaId && p.ServicePublicId != null)
            .Select(p => new { p.ServicePublicId, p.Amount, p.ImputedAmount })
            .ToListAsync(cancellationToken);

        var paidByService = new Dictionary<Guid, decimal>();
        foreach (var payment in paymentRows)
        {
            if (payment.ServicePublicId is not Guid servicePublicId) continue;
            decimal imputedAmount = payment.ImputedAmount ?? payment.Amount;
            paidByService.TryGetValue(servicePublicId, out var current);
            paidByService[servicePublicId] = current + imputedAmount;
        }

        // 3) Saldo a favor con el operador APLICADO a ESTA reserva (bug 2026-07-03). La aplicacion se hace a nivel
        //    RESERVA (SupplierCreditApplication.TargetReservaId), pero el estado "pagado al operador" es POR
        //    SERVICIO. Atribuimos el monto aplicado a los servicios de la MISMA moneda y el MISMO operador en
        //    orden cronologico (FIFO por CreatedAt), mismo espiritu que el drenaje FIFO del pool. Si el saldo a
        //    favor aplicado cubre toda la deuda de la reserva, todos sus servicios quedan "paid" (el caso reportado
        //    por el dueño: cubrio la deuda con saldo a favor y los servicios seguian "impagos"). Va DESPUES de los
        //    pagos de caja: el credito solo cubre lo que el efectivo dejo pendiente.
        var creditAppliedByService = await AttributeSupplierCreditToServicesAsync(
            reservaId, serviceRows, paidByService, cancellationToken);

        var dto = new ReservaSupplierPaymentStatusDto
        {
            ReservaPublicId = reserva.PublicId,
            AmountsVisible = await CanSeeSupplierCostFiguresAsync(cancellationToken)
        };

        foreach (var service in serviceRows)
        {
            // (2026-06-26) Coherente con AGUJERO 1: un servicio de proveedor CommissionOnly (intermediacion) NO
            // genera deuda con el operador (el operador factura directo al cliente, la agencia no compra). Por eso
            // NO se reporta estado de pago al operador para ese servicio: queda FUERA del listado, igual que en
            // pre-venta el front no muestra ningun chip. Asi no aparece un "operador impago" espurio. Reusa la
            // regla unica de dominio. Un servicio sin proveedor (modo null) se reporta como antes (no se cambia).
            if (service.SupplierInvoicingMode.HasValue
                && !SupplierDebtCalculator.SupplierGeneratesPurchaseDebt(service.SupplierInvoicingMode.Value))
            {
                continue;
            }

            paidByService.TryGetValue(service.PublicId, out var paid);
            creditAppliedByService.TryGetValue(service.PublicId, out var creditApplied);
            decimal netCost = EconomicRulesHelper.RoundCurrency(service.NetCost);
            paid = EconomicRulesHelper.RoundCurrency(paid);
            creditApplied = EconomicRulesHelper.RoundCurrency(creditApplied);

            // "Cubierto" = pagos de caja + saldo a favor aplicado. El estado y el pendiente se derivan de este
            // total: un servicio cubierto por saldo a favor esta saldado con el operador (paid), aunque no haya
            // efectivo. PaidToOperator sigue siendo SOLO caja; el credito se expone aparte para no confundir.
            decimal covered = EconomicRulesHelper.RoundCurrency(paid + creditApplied);
            decimal outstanding = EconomicRulesHelper.RoundCurrency(netCost - covered);

            // El estado se deriva ANTES de enmascarar (no depende de ver montos): cubierto / algo / nada.
            string status = DeriveOperatorPaymentStatus(netCost, covered);

            var line = new ServiceSupplierPaymentStatusDto
            {
                RecordKind = service.RecordKind,
                ServicePublicId = service.PublicId,
                SupplierPublicId = service.SupplierPublicId,
                SupplierName = service.SupplierName,
                Currency = Monedas.Normalizar(service.Currency),
                NetCost = netCost,
                PaidToOperator = paid,
                CreditAppliedToOperator = creditApplied,
                OutstandingToOperator = outstanding,
                Status = status
            };

            // Masking see_cost: el estado queda visible, los montos se anulan (decision ADR-036 P4=B).
            if (!dto.AmountsVisible)
            {
                line.NetCost = 0m;
                line.PaidToOperator = 0m;
                line.CreditAppliedToOperator = 0m;
                line.OutstandingToOperator = 0m;
            }

            dto.Services.Add(line);
        }

        return dto;
    }

    /// <summary>
    /// Atribuye a cada servicio el saldo a favor con el operador aplicado a la reserva (bug 2026-07-03). Las
    /// aplicaciones (<see cref="SupplierCreditApplication"/>) son a nivel RESERVA + moneda + operador; aca las
    /// reparte entre los servicios de la MISMA moneda y operador, en orden cronologico (FIFO por CreatedAt),
    /// cubriendo primero lo que el efectivo dejo pendiente en cada servicio. El total repartido nunca supera el
    /// saldo a favor aplicado (que a su vez, por el tope de <c>ApplyCreditAsync</c>, nunca supera la deuda viva de
    /// la reserva). Devuelve un mapa servicioPublicId -> monto de saldo a favor atribuido (0 si no le toco).
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> AttributeSupplierCreditToServicesAsync(
        int reservaId,
        List<ReservaServicePaymentRow> serviceRows,
        Dictionary<Guid, decimal> paidByService,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, decimal>();

        // Saldo a favor NETO (Applied - Reversed) aplicado a esta reserva, por (operador, moneda). Una reversa
        // deja su aplicacion en cero, asi que revertir devuelve el estado del servicio a impago (simetria).
        var applicationRows = await _dbContext.SupplierCreditApplications
            .AsNoTracking()
            .Where(a => a.TargetReservaId == reservaId)
            .Select(a => new { a.Kind, a.Amount, a.Entry.SupplierId, a.Entry.Currency })
            .ToListAsync(cancellationToken);
        if (applicationRows.Count == 0) return result;

        var remainingByKey = new Dictionary<(int SupplierId, string Currency), decimal>();
        foreach (var row in applicationRows)
        {
            var key = (row.SupplierId, Monedas.Normalizar(row.Currency));
            decimal signed = row.Kind == SupplierCreditApplicationKind.Applied ? row.Amount : -row.Amount;
            remainingByKey.TryGetValue(key, out var acc);
            remainingByKey[key] = acc + signed;
        }

        // Solo claves con saldo positivo quedan para repartir.
        var keysToDrain = remainingByKey
            .Where(kvp => Math.Round(kvp.Value, 2, MidpointRounding.AwayFromZero) > 0m)
            .Select(kvp => kvp.Key)
            .ToHashSet();
        if (keysToDrain.Count == 0) return result;

        // FIFO cronologico: se cubren primero los servicios mas antiguos (mismo espiritu que el drenaje del pool).
        // Un servicio de operador CommissionOnly no genera deuda: no recibe credito (coherente con el loop que
        // arma la respuesta, que tambien lo excluye). Un servicio sin operador no tiene clave: se ignora.
        var orderedServices = serviceRows
            .Where(s => s.SupplierId.HasValue)
            .Where(s => !s.SupplierInvoicingMode.HasValue
                     || SupplierDebtCalculator.SupplierGeneratesPurchaseDebt(s.SupplierInvoicingMode.Value))
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.PublicId);

        foreach (var service in orderedServices)
        {
            var key = (service.SupplierId!.Value, Monedas.Normalizar(service.Currency));
            if (!remainingByKey.TryGetValue(key, out var remaining)) continue;
            remaining = Math.Round(remaining, 2, MidpointRounding.AwayFromZero);
            if (remaining <= 0m) continue;

            paidByService.TryGetValue(service.PublicId, out var cashPaid);
            decimal outstandingAfterCash = Math.Round(service.NetCost - cashPaid, 2, MidpointRounding.AwayFromZero);
            if (outstandingAfterCash <= 0m) continue;

            decimal take = Math.Min(remaining, outstandingAfterCash);
            result[service.PublicId] = take;
            remainingByKey[key] = Math.Round(remaining - take, 2, MidpointRounding.AwayFromZero);
        }

        return result;
    }

    /// <summary>
    /// Deriva el estado de pago al operador de un servicio: <c>paid</c> si lo pagado cubre el costo,
    /// <c>partial</c> si se pago algo pero no todo, <c>unpaid</c> si no se pago nada. Un servicio sin costo
    /// (NetCost &lt;= 0) se reporta unpaid (no hay nada que pagar todavia, no se marca como "pagado").
    /// </summary>
    private static string DeriveOperatorPaymentStatus(decimal netCost, decimal paid)
    {
        if (paid <= 0m) return ServiceSupplierPaymentStatuses.Unpaid;
        if (netCost <= 0m) return ServiceSupplierPaymentStatuses.Unpaid;
        if (paid >= netCost) return ServiceSupplierPaymentStatuses.Paid;
        return ServiceSupplierPaymentStatuses.Partial;
    }

    /// <summary>
    /// Fila minima de un servicio de la reserva para el estado de pago al operador.
    /// (2026-06-26) <see cref="SupplierInvoicingMode"/> nullable: el modo de facturacion del proveedor del
    /// servicio (null si el servicio no tiene proveedor). Se usa para NO reportar estado de pago al operador
    /// en servicios de proveedores CommissionOnly (intermediacion: no hay deuda con el operador).
    /// </summary>
    private readonly record struct ReservaServicePaymentRow(
        string RecordKind, Guid PublicId, Guid? SupplierPublicId, int? SupplierId, string? SupplierName,
        decimal NetCost, string? Currency, string Status, SupplierInvoicingMode? SupplierInvoicingMode,
        DateTime CreatedAt);

    /// <summary>
    /// Reune todos los servicios de una reserva (6 tablas) con su (recordKind, publicId, proveedor, costo,
    /// moneda, estado). Mismo patron de union en memoria que <c>BuildSupplierServiceDebtRowsAsync</c>
    /// (el provider InMemory no traduce Concat sobre proyecciones; el volumen por reserva es chico).
    /// </summary>
    private async Task<List<ReservaServicePaymentRow>> BuildReservaServiceRowsAsync(
        int reservaId, CancellationToken cancellationToken)
    {
        var rows = new List<ReservaServicePaymentRow>();

        var flights = await _dbContext.FlightSegments.AsNoTracking()
            .Where(s => s.ReservaId == reservaId)
            .Select(s => new { s.PublicId, s.Supplier, s.NetCost, s.Currency, s.Status, s.CreatedAt })
            .ToListAsync(cancellationToken);
        rows.AddRange(flights.Select(s => new ReservaServicePaymentRow(
            ServicePaymentRecordKinds.Flight, s.PublicId,
            s.Supplier != null ? (Guid?)s.Supplier.PublicId : null,
            s.Supplier != null ? (int?)s.Supplier.Id : null,
            s.Supplier != null ? s.Supplier.Name : null,
            s.NetCost, s.Currency, s.Status,
            s.Supplier != null ? (SupplierInvoicingMode?)s.Supplier.InvoicingMode : null,
            s.CreatedAt)));

        var hotels = await _dbContext.HotelBookings.AsNoTracking()
            .Where(s => s.ReservaId == reservaId)
            .Select(s => new { s.PublicId, s.Supplier, s.NetCost, s.Currency, s.Status, s.CreatedAt })
            .ToListAsync(cancellationToken);
        rows.AddRange(hotels.Select(s => new ReservaServicePaymentRow(
            ServicePaymentRecordKinds.Hotel, s.PublicId,
            s.Supplier != null ? (Guid?)s.Supplier.PublicId : null,
            s.Supplier != null ? (int?)s.Supplier.Id : null,
            s.Supplier != null ? s.Supplier.Name : null,
            s.NetCost, s.Currency, s.Status,
            s.Supplier != null ? (SupplierInvoicingMode?)s.Supplier.InvoicingMode : null,
            s.CreatedAt)));

        var transfers = await _dbContext.TransferBookings.AsNoTracking()
            .Where(s => s.ReservaId == reservaId)
            .Select(s => new { s.PublicId, s.Supplier, s.NetCost, s.Currency, s.Status, s.CreatedAt })
            .ToListAsync(cancellationToken);
        rows.AddRange(transfers.Select(s => new ReservaServicePaymentRow(
            ServicePaymentRecordKinds.Transfer, s.PublicId,
            s.Supplier != null ? (Guid?)s.Supplier.PublicId : null,
            s.Supplier != null ? (int?)s.Supplier.Id : null,
            s.Supplier != null ? s.Supplier.Name : null,
            s.NetCost, s.Currency, s.Status,
            s.Supplier != null ? (SupplierInvoicingMode?)s.Supplier.InvoicingMode : null,
            s.CreatedAt)));

        var packages = await _dbContext.PackageBookings.AsNoTracking()
            .Where(s => s.ReservaId == reservaId)
            .Select(s => new { s.PublicId, s.Supplier, s.NetCost, s.Currency, s.Status, s.CreatedAt })
            .ToListAsync(cancellationToken);
        rows.AddRange(packages.Select(s => new ReservaServicePaymentRow(
            ServicePaymentRecordKinds.Package, s.PublicId,
            s.Supplier != null ? (Guid?)s.Supplier.PublicId : null,
            s.Supplier != null ? (int?)s.Supplier.Id : null,
            s.Supplier != null ? s.Supplier.Name : null,
            s.NetCost, s.Currency, s.Status,
            s.Supplier != null ? (SupplierInvoicingMode?)s.Supplier.InvoicingMode : null,
            s.CreatedAt)));

        var assistances = await _dbContext.AssistanceBookings.AsNoTracking()
            .Where(s => s.ReservaId == reservaId)
            .Select(s => new { s.PublicId, s.Supplier, s.NetCost, s.Currency, s.Status, s.CreatedAt })
            .ToListAsync(cancellationToken);
        rows.AddRange(assistances.Select(s => new ReservaServicePaymentRow(
            ServicePaymentRecordKinds.Assistance, s.PublicId,
            s.Supplier != null ? (Guid?)s.Supplier.PublicId : null,
            s.Supplier != null ? (int?)s.Supplier.Id : null,
            s.Supplier != null ? s.Supplier.Name : null,
            s.NetCost, s.Currency, s.Status,
            s.Supplier != null ? (SupplierInvoicingMode?)s.Supplier.InvoicingMode : null,
            s.CreatedAt)));

        var generics = await _dbContext.Servicios.AsNoTracking()
            .Where(s => s.ReservaId == reservaId)
            .Select(s => new { s.PublicId, s.Supplier, s.NetCost, s.Currency, s.Status, s.CreatedAt })
            .ToListAsync(cancellationToken);
        rows.AddRange(generics.Select(s => new ReservaServicePaymentRow(
            ServicePaymentRecordKinds.Generic, s.PublicId,
            s.Supplier != null ? (Guid?)s.Supplier.PublicId : null,
            s.Supplier != null ? (int?)s.Supplier.Id : null,
            s.Supplier != null ? s.Supplier.Name : null,
            s.NetCost, s.Currency, s.Status,
            s.Supplier != null ? (SupplierInvoicingMode?)s.Supplier.InvoicingMode : null,
            s.CreatedAt)));

        return rows;
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
