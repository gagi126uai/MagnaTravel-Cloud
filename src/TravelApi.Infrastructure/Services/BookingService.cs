using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using TravelApi.Application.Constants;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Interfaces;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

public partial class BookingService : IBookingService
{
    private readonly IRepository<FlightSegment> _flightRepo;
    private readonly IRepository<HotelBooking> _hotelRepo;
    private readonly IRepository<PackageBooking> _packageRepo;
    private readonly IRepository<TransferBooking> _transferRepo;
    private readonly IRepository<AssistanceBooking> _assistanceRepo;
    private readonly IRepository<Reserva> _fileRepo;
    private readonly IRepository<Supplier> _supplierRepo;
    private readonly IReservaService _reservaService;
    private readonly ISupplierService _supplierService;
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;
    private readonly ILogger<BookingService> _logger;
    // B1.15 Fase 0.2: dependencias opcionales para masking de costos en POST/PUT.
    // Mismo patron que ReservaService/InvoiceService/PaymentService — opcionales
    // para no romper los tests unitarios que instancian con el ctor de 11 args.
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    // ADR-017 F1.3: lo usa SOLO el path del catalogo find-or-create para leer el flag
    // EnableCatalogFindOrCreate + el setting StaleCostReferenceDays. Opcional para no romper los
    // ctores de tests existentes (14 args). Si es null -> flag OFF (byte-identico), fail-closed.
    private readonly IOperationalFinanceSettingsService? _settingsService;
    // ADR-031 v2.1: auditoria de la baja de asignaciones por cascada al borrar un servicio (M1).
    // Opcional para no romper los ctores de tests existentes; si es null, la limpieza igual ocurre
    // (la integridad NO depende del audit), solo no se registra el evento.
    private readonly IAuditService? _auditService;
    // Plata viva (familia R1, 2026-07-01): candado de reasignacion de operador/moneda de un servicio pagado sin
    // factura. Se reusa el criterio PRECISO del flujo de cancelacion (RefundCap del servicio, pool imputado a la
    // reserva -> excluye prepago a cuenta). Opcional para no romper los ctores de tests existentes; si es null el
    // candado no corre (mismo patron que ReservaService). En produccion la DI lo inyecta (esta registrado).
    // No hay ciclo de DI: BookingCancellationService no depende de IBookingService ni de IReservaService.
    private readonly IBookingCancellationService? _cancellationService;

    public BookingService(
        IRepository<FlightSegment> flightRepo,
        IRepository<HotelBooking> hotelRepo,
        IRepository<PackageBooking> packageRepo,
        IRepository<TransferBooking> transferRepo,
        IRepository<AssistanceBooking> assistanceRepo,
        IRepository<Reserva> fileRepo,
        IRepository<Supplier> supplierRepo,
        IReservaService reservaService,
        ISupplierService supplierService,
        AppDbContext db,
        IMapper mapper,
        ILogger<BookingService> logger,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IOperationalFinanceSettingsService? settingsService = null,
        IAuditService? auditService = null,
        IBookingCancellationService? cancellationService = null)
    {
        _flightRepo = flightRepo;
        _hotelRepo = hotelRepo;
        _packageRepo = packageRepo;
        _transferRepo = transferRepo;
        _assistanceRepo = assistanceRepo;
        _fileRepo = fileRepo;
        _supplierRepo = supplierRepo;
        _reservaService = reservaService;
        _supplierService = supplierService;
        _db = db;
        _mapper = mapper;
        _logger = logger;
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
        _settingsService = settingsService;
        _auditService = auditService;
        _cancellationService = cancellationService;
    }

    /// <summary>
    /// Plata viva (familia R1): candado compartido de reasignacion de operador/moneda de un servicio TIPADO. Se
    /// invoca en los 5 <c>Update*Async</c> tipados ANTES de persistir. Solo evalua cuando (a) cambio el operador
    /// (A != B, incluye pasar a "sin operador") o la moneda respecto del estado ANTES de editar, y (b) el servicio
    /// venia contando como compra confirmada del operador saliente (misma regla que la caja,
    /// <see cref="WorkflowStatusHelper.CountsForSupplierDebtByType"/>). El punto (b) evita un over-block: si el
    /// servicio no estaba confirmado, moverlo no baja la caja del operador -> no hay fuga, pero el RefundCap topeado
    /// por costo podria dar &gt; 0 y bloquear de mas. Delega en el nucleo de cancelacion, que lee el servicio VIEJO
    /// con AsNoTracking (el cambio todavia no se flusheo). Si <c>_cancellationService</c> es null (tests con ctor
    /// viejo) no corre.
    /// </summary>
    private async Task GuardOperatorOrCurrencyReassignmentAsync(
        int reservaId,
        CancellableServiceTable serviceTable,
        int serviceId,
        int oldSupplierId,
        int newSupplierId,
        string? oldCurrency,
        string? newCurrency,
        string debtServiceType,
        string? oldStatus,
        CancellationToken ct)
    {
        if (_cancellationService is null) return;
        if (oldSupplierId <= 0) return; // sin operador saliente no hay caja que pueda quedar colgada

        bool operatorChanged = newSupplierId != oldSupplierId;
        bool currencyChanged = !string.Equals(
            Monedas.Normalizar(oldCurrency), Monedas.Normalizar(newCurrency), StringComparison.Ordinal);
        if (!operatorChanged && !currencyChanged) return;

        // (b): solo si el servicio venia contando como compra confirmada del operador saliente.
        if (!WorkflowStatusHelper.CountsForSupplierDebtByType(debtServiceType, oldStatus)) return;

        await _cancellationService.EnsureServiceOperatorOrCurrencyChangeHasReceivableAnchorAsync(
            reservaId, serviceTable, serviceId,
            // Si cambio el operador, el mensaje habla del operador; si SOLO cambio la moneda, habla de la moneda.
            isCurrencyChange: currencyChanged && !operatorChanged, ct);
    }

    // === RATE RESOLUTION (Snapshot) ===
    // Resuelve el RateId público a interno y aplica snapshot de precios si corresponde.
    private async Task<int?> ResolveRateIdAsync(string? ratePublicIdOrLegacyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ratePublicIdOrLegacyId)) return null;

        var rateId = await _db.Set<Rate>().AsNoTracking()
            .ResolveInternalIdAsync(ratePublicIdOrLegacyId, ct);

        return rateId;
    }

    private async Task<Rate?> GetRateAsync(string? ratePublicIdOrLegacyId, CancellationToken ct)
    {
        var rateId = await ResolveRateIdAsync(ratePublicIdOrLegacyId, ct);
        if (!rateId.HasValue) return null;
        return await _db.Set<Rate>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == rateId.Value, ct);
    }

    /// <summary>
    /// ADR-020 F4 (candado): aplica la regla de candado a un write-path de servicio tipado.
    /// Resuelve el actor del HttpContext (null en tests) y delega en <see cref="ReservaLockGuard"/>.
    /// Si la reserva esta confirmada y no hay autorizacion viva, lanza
    /// <see cref="InvalidOperationException"/> (-> 409 Conflict); si la hay, registra el cambio.
    /// El registro se persiste junto con la mutacion (mismo AppDbContext scoped por request).
    /// </summary>
    /// <summary>
    /// ADR-035 (2026-06-19): PRIMERA COMPUERTA de toda mutacion de servicio tipado — candado por ESTADO.
    /// Si la reserva esta en un estado de solo lectura (Closed/Lost/Cancelled/PendingOperatorRefund),
    /// lanza <see cref="InvalidOperationException"/> (-> 409) ANTES del candado de autorizacion y de los
    /// guards fiscales. Delega en la fuente unica <see cref="ReservaCapacityRules.EnsureServicesEditableByStateAsync"/>.
    /// </summary>
    private Task GuardServicesEditableByStateAsync(int reservaId, CancellationToken ct)
        => ReservaCapacityRules.EnsureServicesEditableByStateAsync(_db, reservaId, ct);

    private Task GuardReservaLockAsync(int reservaId, string operation, string entityType, int? entityId, string? summary, CancellationToken ct)
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        var userId = user?.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        var userName = user?.FindFirstValue("FullName")
            ?? user?.FindFirstValue(System.Security.Claims.ClaimTypes.Name)
            ?? user?.Identity?.Name;
        return ReservaLockGuard.EnsureCanEditAsync(
            _db, reservaId, operation, userId, userName, entityType, entityId, summary, ct);
    }

    /// <summary>
    /// ADR-031 (2026-06-15): GATE UNICO de pasajeros nominales por servicio. Se invoca en TODOS los
    /// call-sites que pueden dejar un servicio RESUELTO/EMITIDO (no solo los Update*StatusAsync, sino
    /// tambien los Create*/Update* de edicion, que pueden hacer nacer un servicio ya "Confirmado" — el
    /// bypass B1 del ADR §2.4). Corre ANTES de persistir el status resuelto y ANTES de
    /// <c>UpdateBalanceAsync</c>, de modo que el motor automatico (ReservaAutoStateService) NUNCA vea
    /// "todo resuelto" con nombres faltantes y auto-confirme una reserva sin pasajeros nominales.
    ///
    /// <para><b>Por que un unico envoltorio</b>: centraliza la invocacion para no olvidar ningun
    /// call-site. Toda ruta nueva que resuelva un servicio DEBE llamar a este metodo. El test del
    /// bypass B1 (Adr031PassengerNominalGateTests) es la red de seguridad contra regresiones.</para>
    ///
    /// <para><b>Cuando NO bloquea</b>: el gate corre SOLO en la TRANSICION no-resuelto -> resuelto
    /// (ADR §7). Si el servicio NO queda resuelto, o si YA estaba resuelto antes de esta operacion (ej.
    /// editar el costo de un hotel que ya estaba "Confirmado"), no se exige nada nuevo: la cobertura
    /// nominal ya se valido cuando se resolvio, y re-validarla en cada edicion posterior seria friccion
    /// sin razon. Para las altas, <paramref name="serviceWasResolved"/> es false (nace ahora).</para>
    ///
    /// <para><b>v2.1 (set por servicio)</b>: el gate ya NO valida la reserva entera; valida el SET DEL
    /// SERVICIO que se esta resolviendo. El set se resuelve por <paramref name="assignmentServiceType"/>
    /// + <paramref name="serviceId"/> via <see cref="ResolveServiceSetAsync"/> (asignaciones explicitas
    /// si existen; si no, toda la reserva). En un ALTA el servicio todavia no tiene Id propio (se asigna
    /// despues del AddAsync) y no puede tener asignaciones, asi que el call-site pasa
    /// <paramref name="serviceId"/>=0 y el set queda en "toda la reserva" (default seguro, correcto).</para>
    ///
    /// <para><b>Seguridad</b>: el mensaje de error nunca incluye el numero de documento (lo garantiza
    /// PassengerNominalRules); tampoco se loguea aca.</para>
    /// </summary>
    private async Task EnsureNominalCoverageBeforeResolvingAsync(
        int reservaId,
        bool serviceWasResolved,
        bool serviceIsResolved,
        PassengerNominalRules.ServiceKind serviceKind,
        string assignmentServiceType,
        int serviceId,
        CancellationToken ct)
    {
        // Solo la TRANSICION no-resuelto -> resuelto exige nombres. Si no queda resuelto, o si ya lo
        // estaba (edicion sobre un servicio ya confirmado/emitido), no hay nada nuevo que validar.
        if (serviceWasResolved || !serviceIsResolved)
            return;

        var serviceSet = await ResolveServiceSetAsync(reservaId, assignmentServiceType, serviceId, ct);
        PassengerNominalRules.EnsureCovered(serviceSet, serviceKind);
    }

    /// <summary>
    /// ADR-031 v2.1 (§4.2): resuelve el SET de pasajeros de un servicio para el gate / voucher. Lee de la
    /// DB las dos colecciones (pasajeros de la reserva + ids asignados a este servicio) y delega la regla
    /// de seleccion en el helper PURO <see cref="PassengerNominalRules.ResolveServiceSet"/>, para que el
    /// gate, el preview del front y el voucher resuelvan el set EXACTAMENTE igual (fuente unica).
    ///
    /// <para>Default seguro: si el servicio no tiene asignaciones (caso tipico, cero clics del agente), el
    /// set es TODA la reserva. La cantidad propia del servicio (Adults, etc.) NO se consulta aca a
    /// proposito (ADR §2.7/§3.2): el subconjunto se expresa SOLO asignando.</para>
    /// </summary>
    private async Task<IReadOnlyList<Passenger>> ResolveServiceSetAsync(
        int reservaId,
        string assignmentServiceType,
        int serviceId,
        CancellationToken ct)
    {
        // Pasajeros de la reserva (la regla es pura y necesita la coleccion cargada). AsNoTracking:
        // solo leemos para validar, no mutamos nada aca.
        var reservaPassengers = await _db.Passengers
            .AsNoTracking()
            .Where(p => p.ReservaId == reservaId)
            .ToListAsync(ct);

        // Ids asignados a ESTE servicio. serviceId<=0 (alta sin Id propio aun) => no hay asignaciones.
        var assignedPassengerIds = serviceId > 0
            ? await _db.PassengerServiceAssignments
                .AsNoTracking()
                .Where(a => a.ServiceType == assignmentServiceType && a.ServiceId == serviceId)
                .Select(a => a.PassengerId)
                .ToListAsync(ct)
            : new List<int>();

        return PassengerNominalRules.ResolveServiceSet(reservaPassengers, assignedPassengerIds);
    }

    /// <summary>
    /// ADR-031 v2.1 (M1, §4.3 — bloqueante de integridad): borra las <c>PassengerServiceAssignment</c> de
    /// un servicio que se esta borrando. Como <c>ServiceId</c> es soft-FK (la tabla destino varia), EF NO
    /// cascadea: sin esta limpieza quedan filas huerfanas y, como el Id se reusa, un servicio NUEVO con el
    /// mismo (ServiceType, ServiceId) heredaria el set del servicio muerto -> gate/voucher sobre los
    /// pasajeros equivocados, en silencio.
    ///
    /// <para><b>Atomicidad (I-ATOM / M-ATOM-1)</b>: NO hace SaveChanges, y la auditoria se STAGEA (no se
    /// guarda). El caller marca el borrado del servicio, la baja de asignaciones Y el alta del audit en el
    /// MISMO AppDbContext y cierra todo con su propio SaveChanges -> EF las agrupa en una sola transaccion.
    /// Nunca queda el servicio borrado con asignaciones vivas (ni al reves), y la traza de auditoria solo
    /// existe si el borrado efectivamente se commiteo. Importante: usamos <c>StageBusinessEvent</c> y NO
    /// <c>LogBusinessEventAsync</c>; este ultimo hace AddAsync + SaveChanges inmediato (Repository.cs) y
    /// flusheaba la baja de asignaciones en una transaccion separada ANTES del borrado del servicio.</para>
    ///
    /// <para><b>Auditoria</b>: registra la cascada como <c>PassengerUnassignedFromServiceByDelete</c> (si hay
    /// IAuditService inyectado), con el conteo de filas borradas. details sin numero de documento.</para>
    /// </summary>
    private async Task CleanupAssignmentsForDeletedServiceAsync(
        string assignmentServiceType, int serviceId, int reservaId, CancellationToken ct)
    {
        var orphanAssignments = await _db.PassengerServiceAssignments
            .Where(a => a.ServiceType == assignmentServiceType && a.ServiceId == serviceId)
            .ToListAsync(ct);

        if (orphanAssignments.Count == 0)
            return;

        _db.PassengerServiceAssignments.RemoveRange(orphanAssignments);

        // Auditoria de la cascada. Un solo evento por servicio con el conteo, no uno por pasajero, para no
        // inundar el log al borrar un servicio con muchos pasajeros. Sin numero de documento (misma regla
        // que el gate). Se STAGEA (no se guarda) para que entre en el mismo SaveChanges que el borrado.
        if (_auditService is not null)
        {
            var (userId, userName) = GetActor();
            var details = System.Text.Json.JsonSerializer.Serialize(new
            {
                serviceType = assignmentServiceType,
                serviceId,
                reservaId,
                removedAssignmentCount = orphanAssignments.Count
            });
            _auditService.StageBusinessEvent(
                AuditActions.PassengerUnassignedFromServiceByDelete,
                AuditActions.PassengerServiceAssignmentEntityName,
                serviceId.ToString(),
                details,
                userId ?? string.Empty,
                userName);
        }
    }

    /// <summary>
    /// Fuga 3 (ADR-017 §2.7, F1b — fix de seguridad SIN flag): decide que valores de costo
    /// persiste un UPDATE segun el permiso del caller.
    ///
    /// Contexto del bug: el PUT de bookings solo exige ReservasEdit (sin gate de costos);
    /// a un caller sin cobranzas.see_cost el GET le enmascara NetCost/Tax a 0, el form de
    /// edicion se puebla con ese 0 y el submit lo manda de vuelta -> el mapeo automatico
    /// pisaba el costo real persistido con 0 en cada edicion legitima. Por eso los maps
    /// de UPDATE (MappingProfile) ahora IGNORAN NetCost/Tax/Commission y la asignacion
    /// pasa por aca:
    ///  - Caller CON permiso (o Admin): valores del request, identico al comportamiento de siempre.
    ///  - Caller SIN permiso: se PRESERVAN NetCost/Tax persistidos. La ganancia se recalcula
    ///    canonica (Commission = SalePrice - NetCost - Tax, formula documentada en las 5
    ///    entidades) con el SalePrice del request (que el caller SI ve y puede editar) y los
    ///    costos preservados. La Commission del request se descarta: el front la calculo con
    ///    el costo enmascarado en 0, asi que no es un dato real.
    ///
    /// Fail-closed: sin HttpContext/resolver (tests sin accessor) se trata como "sin permiso",
    /// igual que CostMasking.
    /// </summary>
    private async Task<(decimal NetCost, decimal Tax, decimal Commission)> ResolveUpdateCostFieldsAsync(
        string serviceType,
        int serviceId,
        decimal persistedNetCost,
        decimal persistedTax,
        decimal requestNetCost,
        decimal requestTax,
        decimal requestCommission,
        decimal requestSalePrice,
        CancellationToken ct)
    {
        if (await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            return (requestNetCost, requestTax, requestCommission);
        }

        // Trazabilidad: dejamos registro de que fue EL SISTEMA quien decidio los costos
        // (preservando lo persistido), no el vendedor. Solo IDs — sin montos en el log.
        _logger.LogInformation(
            "ResolveUpdateCostFields: caller sin ver-costos; se preservan NetCost/Tax persistidos y se recalcula la ganancia canonica. ServiceType={ServiceType} ServiceId={ServiceId}",
            serviceType, serviceId);

        var recalculatedCommission = requestSalePrice - persistedNetCost - persistedTax;
        return (persistedNetCost, persistedTax, recalculatedCommission);
    }

    /// <summary>
    /// B1 (ADR-017 F1b): cuantas "unidades" de tarifa de hotel hay que cobrar.
    /// El tarifario de hotel guarda precios POR NOCHE y POR HABITACION (precio unitario,
    /// ver Rate.PriceUnit/HotelPriceType); el booking persiste montos TOTALES. El criterio
    /// es el mismo que usa el form (getHotelQuantity): noches (minimo 1) x habitaciones (minimo 1).
    /// </summary>
    private static int ComputeHotelRateQuantity(DateTime checkIn, DateTime checkOut, int rooms)
    {
        var nights = (checkOut.Date - checkIn.Date).Days;
        return Math.Max(nights, 1) * Math.Max(rooms, 1);
    }

    /// <summary>
    /// B1 (ADR-017 F1b — regresion del masking): el alta de hotel desde tarifario nacia con
    /// costo 0 para vendedores sin <c>cobranzas.see_cost</c>. La cadena: el search del tarifario
    /// les enmascara NetCost/Tax a 0 -> el form copia ese 0 -> el create lo persiste, porque
    /// Hotel (a diferencia de Flight/Package/Transfer/Assistance) NO re-aplica precios del
    /// Rate en su snapshot.
    ///
    /// Fix: si el caller NO puede ver costos, el server resuelve el costo real desde la tarifa
    /// (el server sabe; el caller sigue sin verlo) y recalcula la ganancia canonica
    /// (Commission = SalePrice - NetCost - Tax) con el SalePrice del request, que el caller
    /// SI ve y puede editar. Si la tarifa no tiene costo utilizable, se persiste 0 (no inventar).
    /// Callers CON permiso: no se toca nada — el request manda, como siempre.
    /// </summary>
    private async Task ApplyHotelRateCostsForMaskedCallerAsync(
        HotelBooking hotel,
        Rate rate,
        CreateHotelRequest req,
        CancellationToken ct)
    {
        if (await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct)) return;

        var quantity = ComputeHotelRateQuantity(req.CheckIn, req.CheckOut, req.Rooms);

        // Math.Round a 2 decimales (AwayFromZero) = mismo redondeo que roundMoney en el front.
        hotel.NetCost = rate.NetCost > 0m
            ? Math.Round(rate.NetCost * quantity, 2, MidpointRounding.AwayFromZero)
            : 0m;
        hotel.Tax = rate.Tax > 0m
            ? Math.Round(rate.Tax * quantity, 2, MidpointRounding.AwayFromZero)
            : 0m;

        // La Commission del request se descarta: el front la calculo con el costo enmascarado
        // en 0, asi que no es un dato real. Formula canonica documentada en HotelBooking.
        hotel.Commission = hotel.SalePrice - hotel.NetCost - hotel.Tax;

        // Trazabilidad: el costo lo resolvio el sistema desde la tarifa, no el vendedor.
        // Solo IDs — sin montos en el log.
        _logger.LogInformation(
            "CreateHotel: caller sin ver-costos; costos resueltos server-side desde el tarifario. ReservaId={ReservaId} RateId={RateId} Quantity={Quantity}",
            hotel.ReservaId, rate.Id, quantity);
    }

    private static void ValidateHotelStay(DateTime checkIn, DateTime checkOut)
    {
        if (checkOut <= checkIn)
        {
            throw new ArgumentException("El check-out debe ser posterior al check-in.");
        }
    }

    private static void ValidateAssistanceValidity(DateTime validFrom, DateTime validTo)
    {
        // La vigencia "hasta" no puede ser anterior a la vigencia "desde". Permitimos que sean
        // iguales (poliza de un solo dia), a diferencia del hotel que exige checkout > checkin.
        if (validTo < validFrom)
        {
            throw new ArgumentException("La vigencia 'hasta' no puede ser anterior a la vigencia 'desde'.");
        }
    }

    /// <summary>
    /// Integridad de datos (2026-06-25): la hora de LLEGADA de un vuelo no puede ser anterior a la de
    /// salida. La llegada es OPCIONAL (vuelos solo de ida, ver <see cref="FlightSegment.ArrivalTime"/>):
    /// si no viene, no hay nada que validar. Solo se compara cuando AMBAS estan presentes. Se permite que
    /// sean iguales (caso borde sin sentido practico, pero no es una fecha "imposible").
    /// </summary>
    private static void ValidateFlightTimes(DateTime departureTime, DateTime? arrivalTime)
    {
        if (arrivalTime.HasValue && arrivalTime.Value < departureTime)
        {
            throw new ArgumentException("La hora de llegada no puede ser anterior a la de salida.");
        }
    }

    /// <summary>
    /// Integridad de datos (2026-06-25): la fecha/hora de REGRESO de un traslado no puede ser anterior a la
    /// de salida (pickup). El regreso es OPCIONAL (solo ida y vuelta, ver
    /// <see cref="TransferBooking.ReturnDateTime"/>): si no viene, no se valida. Solo se compara con ambas
    /// presentes.
    /// </summary>
    private static void ValidateTransferTimes(DateTime pickupDateTime, DateTime? returnDateTime)
    {
        if (returnDateTime.HasValue && returnDateTime.Value < pickupDateTime)
        {
            throw new ArgumentException("La fecha de regreso no puede ser anterior a la de salida.");
        }
    }

    /// <summary>
    /// Integridad de datos (2026-06-25): la fecha de FIN de un paquete no puede ser anterior a la de inicio.
    /// El fin es OPCIONAL (la ficha "producto-primero" permite omitirlo, ver
    /// <see cref="PackageBooking.EndDate"/>): si no viene, no se valida. Solo se compara con ambas presentes.
    /// </summary>
    private static void ValidatePackageDates(DateTime startDate, DateTime? endDate)
    {
        if (endDate.HasValue && endDate.Value < startDate)
        {
            throw new ArgumentException("La fecha de fin no puede ser anterior a la de inicio.");
        }
    }

    /// <summary>
    /// Aplica el snapshot del tarifario a una asistencia: congela precios (igual que Flight/Package)
    /// y copia la moneda para trazabilidad. Si la tarifa define proveedor, lo usa.
    /// </summary>
    private static void ApplyAssistanceRateSnapshot(AssistanceBooking assistance, Rate rate)
    {
        assistance.RateId = rate.Id;
        assistance.NetCost = rate.NetCost;
        assistance.SalePrice = rate.SalePrice;
        assistance.Commission = rate.Commission;
        assistance.Tax = rate.Tax; // impuesto incluido (igual que Flight): se congela del tarifario

        // Trazabilidad: guardamos en que moneda se cotizo (copiada del tarifario).
        // No afecta saldo/pagos/factura; solo deja registro de la moneda original.
        assistance.Currency = rate.Currency;

        if (rate.SupplierId.HasValue)
        {
            assistance.SupplierId = rate.SupplierId.Value;
        }
    }

    private static void ApplyHotelRateSnapshot(HotelBooking hotel, Rate rate)
    {
        hotel.RateId = rate.Id;

        // Trazabilidad: guardamos en que moneda se cotizo (copiada del tarifario).
        // No afecta saldo/pagos/factura; solo deja registro de la moneda original.
        hotel.Currency = rate.Currency;

        if (rate.SupplierId.HasValue)
        {
            hotel.SupplierId = rate.SupplierId.Value;
        }

        hotel.HotelName = string.IsNullOrWhiteSpace(rate.HotelName)
            ? rate.ProductName
            : rate.HotelName;

        if (!string.IsNullOrWhiteSpace(rate.City))
        {
            hotel.City = rate.City;
        }

        hotel.StarRating = rate.StarRating;

        if (!string.IsNullOrWhiteSpace(rate.RoomType))
        {
            hotel.RoomType = rate.RoomType;
        }

        if (!string.IsNullOrWhiteSpace(rate.MealPlan))
        {
            hotel.MealPlan = rate.MealPlan;
        }
    }

    private async Task RecalculateReservationScheduleAsync(int reservaId, CancellationToken ct)
    {
        var reserva = await _db.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct);
        if (reserva == null) return;

        // El calculo del min/max de fechas de servicios vive en ReservaScheduleCalculator
        // para poder reusarlo desde el lifecycle automation y al construir DTOs.
        var (nextStart, nextEnd) = await ReservaScheduleCalculator.ComputeAsync(_db, reservaId, ct);

        if (reserva.StartDate != nextStart || reserva.EndDate != nextEnd)
        {
            reserva.StartDate = nextStart;
            reserva.EndDate = nextEnd;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<int> ResolveRequiredIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken ct)
        where TEntity : class, IHasPublicId
    {
        var resolved = await _db.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicIdOrLegacyId, ct);

        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado");
    }

    #region Flights

    public async Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetFlightsAsync(reservaId, ct);
    }

    public async Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(int reservaId, CancellationToken ct)
    {
        var dtos = await _flightRepo.Query()
            .Where(f => f.ReservaId == reservaId)
            .Include(f => f.Rate)
            .OrderBy(f => f.DepartureTime)
            .ProjectTo<FlightSegmentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);

        // Seguridad B1.15: este endpoint de sub-coleccion devolvia NetCost (lo que
        // le cuesta a la agencia) a CUALQUIER usuario logueado. Enmascaramos el costo
        // para quien no tiene cobranzas.see_cost (Admin/ver-costos lo siguen viendo).
        // CanSeeCost se evalua una sola vez (mismo caller para toda la lista).
        if (!await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            // CostToConfirm es MARCA de costo (ADR-017, guia UX linea 81): se oculta junto con los montos.
            foreach (var dto in dtos) { dto.NetCost = 0m; dto.Tax = 0m; dto.CostToConfirm = false; }
        }
        return dtos;
    }

    public async Task<FlightSegmentDto> GetFlightByIdAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var flightId = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);
        return await GetFlightByIdAsync(reservaId, flightId, ct);
    }

    public async Task<FlightSegmentDto> GetFlightByIdAsync(int reservaId, int id, CancellationToken ct)
    {
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        // Validamos que el vuelo pertenezca a la reserva del path: evita que alguien
        // lea un servicio de otra reserva pasando un id ajeno (defensa ademas del
        // RequireOwnership del controller).
        if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        var dto = _mapper.Map<FlightSegmentDto>(flight);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(flight.RateId, ct);
        await CostMasking.MaskFlightAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<FlightSegmentDto> CreateFlightAsync(string reservaPublicIdOrLegacyId, CreateFlightRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceAdded, "FlightSegment", null, "Aereo", ct);
        return await CreateFlightAsync(reservaId, req, ct);
    }

    /// <summary>
    /// Normaliza una hora de vuelo/traslado para guardarla SIN corrimiento de zona horaria.
    ///
    /// POR QUE existe: las horas de vuelo y traslado son "hora local del aeropuerto/lugar"
    /// (lo que figura en el ticket y en el voucher), NO un instante universal (UTC). El front
    /// hacia <c>new Date("2025-06-15T14:30").toISOString()</c>, que interpretaba el texto como
    /// hora LOCAL del navegador y lo convertia a UTC: una salida "14:30" cargada en Argentina
    /// (UTC-3) se guardaba corrida ("17:30Z") y el pasajero veia mal el itinerario. Hotel no
    /// sufre esto porque usa fechas sin hora (date-only).
    ///
    /// QUE hace: toma los componentes de fecha/hora tal cual (14:30 sigue siendo 14:30) y los
    /// marca como Kind=Utc SIN convertir. Esto es necesario porque las columnas
    /// DepartureTime/ArrivalTime/PickupDateTime/ReturnDateTime son
    /// <c>timestamp with time zone</c> en Postgres y Npgsql (sin EnableLegacyTimestampBehavior)
    /// EXIGE Kind=Utc al escribir; con Local/Unspecified tira InvalidOperationException -> 500.
    ///
    /// Resultado: lo que el usuario carga (14:30) es lo que se guarda y lo que el VoucherService
    /// imprime (hace <c>ToString("dd/MM/yyyy HH:mm")</c> sin conversion). Consistente con el resto
    /// del sistema, que tambien usa <c>SpecifyKind(..., Utc)</c> para campos sin instante real
    /// (ver ReservaScheduleCalculator, DestinationService, CatalogPackageService).
    ///
    /// CONTRATO con el front: debe mandar la hora local SIN sufijo "Z" ni offset
    /// (ej. <c>"2025-06-15T14:30:00"</c>). Asi llega como Kind=Unspecified y se guarda tal cual.
    /// Si un cliente viejo todavia manda con "Z" (Kind=Utc), el valor YA viene corrido por el
    /// navegador y no se puede des-corregir aca de forma confiable: lo dejamos verbatim hasta que
    /// ese cliente se actualice (no introducimos una segunda conversion encima).
    /// </summary>
    private static DateTime NormalizeAirportWallClock(DateTime wallClock)
    {
        // Tomamos los componentes de pared tal cual (Year/Month/Day/Hour/Minute...) y solo
        // cambiamos el Kind a Utc. NO usamos ToUniversalTime(): eso volveria a correr la hora.
        return DateTime.SpecifyKind(wallClock, DateTimeKind.Utc);
    }

    /// <summary>
    /// BUG 2 (2026-06-08): variante nullable de <see cref="NormalizeAirportWallClock(DateTime)"/> para
    /// la hora de llegada de un vuelo solo de ida (null = sin hora de llegada). null se preserva tal cual.
    /// </summary>
    private static DateTime? NormalizeAirportWallClock(DateTime? wallClock)
        => wallClock.HasValue ? NormalizeAirportWallClock(wallClock.Value) : (DateTime?)null;

    /// <summary>
    /// Bug en vivo 2026-06-06 (ficha inline): normaliza una fecha-calendario del request (date-only:
    /// check-in/check-out de hotel, inicio/fin de paquete, vigencia de asistencia) a medianoche con
    /// <see cref="DateTimeKind.Utc"/>, SIN convertir el instante — el mismo contrato "fecha de pared
    /// disfrazada de Utc" de <see cref="NormalizeAirportWallClock"/>.
    ///
    /// POR QUE existe: la ficha inline manda el value crudo del input date ("2026-08-12") y el binder
    /// JSON lo deserializa con Kind=Unspecified. Npgsql (sin EnableLegacyTimestampBehavior) EXIGE
    /// Kind=Utc al escribir columnas 'timestamp with time zone' y con Unspecified tira
    /// DbUpdateException -> 500 en el INSERT. El modal viejo mandaba "...T00:00:00.000Z" (Kind=Utc)
    /// y por eso nunca explotaba. Los DOS contratos quedan validos: sobre una medianoche ya-Utc,
    /// <c>.Date</c> + SpecifyKind no cambia nada (idempotente); sobre Unspecified/Local preserva la
    /// fecha calendario que eligio el vendedor y solo arregla el Kind.
    /// </summary>
    private static DateTime NormalizeCalendarDate(DateTime date)
        => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);

    /// <summary>Variante nullable de <see cref="NormalizeCalendarDate(DateTime)"/>: null = sin fecha.</summary>
    private static DateTime? NormalizeCalendarDate(DateTime? date)
        => date.HasValue ? NormalizeCalendarDate(date.Value) : (DateTime?)null;

    /// <summary>
    /// ADR-018 Ronda 7 (2026-06-06): normaliza un texto OPCIONAL del request — vacio o solo espacios
    /// se persiste como null ("Sin especificar"), nunca como "". Reemplaza al viejo coalesce a default
    /// de negocio ("Economy"/"Sedan" de ADR-018 §2): Gaston decidio que el sistema deja de exigir
    /// Cabina y Tipo de vehiculo, asi que el server ya NO inventa un valor que el vendedor no eligio.
    /// </summary>
    internal static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// ADR-017 (pill violeta "creado en esta venta"): resuelve si el producto del tarifario vinculado al
    /// servicio nacio inline durante una venta (<see cref="Rate.CreatedInSale"/>).
    ///
    /// POR QUE existe: los paths de entidad suelta (byId/create/update/status) cargan el booking con
    /// <c>FindAsync</c>, que NO trae la nav Rate -> el MapFrom del MappingProfile (que mira <c>src.Rate</c>)
    /// daria false aunque el valor real sea true. Esta query puntual por PK es la fuente confiable.
    /// Los listados NO la necesitan: usan ProjectTo, que joinea la nav en SQL.
    /// NO es dato de costo: el resultado lo ven todos (no se enmascara).
    /// </summary>
    private async Task<bool> ResolveProductCreatedInSaleAsync(int? rateId, CancellationToken ct)
    {
        if (!rateId.HasValue) return false;
        return await _db.Set<Rate>().AsNoTracking()
            .AnyAsync(r => r.Id == rateId.Value && r.CreatedInSale, ct);
    }

    public async Task<FlightSegmentDto> CreateFlightAsync(int reservaId, CreateFlightRequest req, CancellationToken ct)
    {
        // ADR-017 F1.3: con el catalogo find-or-create prendido, el alta corre por el path nuevo
        // (transaccion atomica + find-or-create + request-manda + cadena de costo D7 + upsert de
        // RateSupplierSale). Con el flag APAGADO, sigue EXACTAMENTE el codigo de abajo (byte-identico).
        if (await IsCatalogFindOrCreateEnabledAsync(ct))
            return await CreateFlightWithCatalogAsync(reservaId, req, ct);

        ValidateFlightTimes(req.DepartureTime, req.ArrivalTime);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        var flight = _mapper.Map<FlightSegment>(req);
        flight.ReservaId = reservaId;
        flight.SupplierId = supplierId;

        // ADR-018 Ronda 7 (2026-06-06): la cabina es OPCIONAL. Vacio/null = "Sin especificar" y se
        // persiste null (antes se coalesceaba a "Economy"; ese default de negocio quedo derogado).
        flight.CabinClass = NormalizeOptionalText(flight.CabinClass);

        // B1 (zona horaria): la hora de vuelo es "hora local del aeropuerto" (la que figura
        // en el ticket), NO un instante UTC. La guardamos tal cual la cargo el usuario para
        // que el voucher la muestre sin corrimiento. Ver NormalizeAirportWallClock.
        flight.DepartureTime = NormalizeAirportWallClock(flight.DepartureTime);
        flight.ArrivalTime = NormalizeAirportWallClock(flight.ArrivalTime);

        // Auditoria ERP item 5: los deadlines mapean por convencion en el ALTA; solo falta normalizar a
        // fecha de pared (Npgsql exige Kind=Utc en timestamptz). Ver NormalizeCalendarDate.
        flight.TicketingDeadline = NormalizeCalendarDate(flight.TicketingDeadline);
        flight.OperatorPaymentDeadline = NormalizeCalendarDate(flight.OperatorPaymentDeadline);

        // Snapshot desde tarifario: si viene RateId, congelamos precios del tarifario
        var rate = await GetRateAsync(req.RateId, ct);
        if (rate != null)
        {
            flight.RateId = rate.Id;
            flight.NetCost = rate.NetCost;
            flight.SalePrice = rate.SalePrice;
            flight.Commission = rate.Commission;
            flight.Tax = rate.Tax;
            // Trazabilidad: guardamos en que moneda se cotizo (copiada del tarifario).
            // No afecta saldo/pagos/factura; solo deja registro de la moneda original.
            flight.Currency = rate.Currency;
        }

        // En Presupuesto el status siempre es "Solicitado".
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            flight.Status = "Solicitado";

        // Guard: si la reserva esta en Operativo/Closed, el servicio nuevo debe estar confirmado
        // ADR-018: la identidad visible se deriva de ServiceDisplayName (ProductName si la ficha
        // "producto-primero" no cargo aerolinea/numero), para no mostrar "Vuelo " vacio en el mensaje.
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Vuelo {ServiceDisplayName.ForFlight(flight.ProductName, flight.AirlineCode, flight.FlightNumber)}", flight.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        // ADR-031 (bypass B1): el aereo RESUELVE por TicketIssuedAt (que el alta no setea), asi que este
        // gate es defensivo hoy. Se cablea igual para que ninguna ruta de alta que en el futuro deje el
        // vuelo resuelto se cuele sin nombre + documento. La emision real tiene su gate efectivo.
        // El alta nace sin resolver previo -> wasResolved=false.
        // serviceId: 0 -> el alta no tiene Id propio aun, no puede tener asignaciones; set = toda la reserva.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, serviceWasResolved: false, ServiceResolutionRules.IsResolved(flight),
            PassengerNominalRules.ServiceKind.Flight, AssignmentServiceType.Flight, serviceId: 0, ct);

        await _flightRepo.AddAsync(flight, ct);

        if (flight.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);

        var dto = _mapper.Map<FlightSegmentDto>(flight);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(flight.RateId, ct);
        // B1.15: el response de POST exponia el costo del proveedor a usuarios sin
        // permiso. Enmascaramos NetCost igual que Hotel.
        await CostMasking.MaskFlightAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<FlightSegmentDto> UpdateFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateFlightRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var flightId = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceEdited, "FlightSegment", flightId, "Aereo", ct);
        return await UpdateFlightAsync(reservaId, flightId, req, ct);
    }

    public async Task<FlightSegmentDto> UpdateFlightAsync(int reservaId, int id, UpdateFlightRequest req, CancellationToken ct)
    {
        ValidateFlightTimes(req.DepartureTime, req.ArrivalTime);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        // B1.15 Fase 0' (CODE-04): inmutabilidad post-CAE / post-voucher.
        var blockReason = await MutationGuards.GetBookingMutationBlockReasonAsync(_db, reservaId, "Flight", ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateFlightAsync rejected. FlightId={FlightId} ReservaId={ReservaId}. Reason={Reason}",
                id, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        var oldSupplierId = flight.SupplierId;
        var oldStatus = flight.Status;
        // Plata viva (familia R1): moneda ANTES del map, para la guarda de reasignacion de operador/moneda de abajo.
        var oldCurrency = flight.Currency;
        // ADR-031: estado de resolucion ANTES de la edicion (para gatear solo la transicion a resuelto).
        var flightWasResolved = ServiceResolutionRules.IsResolved(flight);
        // ADR-027 (hallazgo #10): precio/costo ANTES del map para detectar "el operador confirmo con cambios".
        var oldSalePrice = flight.SalePrice;
        var oldNetCost = flight.NetCost;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, flight);
        flight.SupplierId = supplierId;

        // ADR-018 (anti-clobber): el map IGNORA ProductName. La ficha inline reenvia el texto que vio el
        // vendedor (round-trip) y ahi lo actualizamos; el modal viejo NO lo manda (null/vacio) y entonces
        // PRESERVAMOS el valor persistido, para que la identidad del servicio no revierta a "Vuelo "/ruta.
        if (!string.IsNullOrWhiteSpace(req.ProductName))
            flight.ProductName = req.ProductName.Trim();

        // Auditoria ERP item 5 (anti-clobber): el map IGNORA los deadlines. La ficha de aereo los
        // reenvia (round-trip) y ahi los actualizamos; un modal viejo que NO los manda llega null y
        // PRESERVAMOS el valor persistido (no borramos una fecha que cargo el operador). Se normalizan a
        // fecha de pared (Kind=Utc) igual que en el alta. Ver NormalizeCalendarDate.
        if (req.TicketingDeadline.HasValue)
            flight.TicketingDeadline = NormalizeCalendarDate(req.TicketingDeadline.Value);
        if (req.OperatorPaymentDeadline.HasValue)
            flight.OperatorPaymentDeadline = NormalizeCalendarDate(req.OperatorPaymentDeadline.Value);

        // ADR-018 Ronda 7: cabina opcional. OJO, aca NO hay anti-clobber a proposito: la ficha reenvia
        // la cabina en cada edicion (round-trip), asi que null/vacio significa "el vendedor la dejo en
        // Sin especificar" y debe persistirse null (es un borrado legitimo, no un campo no enviado).
        flight.CabinClass = NormalizeOptionalText(flight.CabinClass);

        // Fuga 3 (F1b): el map ignora NetCost/Tax/Commission; se aplican segun permiso del caller.
        (flight.NetCost, flight.Tax, flight.Commission) = await ResolveUpdateCostFieldsAsync(
            serviceType: "Flight", serviceId: id,
            persistedNetCost: flight.NetCost, persistedTax: flight.Tax,
            requestNetCost: req.NetCost, requestTax: req.Tax,
            requestCommission: req.Commission, requestSalePrice: req.SalePrice, ct: ct);

        // B1 (zona horaria): misma normalizacion que en el alta. La hora de vuelo se guarda
        // como hora local del aeropuerto, sin convertir a UTC. Ver NormalizeAirportWallClock.
        flight.DepartureTime = NormalizeAirportWallClock(flight.DepartureTime);
        flight.ArrivalTime = NormalizeAirportWallClock(flight.ArrivalTime);

        // Si viene un RateId nuevo, solo se re-vincula la tarifa (RateId). OJO: NO se
        // re-aplican precios del tarifario en el update — los costos ya quedaron resueltos
        // arriba segun el permiso del caller (ResolveUpdateCostFieldsAsync).
        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            flight.RateId = rateId.Value;

        // En Presupuesto el status siempre es "Solicitado".
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            flight.Status = "Solicitado";

        // ADR-018: identidad visible via ServiceDisplayName (ProductName si no hay aerolinea/numero).
        var label = $"Vuelo {ServiceDisplayName.ForFlight(flight.ProductName, flight.AirlineCode, flight.FlightNumber)}";
        // Guard 1: en reserva Operativo/Closed el servicio debe quedar confirmado
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, flight.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        // Guard 2: no degradar de confirmado a no-confirmado si hay pagos al proveedor
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, flight.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        // ADR-031 (bypass B1): el aereo RESUELVE por TicketIssuedAt (que la edicion no setea), gate
        // defensivo hoy. Cableado para que ninguna ruta de edicion futura deje el vuelo resuelto sin nombres.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, flightWasResolved, ServiceResolutionRules.IsResolved(flight),
            PassengerNominalRules.ServiceKind.Flight, AssignmentServiceType.Flight, flight.Id, ct);

        // Plata viva (familia R1): impedir reasignar operador/moneda de un aereo confirmado (HK/TK/KK/KL) y pagado al
        // operador saliente sin factura que ancle el receivable. Ver EnsureServiceOperatorOrCurrencyChangeHas...
        await GuardOperatorOrCurrencyReassignmentAsync(
            reservaId, CancellableServiceTable.Flight, flight.Id, oldSupplierId, flight.SupplierId,
            oldCurrency, flight.Currency, debtServiceType: "Vuelo", oldStatus: oldStatus, ct);

        await _flightRepo.UpdateAsync(flight, ct);
        if (oldSupplierId > 0 && oldSupplierId == flight.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }
        else if (oldSupplierId != flight.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (flight.SupplierId > 0) await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        // ADR-027 (detalle): marca "confirmada con cambios" + registra el detalle (que servicio, antes/despues)
        // si cambio precio/costo y la reserva esta viva.
        var flightChange = new PendingServiceChange
        {
            ServiceType = "Aereo",
            ServiceDescription = BuildFlightChangeDescription(flight),
            ServicePublicId = flight.PublicId,
            Currency = flight.Currency,
            OldSalePrice = oldSalePrice,
            NewSalePrice = flight.SalePrice,
            OldNetCost = oldNetCost,
            NewNetCost = flight.NetCost,
        };
        await _reservaService.UpdateBalanceAsync(reservaId, flightChange);

        var dto = _mapper.Map<FlightSegmentDto>(flight);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(flight.RateId, ct);
        // B1.15: el response de PUT exponia el costo del proveedor a usuarios sin
        // permiso. Enmascaramos NetCost igual que Hotel.
        await CostMasking.MaskFlightAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    /// <summary>
    /// ADR-027 (detalle): arma una descripcion legible del vuelo para la franja "confirmada con cambios"
    /// (ej. "Aereo AR EZE-MIA"). Cae a "Aereo" pelado si no hay datos. Solo para mostrar, no es identidad.
    /// </summary>
    private static string BuildFlightChangeDescription(FlightSegment flight)
    {
        var route = string.Join("-", new[] { flight.Origin, flight.Destination }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var airline = flight.AirlineCode ?? flight.AirlineName;
        var parts = new[] { "Aereo", airline, route }.Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(" ", parts);
    }

    public async Task DeleteFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var flightId = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceDeleted, "FlightSegment", flightId, "Aereo", ct);
        await DeleteFlightAsync(reservaId, flightId, ct);
    }

    public async Task DeleteFlightAsync(int reservaId, int id, CancellationToken ct)
    {
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        var flightConfirmed = flight.ConfirmedAt != null
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsOperatorConfirmed(flight)
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsResolved(flight);
        await EnsureCanRemoveServiceAsync(reservaId, flightConfirmed, ct);

        // ADR-031 v2.1 (M1): limpiar las asignaciones de este servicio en la MISMA transaccion que el
        // borrado. RemoveRange aca + el SaveChanges de DeleteAsync (mismo _db) las agrupan juntas.
        await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Flight, flight.Id, reservaId, ct);
        await _flightRepo.DeleteAsync(flight, ct);
        if (flight.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    #endregion

    #region Hotels

    public async Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetHotelsAsync(reservaId, ct);
    }

    public async Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(int reservaId, CancellationToken ct)
    {
        var dtos = await _hotelRepo.Query()
            .Where(h => h.ReservaId == reservaId)
            .Include(h => h.Rate)
            .OrderBy(h => h.CheckIn)
            .ProjectTo<HotelBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);

        // Seguridad B1.15: el GET de sub-coleccion devolvia NetCost a cualquier
        // usuario logueado. Enmascaramos para quien no tiene cobranzas.see_cost.
        if (!await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            // CostToConfirm es MARCA de costo (ADR-017, guia UX linea 81): se oculta junto con los montos.
            foreach (var dto in dtos) { dto.NetCost = 0m; dto.Tax = 0m; dto.CostToConfirm = false; }
        }
        return dtos;
    }

    public async Task<HotelBookingDto> GetHotelByIdAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var hotelId = await ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct);
        return await GetHotelByIdAsync(reservaId, hotelId, ct);
    }

    public async Task<HotelBookingDto> GetHotelByIdAsync(int reservaId, int id, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

        var dto = _mapper.Map<HotelBookingDto>(hotel);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(hotel.RateId, ct);
        // B1.15: el byId tambien exponia NetCost sin enmascarar. Lo alineamos con
        // Create/Update que ya enmascaraban.
        await CostMasking.MaskHotelAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<HotelBookingDto> CreateHotelAsync(string reservaPublicIdOrLegacyId, CreateHotelRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceAdded, "HotelBooking", null, "Hotel", ct);
        return await CreateHotelAsync(reservaId, req, ct);
    }

    public async Task<HotelBookingDto> CreateHotelAsync(int reservaId, CreateHotelRequest req, CancellationToken ct)
    {
        // ADR-017 F1.3: ver nota en CreateFlightAsync. Flag OFF = byte-identico al codigo de abajo.
        if (await IsCatalogFindOrCreateEnabledAsync(ct))
            return await CreateHotelWithCatalogAsync(reservaId, req, ct);

        ValidateHotelStay(req.CheckIn, req.CheckOut);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var rate = await GetRateAsync(req.RateId, ct);
        var supplierId = rate?.SupplierId ?? await ResolveSupplierIdAsync(req.SupplierId, ct);

        var hotel = _mapper.Map<HotelBooking>(req);
        hotel.ReservaId = reservaId;
        hotel.SupplierId = supplierId;

        // Bug 2026-06-06: la ficha inline manda CheckIn/CheckOut como fecha pelada ("2026-08-12") y el
        // binder los deja con Kind=Unspecified -> Npgsql los rechaza en timestamptz. Normalizamos a
        // fecha de pared (medianoche Kind=Utc). Ver NormalizeCalendarDate.
        hotel.CheckIn = NormalizeCalendarDate(hotel.CheckIn);
        hotel.CheckOut = NormalizeCalendarDate(hotel.CheckOut);

        // Auditoria ERP item 5: el deadline mapea por convencion en el ALTA; normalizamos a fecha de
        // pared (Kind=Utc). Ver NormalizeCalendarDate.
        hotel.OperatorPaymentDeadline = NormalizeCalendarDate(hotel.OperatorPaymentDeadline);

        if (rate != null)
        {
            ApplyHotelRateSnapshot(hotel, rate);

            // B1 (F1b): si el caller no puede ver costos, el NetCost/Tax del request son el 0
            // enmascarado rebotado por el form — el costo real lo resuelve el server desde la
            // tarifa. Con permiso, no hace nada (el request manda, como siempre).
            await ApplyHotelRateCostsForMaskedCallerAsync(hotel, rate, req, ct);
        }

        // En Presupuesto el status siempre es "Solicitado" — no es una reserva real.
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            hotel.Status = "Solicitado";

        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Hotel {hotel.HotelName ?? "sin nombre"}", hotel.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        // ADR-031 (bypass B1): el alta puede dejar el hotel ya "Confirmado" (request con status resuelto
        // y reserva fuera de Presupuesto) -> exigir el titular ANTES de persistir, para que el motor
        // automatico no auto-confirme la reserva sin nombres.
        // serviceId: 0 -> alta sin Id propio aun; set = toda la reserva.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, serviceWasResolved: false, ServiceResolutionRules.IsResolved(hotel),
            PassengerNominalRules.ServiceKind.Hotel, AssignmentServiceType.Hotel, serviceId: 0, ct);

        await _hotelRepo.AddAsync(hotel, ct);

        if (hotel.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);

        var dto = _mapper.Map<HotelBookingDto>(hotel);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(hotel.RateId, ct);
        // B1.15 Fase 0.2: enmascarar NetCost si el caller no tiene cobranzas.see_cost.
        // Antes el response de POST exponia el costo del proveedor a usuarios sin permiso.
        await CostMasking.MaskHotelAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<HotelBookingDto> UpdateHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateHotelRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var hotelId = await ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceEdited, "HotelBooking", hotelId, "Hotel", ct);
        return await UpdateHotelAsync(reservaId, hotelId, req, ct);
    }

    public async Task<HotelBookingDto> UpdateHotelAsync(int reservaId, int id, UpdateHotelRequest req, CancellationToken ct)
    {
        ValidateHotelStay(req.CheckIn, req.CheckOut);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

        // B1.15 Fase 0' (CODE-04): inmutabilidad post-CAE / post-voucher.
        var blockReason = await MutationGuards.GetBookingMutationBlockReasonAsync(_db, reservaId, "Hotel", ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateHotelAsync rejected. HotelId={HotelId} ReservaId={ReservaId}. Reason={Reason}",
                id, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        var oldSupplierId = hotel.SupplierId;
        var oldStatus = hotel.Status;
        // Plata viva (familia R1): moneda ANTES del map, para detectar reasignacion de operador/moneda. Ver
        // GuardOperatorOrCurrencyReassignmentAsync mas abajo.
        var oldCurrency = hotel.Currency;
        // ADR-031: estado de resolucion ANTES de la edicion (para gatear solo la transicion a resuelto).
        var hotelWasResolved = ServiceResolutionRules.IsResolved(hotel);
        // ADR-027 (hallazgo #10): precio/costo ANTES del map para detectar "el operador confirmo con cambios".
        var oldSalePrice = hotel.SalePrice;
        var oldNetCost = hotel.NetCost;
        var oldRateId = hotel.RateId;
        var oldHotelName = hotel.HotelName;
        var oldCity = hotel.City;
        var oldCountry = hotel.Country;
        var oldStarRating = hotel.StarRating;
        var oldRoomType = hotel.RoomType;
        var oldMealPlan = hotel.MealPlan;
        var requestedRateId = await ResolveRateIdAsync(req.RateId, ct);
        var isRateChanged = requestedRateId.HasValue && requestedRateId != oldRateId;
        var requestedRate = isRateChanged
            ? await _db.Set<Rate>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestedRateId.Value, ct)
            : null;
        var supplierId = requestedRate?.SupplierId
            ?? (!string.IsNullOrWhiteSpace(req.SupplierId)
                ? await ResolveSupplierIdAsync(req.SupplierId, ct)
                : hotel.SupplierId);

        _mapper.Map(req, hotel);
        hotel.SupplierId = supplierId;

        // Bug 2026-06-06: misma normalizacion que en el alta — la ficha inline manda fechas peladas
        // (Kind=Unspecified) y Npgsql las rechaza en timestamptz. Ver NormalizeCalendarDate.
        hotel.CheckIn = NormalizeCalendarDate(hotel.CheckIn);
        hotel.CheckOut = NormalizeCalendarDate(hotel.CheckOut);

        // Auditoria ERP item 5 (anti-clobber): el map IGNORA el deadline; lo asignamos solo si viene con
        // valor, asi un caller que no lo manda (null) no borra la fecha cargada. Ver NormalizeCalendarDate.
        if (req.OperatorPaymentDeadline.HasValue)
            hotel.OperatorPaymentDeadline = NormalizeCalendarDate(req.OperatorPaymentDeadline.Value);

        // Fuga 3 (F1b): el map ignora NetCost/Tax/Commission; se aplican segun permiso del caller.
        // (ApplyHotelRateSnapshot, mas abajo, no toca precios en Hotel: solo atributos.)
        (hotel.NetCost, hotel.Tax, hotel.Commission) = await ResolveUpdateCostFieldsAsync(
            serviceType: "Hotel", serviceId: id,
            persistedNetCost: hotel.NetCost, persistedTax: hotel.Tax,
            requestNetCost: req.NetCost, requestTax: req.Tax,
            requestCommission: req.Commission, requestSalePrice: req.SalePrice, ct: ct);

        if (requestedRate != null)
        {
            ApplyHotelRateSnapshot(hotel, requestedRate);
        }
        else if (oldRateId.HasValue)
        {
            hotel.RateId = oldRateId;
            hotel.SupplierId = oldSupplierId;
            hotel.HotelName = oldHotelName;
            hotel.City = oldCity;
            hotel.Country = oldCountry;
            hotel.StarRating = oldStarRating;
            hotel.RoomType = oldRoomType;
            hotel.MealPlan = oldMealPlan;
        }

        // En Presupuesto el status siempre es "Solicitado".
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            hotel.Status = "Solicitado";

        var label = $"Hotel {hotel.HotelName ?? "sin nombre"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, hotel.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, hotel.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        // ADR-031 (bypass B1): la edicion puede dejar el hotel resuelto (transicion no-resuelto ->
        // resuelto) -> exigir el titular ANTES de persistir.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, hotelWasResolved, ServiceResolutionRules.IsResolved(hotel),
            PassengerNominalRules.ServiceKind.Hotel, AssignmentServiceType.Hotel, hotel.Id, ct);

        // Plata viva (familia R1): impedir reasignar operador/moneda de un servicio confirmado y pagado al operador
        // saliente sin factura que ancle el receivable. Corre ANTES de persistir; el candado post-CAE (MutationGuards,
        // arriba) ya cubrio la factura viva. hotel.SupplierId ya es el FINAL (tras el eventual revert por tarifa).
        await GuardOperatorOrCurrencyReassignmentAsync(
            reservaId, CancellableServiceTable.Hotel, hotel.Id, oldSupplierId, hotel.SupplierId,
            oldCurrency, hotel.Currency, debtServiceType: "Hotel", oldStatus: oldStatus, ct);

        await _hotelRepo.UpdateAsync(hotel, ct);
        if (oldSupplierId > 0 && oldSupplierId == hotel.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }
        else if (oldSupplierId != hotel.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (hotel.SupplierId > 0) await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        // ADR-027 (detalle): marca "confirmada con cambios" + registra el detalle si cambio precio/costo.
        var hotelChange = new PendingServiceChange
        {
            ServiceType = "Hotel",
            ServiceDescription = string.IsNullOrWhiteSpace(hotel.HotelName) ? "Hotel" : $"Hotel {hotel.HotelName}",
            ServicePublicId = hotel.PublicId,
            Currency = hotel.Currency,
            OldSalePrice = oldSalePrice,
            NewSalePrice = hotel.SalePrice,
            OldNetCost = oldNetCost,
            NewNetCost = hotel.NetCost,
        };
        await _reservaService.UpdateBalanceAsync(reservaId, hotelChange);

        var dto = _mapper.Map<HotelBookingDto>(hotel);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(hotel.RateId, ct);
        // B1.15 Fase 0.2: enmascarar NetCost si el caller no tiene cobranzas.see_cost.
        // Antes el response de PUT exponia el costo del proveedor a usuarios sin permiso.
        await CostMasking.MaskHotelAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task DeleteHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var hotelId = await ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceDeleted, "HotelBooking", hotelId, "Hotel", ct);
        await DeleteHotelAsync(reservaId, hotelId, ct);
    }

    public async Task DeleteHotelAsync(int reservaId, int id, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

        var hotelConfirmed = hotel.ConfirmedAt != null
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsOperatorConfirmed(hotel)
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsResolved(hotel);
        await EnsureCanRemoveServiceAsync(reservaId, hotelConfirmed, ct);

        // ADR-031 v2.1 (M1): limpiar asignaciones en la misma transaccion que el borrado.
        await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Hotel, hotel.Id, reservaId, ct);
        await _hotelRepo.DeleteAsync(hotel, ct);
        if (hotel.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    #endregion

    #region Packages

    public async Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetPackagesAsync(reservaId, ct);
    }

    public async Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(int reservaId, CancellationToken ct)
    {
        var dtos = await _packageRepo.Query()
            .Where(p => p.ReservaId == reservaId)
            .Include(p => p.Rate)
            .OrderBy(p => p.CreatedAt)
            .ProjectTo<PackageBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);

        // Seguridad B1.15: enmascaramos NetCost para quien no tiene cobranzas.see_cost.
        if (!await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            // CostToConfirm es MARCA de costo (ADR-017, guia UX linea 81): se oculta junto con los montos.
            foreach (var dto in dtos) { dto.NetCost = 0m; dto.Tax = 0m; dto.CostToConfirm = false; }
        }
        return dtos;
    }

    public async Task<PackageBookingDto> GetPackageByIdAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var packageId = await ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct);
        return await GetPackageByIdAsync(reservaId, packageId, ct);
    }

    public async Task<PackageBookingDto> GetPackageByIdAsync(int reservaId, int id, CancellationToken ct)
    {
        var package = await _packageRepo.GetByIdAsync(id, ct);
        // El paquete debe pertenecer a la reserva del path (defensa contra ids ajenos).
        if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

        var dto = _mapper.Map<PackageBookingDto>(package);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(package.RateId, ct);
        await CostMasking.MaskPackageAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<PackageBookingDto> CreatePackageAsync(string reservaPublicIdOrLegacyId, CreatePackageRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceAdded, "PackageBooking", null, "Paquete", ct);
        return await CreatePackageAsync(reservaId, req, ct);
    }

    public async Task<PackageBookingDto> CreatePackageAsync(int reservaId, CreatePackageRequest req, CancellationToken ct)
    {
        // ADR-017 F1.3: ver nota en CreateFlightAsync. Flag OFF = byte-identico al codigo de abajo.
        if (await IsCatalogFindOrCreateEnabledAsync(ct))
            return await CreatePackageWithCatalogAsync(reservaId, req, ct);

        ValidatePackageDates(req.StartDate, req.EndDate);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        var package = _mapper.Map<PackageBooking>(req);
        package.ReservaId = reservaId;
        package.SupplierId = supplierId;

        // Bug 2026-06-06: la ficha inline manda StartDate/EndDate como fecha pelada (Kind=Unspecified)
        // y Npgsql las rechaza en timestamptz. Normalizamos a fecha de pared. Ver NormalizeCalendarDate.
        package.StartDate = NormalizeCalendarDate(package.StartDate);
        package.EndDate = NormalizeCalendarDate(package.EndDate);

        // Auditoria ERP item 5: deadline mapeado por convencion en el ALTA; normalizado a fecha de pared.
        package.OperatorPaymentDeadline = NormalizeCalendarDate(package.OperatorPaymentDeadline);

        // Snapshot desde tarifario
        var rate = await GetRateAsync(req.RateId, ct);
        if (rate != null)
        {
            package.RateId = rate.Id;
            package.NetCost = rate.NetCost;
            package.SalePrice = rate.SalePrice;
            package.Commission = rate.Commission;
            package.Tax = rate.Tax; // impuesto incluido (igual que Flight): se congela del tarifario
            // Trazabilidad: guardamos en que moneda se cotizo (copiada del tarifario).
            // No afecta saldo/pagos/factura; solo deja registro de la moneda original.
            package.Currency = rate.Currency;
        }

        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            package.Status = "Solicitado";

        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Paquete {package.PackageName ?? "sin nombre"}", package.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        // ADR-031 (bypass B1): el alta puede dejar el paquete resuelto -> exigir el nombre de TODOS
        // los declarados ANTES de persistir.
        // serviceId: 0 -> alta sin Id propio aun; set = toda la reserva.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, serviceWasResolved: false, ServiceResolutionRules.IsResolved(package),
            PassengerNominalRules.ServiceKind.Package, AssignmentServiceType.Package, serviceId: 0, ct);

        await _packageRepo.AddAsync(package, ct);

        if (package.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);

        var dto = _mapper.Map<PackageBookingDto>(package);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(package.RateId, ct);
        // B1.15: enmascarar NetCost en el response de POST (igual que Hotel).
        await CostMasking.MaskPackageAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<PackageBookingDto> UpdatePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdatePackageRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var packageId = await ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceEdited, "PackageBooking", packageId, "Paquete", ct);
        return await UpdatePackageAsync(reservaId, packageId, req, ct);
    }

    public async Task<PackageBookingDto> UpdatePackageAsync(int reservaId, int id, UpdatePackageRequest req, CancellationToken ct)
    {
        ValidatePackageDates(req.StartDate, req.EndDate);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

        // B1.15 Fase 0' (CODE-04): inmutabilidad post-CAE / post-voucher.
        var blockReason = await MutationGuards.GetBookingMutationBlockReasonAsync(_db, reservaId, "Package", ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdatePackageAsync rejected. PackageId={PackageId} ReservaId={ReservaId}. Reason={Reason}",
                id, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        var oldNetCost = package.NetCost;
        // ADR-027 (hallazgo #10): precio ANTES del map para detectar "el operador confirmo con cambios".
        var oldSalePrice = package.SalePrice;
        var oldSupplierId = package.SupplierId;
        var oldStatus = package.Status;
        // Plata viva (familia R1): moneda ANTES del map, para la guarda de reasignacion de operador/moneda de abajo.
        var oldCurrency = package.Currency;
        // ADR-031: estado de resolucion ANTES de la edicion (para gatear solo la transicion a resuelto).
        var packageWasResolved = ServiceResolutionRules.IsResolved(package);
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, package);
        package.SupplierId = supplierId;

        // Bug 2026-06-06: misma normalizacion que en el alta — fechas peladas (Kind=Unspecified) de la
        // ficha inline son rechazadas por Npgsql en timestamptz. Ver NormalizeCalendarDate.
        package.StartDate = NormalizeCalendarDate(package.StartDate);
        package.EndDate = NormalizeCalendarDate(package.EndDate);

        // Auditoria ERP item 5 (anti-clobber): el map IGNORA el deadline; asignamos solo si viene con valor.
        if (req.OperatorPaymentDeadline.HasValue)
            package.OperatorPaymentDeadline = NormalizeCalendarDate(req.OperatorPaymentDeadline.Value);

        // Fuga 3 (F1b): el map ignora NetCost/Tax/Commission; se aplican segun permiso del caller.
        (package.NetCost, package.Tax, package.Commission) = await ResolveUpdateCostFieldsAsync(
            serviceType: "Package", serviceId: id,
            persistedNetCost: package.NetCost, persistedTax: package.Tax,
            requestNetCost: req.NetCost, requestTax: req.Tax,
            requestCommission: req.Commission, requestSalePrice: req.SalePrice, ct: ct);

        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            package.RateId = rateId.Value;

        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            package.Status = "Solicitado";

        var label = $"Paquete {package.PackageName ?? "sin nombre"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, package.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, package.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        // ADR-031 (bypass B1): la edicion puede dejar el paquete resuelto -> exigir el nombre de TODOS
        // los declarados ANTES de persistir.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, packageWasResolved, ServiceResolutionRules.IsResolved(package),
            PassengerNominalRules.ServiceKind.Package, AssignmentServiceType.Package, package.Id, ct);

        // Plata viva (familia R1): impedir reasignar operador/moneda de un paquete confirmado y pagado al operador
        // saliente sin factura que ancle el receivable. Ver EnsureServiceOperatorOrCurrencyChangeHas...
        await GuardOperatorOrCurrencyReassignmentAsync(
            reservaId, CancellableServiceTable.Package, package.Id, oldSupplierId, package.SupplierId,
            oldCurrency, package.Currency, debtServiceType: "Paquete", oldStatus: oldStatus, ct);

        await _packageRepo.UpdateAsync(package, ct);
        if (oldSupplierId > 0 && oldSupplierId == package.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }
        else if (oldSupplierId != package.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (package.SupplierId > 0) await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        // ADR-027 (detalle): marca "confirmada con cambios" + registra el detalle si cambio precio/costo.
        var packageChange = new PendingServiceChange
        {
            ServiceType = "Paquete",
            ServiceDescription = string.IsNullOrWhiteSpace(package.PackageName) ? "Paquete" : $"Paquete {package.PackageName}",
            ServicePublicId = package.PublicId,
            Currency = package.Currency,
            OldSalePrice = oldSalePrice,
            NewSalePrice = package.SalePrice,
            OldNetCost = oldNetCost,
            NewNetCost = package.NetCost,
        };
        await _reservaService.UpdateBalanceAsync(reservaId, packageChange);

        var dto = _mapper.Map<PackageBookingDto>(package);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(package.RateId, ct);
        // B1.15: enmascarar NetCost en el response de PUT (igual que Hotel).
        await CostMasking.MaskPackageAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task DeletePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var packageId = await ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceDeleted, "PackageBooking", packageId, "Paquete", ct);
        await DeletePackageAsync(reservaId, packageId, ct);
    }

    public async Task DeletePackageAsync(int reservaId, int id, CancellationToken ct)
    {
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

        var packageConfirmed = package.ConfirmedAt != null
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsOperatorConfirmed(package)
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsResolved(package);
        await EnsureCanRemoveServiceAsync(reservaId, packageConfirmed, ct);

        // ADR-031 v2.1 (M1): limpiar asignaciones en la misma transaccion que el borrado.
        await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Package, package.Id, reservaId, ct);
        await _packageRepo.DeleteAsync(package, ct);
        if (package.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    #endregion

    #region Transfers

    public async Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetTransfersAsync(reservaId, ct);
    }

    public async Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(int reservaId, CancellationToken ct)
    {
        var dtos = await _transferRepo.Query()
            .Where(t => t.ReservaId == reservaId)
            .Include(t => t.Rate)
            .OrderBy(t => t.PickupDateTime)
            .ProjectTo<TransferBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);

        // Seguridad B1.15: enmascaramos NetCost para quien no tiene cobranzas.see_cost.
        if (!await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            // CostToConfirm es MARCA de costo (ADR-017, guia UX linea 81): se oculta junto con los montos.
            foreach (var dto in dtos) { dto.NetCost = 0m; dto.Tax = 0m; dto.CostToConfirm = false; }
        }
        return dtos;
    }

    public async Task<TransferBookingDto> GetTransferByIdAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var transferId = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);
        return await GetTransferByIdAsync(reservaId, transferId, ct);
    }

    public async Task<TransferBookingDto> GetTransferByIdAsync(int reservaId, int id, CancellationToken ct)
    {
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        // El traslado debe pertenecer a la reserva del path (defensa contra ids ajenos).
        if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        var dto = _mapper.Map<TransferBookingDto>(transfer);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(transfer.RateId, ct);
        await CostMasking.MaskTransferAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<TransferBookingDto> CreateTransferAsync(string reservaPublicIdOrLegacyId, CreateTransferRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceAdded, "TransferBooking", null, "Traslado", ct);
        return await CreateTransferAsync(reservaId, req, ct);
    }

    public async Task<TransferBookingDto> CreateTransferAsync(int reservaId, CreateTransferRequest req, CancellationToken ct)
    {
        // ADR-017 F1.3: ver nota en CreateFlightAsync. Flag OFF = byte-identico al codigo de abajo.
        if (await IsCatalogFindOrCreateEnabledAsync(ct))
            return await CreateTransferWithCatalogAsync(reservaId, req, ct);

        ValidateTransferTimes(req.PickupDateTime, req.ReturnDateTime);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        var transfer = _mapper.Map<TransferBooking>(req);
        transfer.ReservaId = reservaId;
        transfer.SupplierId = supplierId;

        // ADR-018 Ronda 7 (2026-06-06): el tipo de vehiculo es OPCIONAL. Vacio/null = no informado y se
        // persiste null (antes se coalesceaba a "Sedan"; ese default de negocio quedo derogado).
        transfer.VehicleType = NormalizeOptionalText(transfer.VehicleType);

        // B1 (zona horaria): la hora del traslado es hora local (la que ve el pasajero en el
        // itinerario), NO un instante UTC. Se guarda tal cual, sin corrimiento. ReturnDateTime
        // es opcional (solo round-trip). Ver NormalizeAirportWallClock.
        transfer.PickupDateTime = NormalizeAirportWallClock(transfer.PickupDateTime);
        if (transfer.ReturnDateTime.HasValue)
            transfer.ReturnDateTime = NormalizeAirportWallClock(transfer.ReturnDateTime.Value);

        // Auditoria ERP item 5: deadline mapeado por convencion en el ALTA; normalizado a fecha de pared.
        transfer.OperatorPaymentDeadline = NormalizeCalendarDate(transfer.OperatorPaymentDeadline);

        // Snapshot desde tarifario
        var rate = await GetRateAsync(req.RateId, ct);
        if (rate != null)
        {
            transfer.RateId = rate.Id;
            transfer.NetCost = rate.NetCost;
            transfer.SalePrice = rate.SalePrice;
            transfer.Commission = rate.Commission;
            transfer.Tax = rate.Tax; // impuesto incluido (igual que Flight): se congela del tarifario
            // Trazabilidad: guardamos en que moneda se cotizo (copiada del tarifario).
            // No afecta saldo/pagos/factura; solo deja registro de la moneda original.
            transfer.Currency = rate.Currency;
        }

        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            transfer.Status = "Solicitado";

        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Transfer {transfer.VehicleType ?? ""}".Trim(), transfer.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        // ADR-031 (bypass B1): el alta puede dejar el traslado resuelto -> exigir el titular ANTES de persistir.
        // serviceId: 0 -> alta sin Id propio aun; set = toda la reserva.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, serviceWasResolved: false, ServiceResolutionRules.IsResolved(transfer),
            PassengerNominalRules.ServiceKind.Transfer, AssignmentServiceType.Transfer, serviceId: 0, ct);

        await _transferRepo.AddAsync(transfer, ct);

        if (transfer.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);

        var dto = _mapper.Map<TransferBookingDto>(transfer);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(transfer.RateId, ct);
        // B1.15: enmascarar NetCost en el response de POST (igual que Hotel).
        await CostMasking.MaskTransferAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<TransferBookingDto> UpdateTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateTransferRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var transferId = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceEdited, "TransferBooking", transferId, "Traslado", ct);
        return await UpdateTransferAsync(reservaId, transferId, req, ct);
    }

    public async Task<TransferBookingDto> UpdateTransferAsync(int reservaId, int id, UpdateTransferRequest req, CancellationToken ct)
    {
        ValidateTransferTimes(req.PickupDateTime, req.ReturnDateTime);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        // B1.15 Fase 0' (CODE-04): inmutabilidad post-CAE / post-voucher.
        var blockReason = await MutationGuards.GetBookingMutationBlockReasonAsync(_db, reservaId, "Transfer", ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateTransferAsync rejected. TransferId={TransferId} ReservaId={ReservaId}. Reason={Reason}",
                id, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        var oldNetCost = transfer.NetCost;
        // ADR-027 (hallazgo #10): precio ANTES del map para detectar "el operador confirmo con cambios".
        var oldSalePrice = transfer.SalePrice;
        var oldSupplierId = transfer.SupplierId;
        var oldStatus = transfer.Status;
        // Plata viva (familia R1): moneda ANTES del map, para la guarda de reasignacion de operador/moneda de abajo.
        var oldCurrency = transfer.Currency;
        // ADR-031: estado de resolucion ANTES de la edicion (para gatear solo la transicion a resuelto).
        var transferWasResolved = ServiceResolutionRules.IsResolved(transfer);
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, transfer);
        transfer.SupplierId = supplierId;

        // ADR-018 (anti-clobber): el map IGNORA ProductName. La ficha inline reenvia el texto del vendedor
        // (round-trip) y lo actualizamos; el modal viejo NO lo manda (null/vacio) y PRESERVAMOS el persistido,
        // para que la identidad del traslado no revierta a "Transfer "/ruta.
        if (!string.IsNullOrWhiteSpace(req.ProductName))
            transfer.ProductName = req.ProductName.Trim();

        // ADR-018 Ronda 7: tipo de vehiculo opcional. Sin anti-clobber a proposito: la ficha lo reenvia
        // en cada edicion (round-trip), asi que null/vacio es un borrado legitimo y se persiste null.
        transfer.VehicleType = NormalizeOptionalText(transfer.VehicleType);

        // Fuga 3 (F1b): el map ignora NetCost/Tax/Commission; se aplican segun permiso del caller.
        (transfer.NetCost, transfer.Tax, transfer.Commission) = await ResolveUpdateCostFieldsAsync(
            serviceType: "Transfer", serviceId: id,
            persistedNetCost: transfer.NetCost, persistedTax: transfer.Tax,
            requestNetCost: req.NetCost, requestTax: req.Tax,
            requestCommission: req.Commission, requestSalePrice: req.SalePrice, ct: ct);

        // B1 (zona horaria): misma normalizacion que en el alta. Hora local del traslado,
        // sin convertir a UTC. Ver NormalizeAirportWallClock.
        transfer.PickupDateTime = NormalizeAirportWallClock(transfer.PickupDateTime);
        if (transfer.ReturnDateTime.HasValue)
            transfer.ReturnDateTime = NormalizeAirportWallClock(transfer.ReturnDateTime.Value);

        // Auditoria ERP item 5 (anti-clobber): el map IGNORA el deadline; asignamos solo si viene con valor.
        if (req.OperatorPaymentDeadline.HasValue)
            transfer.OperatorPaymentDeadline = NormalizeCalendarDate(req.OperatorPaymentDeadline.Value);

        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            transfer.RateId = rateId.Value;

        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            transfer.Status = "Solicitado";

        var label = $"Transfer {transfer.VehicleType ?? ""}".Trim();
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, transfer.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, transfer.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        // ADR-031 (bypass B1): la edicion puede dejar el traslado resuelto -> exigir el titular ANTES de persistir.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, transferWasResolved, ServiceResolutionRules.IsResolved(transfer),
            PassengerNominalRules.ServiceKind.Transfer, AssignmentServiceType.Transfer, transfer.Id, ct);

        // Plata viva (familia R1): impedir reasignar operador/moneda de un traslado confirmado y pagado al operador
        // saliente sin factura que ancle el receivable. Ver EnsureServiceOperatorOrCurrencyChangeHas...
        await GuardOperatorOrCurrencyReassignmentAsync(
            reservaId, CancellableServiceTable.Transfer, transfer.Id, oldSupplierId, transfer.SupplierId,
            oldCurrency, transfer.Currency, debtServiceType: "Traslado", oldStatus: oldStatus, ct);

        await _transferRepo.UpdateAsync(transfer, ct);
        if (oldSupplierId > 0 && oldSupplierId == transfer.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }
        else if (oldSupplierId != transfer.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (transfer.SupplierId > 0) await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        // ADR-027 (detalle): marca "confirmada con cambios" + registra el detalle si cambio precio/costo.
        var transferChange = new PendingServiceChange
        {
            ServiceType = "Traslado",
            ServiceDescription = string.IsNullOrWhiteSpace(transfer.ProductName) ? "Traslado" : $"Traslado {transfer.ProductName}",
            ServicePublicId = transfer.PublicId,
            Currency = transfer.Currency,
            OldSalePrice = oldSalePrice,
            NewSalePrice = transfer.SalePrice,
            OldNetCost = oldNetCost,
            NewNetCost = transfer.NetCost,
        };
        await _reservaService.UpdateBalanceAsync(reservaId, transferChange);

        var dto = _mapper.Map<TransferBookingDto>(transfer);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(transfer.RateId, ct);
        // B1.15: enmascarar NetCost en el response de PUT (igual que Hotel).
        await CostMasking.MaskTransferAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task DeleteTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var transferId = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceDeleted, "TransferBooking", transferId, "Traslado", ct);
        await DeleteTransferAsync(reservaId, transferId, ct);
    }

    public async Task DeleteTransferAsync(int reservaId, int id, CancellationToken ct)
    {
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        var transferConfirmed = transfer.ConfirmedAt != null
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsOperatorConfirmed(transfer)
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsResolved(transfer);
        await EnsureCanRemoveServiceAsync(reservaId, transferConfirmed, ct);

        // ADR-031 v2.1 (M1): limpiar asignaciones en la misma transaccion que el borrado.
        await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Transfer, transfer.Id, reservaId, ct);
        await _transferRepo.DeleteAsync(transfer, ct);
        if (transfer.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    // ADR-020 (F5): manda el servicio. Si fue confirmado por el operador no se borra, se cancela.
    // Los servicios tipados (flight/hotel/...) no tienen el link Payment.ServicioReservaId, asi que
    // genericServiceId es siempre null aca.
    private async Task EnsureCanRemoveServiceAsync(int reservaId, bool serviceIsOperatorConfirmed, CancellationToken ct)
    {
        var blockReason = await DeleteGuards.GetServiceDeleteBlockReasonAsync(
            _db, reservaId, serviceIsOperatorConfirmed, genericServiceId: null, ct, _logger);
        if (blockReason != null)
        {
            _logger.LogInformation(
                "DeleteService rejected (BookingService). ReservaId={ReservaId}. Reason={Reason}",
                reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }
    }

    private async Task<int> ResolveSupplierIdAsync(string supplierPublicIdOrLegacyId, CancellationToken ct)
    {
        var supplierId = await _supplierRepo.Query()
            .AsNoTracking()
            .ResolveInternalIdAsync(supplierPublicIdOrLegacyId, ct);

        if (!supplierId.HasValue)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        return supplierId.Value;
    }

    #endregion

    #region Assistances

    // Asistencia al viajero (seguro). Espejo EXACTO de la region Hotels: mismas validaciones,
    // mismo snapshot de tarifario, mismos guards (Solicitado en Presupuesto, status block,
    // downgrade block, mutation guard post-CAE/voucher) y mismo enmascarado de NetCost en TODOS
    // los returns. La vigencia (ValidFrom/ValidTo) se trata como las fechas de Hotel: date-only.

    public async Task<IEnumerable<AssistanceBookingDto>> GetAssistancesAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetAssistancesAsync(reservaId, ct);
    }

    public async Task<IEnumerable<AssistanceBookingDto>> GetAssistancesAsync(int reservaId, CancellationToken ct)
    {
        var dtos = await _assistanceRepo.Query()
            .Where(a => a.ReservaId == reservaId)
            .Include(a => a.Rate)
            .OrderBy(a => a.ValidFrom)
            .ProjectTo<AssistanceBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);

        // Seguridad B1.15: enmascaramos NetCost para quien no tiene cobranzas.see_cost.
        if (!await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct))
        {
            // CostToConfirm es MARCA de costo (ADR-017, guia UX linea 81): se oculta junto con los montos.
            foreach (var dto in dtos) { dto.NetCost = 0m; dto.Tax = 0m; dto.CostToConfirm = false; }
        }
        return dtos;
    }

    public async Task<AssistanceBookingDto> GetAssistanceByIdAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var assistanceId = await ResolveRequiredIdAsync<AssistanceBooking>(publicIdOrLegacyId, ct);
        return await GetAssistanceByIdAsync(reservaId, assistanceId, ct);
    }

    public async Task<AssistanceBookingDto> GetAssistanceByIdAsync(int reservaId, int id, CancellationToken ct)
    {
        var assistance = await _assistanceRepo.GetByIdAsync(id, ct);
        // La asistencia debe pertenecer a la reserva del path (defensa contra ids ajenos).
        if (assistance == null || assistance.ReservaId != reservaId) throw new KeyNotFoundException("Asistencia no encontrada");

        var dto = _mapper.Map<AssistanceBookingDto>(assistance);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(assistance.RateId, ct);
        await CostMasking.MaskAssistanceAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<AssistanceBookingDto> CreateAssistanceAsync(string reservaPublicIdOrLegacyId, CreateAssistanceRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceAdded, "AssistanceBooking", null, "Asistencia", ct);
        return await CreateAssistanceAsync(reservaId, req, ct);
    }

    public async Task<AssistanceBookingDto> CreateAssistanceAsync(int reservaId, CreateAssistanceRequest req, CancellationToken ct)
    {
        // ADR-017 F1.3: ver nota en CreateFlightAsync. Flag OFF = byte-identico al codigo de abajo.
        if (await IsCatalogFindOrCreateEnabledAsync(ct))
            return await CreateAssistanceWithCatalogAsync(reservaId, req, ct);

        ValidateAssistanceValidity(req.ValidFrom, req.ValidTo);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var rate = await GetRateAsync(req.RateId, ct);
        // Si la tarifa define proveedor lo usa; sino, el del request.
        var supplierId = rate?.SupplierId ?? await ResolveSupplierIdAsync(req.SupplierId, ct);

        var assistance = _mapper.Map<AssistanceBooking>(req);
        assistance.ReservaId = reservaId;
        assistance.SupplierId = supplierId;

        // Bug 2026-06-06: la ficha inline manda ValidFrom/ValidTo como fecha pelada (Kind=Unspecified)
        // y Npgsql las rechaza en timestamptz. Normalizamos a fecha de pared. Ver NormalizeCalendarDate.
        assistance.ValidFrom = NormalizeCalendarDate(assistance.ValidFrom);
        assistance.ValidTo = NormalizeCalendarDate(assistance.ValidTo);

        // Auditoria ERP item 5: deadline mapeado por convencion en el ALTA; normalizado a fecha de pared.
        assistance.OperatorPaymentDeadline = NormalizeCalendarDate(assistance.OperatorPaymentDeadline);

        if (rate != null)
        {
            ApplyAssistanceRateSnapshot(assistance, rate);
        }

        // En Presupuesto el status siempre es "Solicitado" — no es una reserva real.
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            assistance.Status = "Solicitado";

        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Asistencia {assistance.PlanType ?? "seguro"}", assistance.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        // ADR-031 (bypass B1): el alta puede dejar la asistencia resuelta -> exigir nombre + documento +
        // fecha de nacimiento de TODOS los declarados ANTES de persistir (la prima depende de la edad).
        // serviceId: 0 -> alta sin Id propio aun; set = toda la reserva.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, serviceWasResolved: false, ServiceResolutionRules.IsResolved(assistance),
            PassengerNominalRules.ServiceKind.Assistance, AssignmentServiceType.Assistance, serviceId: 0, ct);

        await _assistanceRepo.AddAsync(assistance, ct);

        if (assistance.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(assistance.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);

        var dto = _mapper.Map<AssistanceBookingDto>(assistance);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(assistance.RateId, ct);
        // B1.15: enmascarar NetCost en el response de POST (igual que Hotel).
        await CostMasking.MaskAssistanceAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<AssistanceBookingDto> UpdateAssistanceAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateAssistanceRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var assistanceId = await ResolveRequiredIdAsync<AssistanceBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceEdited, "AssistanceBooking", assistanceId, "Asistencia", ct);
        return await UpdateAssistanceAsync(reservaId, assistanceId, req, ct);
    }

    public async Task<AssistanceBookingDto> UpdateAssistanceAsync(int reservaId, int id, UpdateAssistanceRequest req, CancellationToken ct)
    {
        ValidateAssistanceValidity(req.ValidFrom, req.ValidTo);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var assistance = await _assistanceRepo.GetByIdAsync(id, ct);
        if (assistance == null || assistance.ReservaId != reservaId) throw new KeyNotFoundException("Asistencia no encontrada");

        // B1.15 Fase 0' (CODE-04): inmutabilidad post-CAE / post-voucher. Asistencia entra
        // al guard generico de bookings igual que los otros 4 tipos.
        var blockReason = await MutationGuards.GetBookingMutationBlockReasonAsync(_db, reservaId, "Assistance", ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateAssistanceAsync rejected. AssistanceId={AssistanceId} ReservaId={ReservaId}. Reason={Reason}",
                id, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        var oldSupplierId = assistance.SupplierId;
        var oldStatus = assistance.Status;
        // Plata viva (familia R1): moneda ANTES del map, para la guarda de reasignacion de operador/moneda de abajo.
        var oldCurrency = assistance.Currency;
        // ADR-031: estado de resolucion ANTES de la edicion (para gatear solo la transicion a resuelto).
        var assistanceWasResolved = ServiceResolutionRules.IsResolved(assistance);
        // ADR-027 (hallazgo #10): precio/costo ANTES del map para detectar "el operador confirmo con cambios".
        var oldSalePrice = assistance.SalePrice;
        var oldNetCost = assistance.NetCost;

        _mapper.Map(req, assistance);
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);
        assistance.SupplierId = supplierId;

        // Bug 2026-06-06: misma normalizacion que en el alta — fechas peladas (Kind=Unspecified) de la
        // ficha inline son rechazadas por Npgsql en timestamptz. Ver NormalizeCalendarDate.
        assistance.ValidFrom = NormalizeCalendarDate(assistance.ValidFrom);
        assistance.ValidTo = NormalizeCalendarDate(assistance.ValidTo);

        // Auditoria ERP item 5 (anti-clobber): el map IGNORA el deadline; asignamos solo si viene con valor.
        if (req.OperatorPaymentDeadline.HasValue)
            assistance.OperatorPaymentDeadline = NormalizeCalendarDate(req.OperatorPaymentDeadline.Value);

        // Fuga 3 (F1b): el map ignora NetCost/Tax/Commission; se aplican segun permiso del caller.
        (assistance.NetCost, assistance.Tax, assistance.Commission) = await ResolveUpdateCostFieldsAsync(
            serviceType: "Assistance", serviceId: id,
            persistedNetCost: assistance.NetCost, persistedTax: assistance.Tax,
            requestNetCost: req.NetCost, requestTax: req.Tax,
            requestCommission: req.Commission, requestSalePrice: req.SalePrice, ct: ct);

        // Si viene un RateId nuevo, solo se re-vincula la tarifa (RateId), igual que en
        // Flight/Package. OJO: NO se re-aplica el snapshot de precios en el update — los
        // costos ya quedaron resueltos arriba segun el permiso del caller.
        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            assistance.RateId = rateId.Value;

        // En Presupuesto el status siempre es "Solicitado".
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
            assistance.Status = "Solicitado";

        var label = $"Asistencia {assistance.PlanType ?? "seguro"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, assistance.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, assistance.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        // ADR-031 (bypass B1): la edicion puede dejar la asistencia resuelta -> exigir nombre +
        // documento + fecha de nacimiento de TODOS los declarados ANTES de persistir.
        await EnsureNominalCoverageBeforeResolvingAsync(
            reservaId, assistanceWasResolved, ServiceResolutionRules.IsResolved(assistance),
            PassengerNominalRules.ServiceKind.Assistance, AssignmentServiceType.Assistance, assistance.Id, ct);

        // Plata viva (familia R1): impedir reasignar operador/moneda de una asistencia confirmada y pagada al operador
        // saliente sin factura que ancle el receivable. Ver EnsureServiceOperatorOrCurrencyChangeHas...
        await GuardOperatorOrCurrencyReassignmentAsync(
            reservaId, CancellableServiceTable.Assistance, assistance.Id, oldSupplierId, assistance.SupplierId,
            oldCurrency, assistance.Currency, debtServiceType: "Asistencia", oldStatus: oldStatus, ct);

        await _assistanceRepo.UpdateAsync(assistance, ct);
        if (oldSupplierId > 0 && oldSupplierId == assistance.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(assistance.SupplierId, ct);
        }
        else if (oldSupplierId != assistance.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (assistance.SupplierId > 0) await _supplierService.UpdateBalanceAsync(assistance.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        // ADR-027 (detalle): marca "confirmada con cambios" + registra el detalle si cambio precio/costo.
        var assistanceChange = new PendingServiceChange
        {
            ServiceType = "Asistencia",
            ServiceDescription = string.IsNullOrWhiteSpace(assistance.PlanType) ? "Asistencia al viajero" : $"Asistencia {assistance.PlanType}",
            ServicePublicId = assistance.PublicId,
            Currency = assistance.Currency,
            OldSalePrice = oldSalePrice,
            NewSalePrice = assistance.SalePrice,
            OldNetCost = oldNetCost,
            NewNetCost = assistance.NetCost,
        };
        await _reservaService.UpdateBalanceAsync(reservaId, assistanceChange);

        var dto = _mapper.Map<AssistanceBookingDto>(assistance);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(assistance.RateId, ct);
        // B1.15: enmascarar NetCost en el response de PUT (igual que Hotel).
        await CostMasking.MaskAssistanceAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task DeleteAssistanceAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var assistanceId = await ResolveRequiredIdAsync<AssistanceBooking>(publicIdOrLegacyId, ct);
        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes del candado de autorizacion.
        await GuardServicesEditableByStateAsync(reservaId, ct);
        await GuardReservaLockAsync(reservaId, ReservaEditAuthorizationOperations.ServiceDeleted, "AssistanceBooking", assistanceId, "Asistencia", ct);
        await DeleteAssistanceAsync(reservaId, assistanceId, ct);
    }

    public async Task DeleteAssistanceAsync(int reservaId, int id, CancellationToken ct)
    {
        var assistance = await _assistanceRepo.GetByIdAsync(id, ct);
        if (assistance == null || assistance.ReservaId != reservaId) throw new KeyNotFoundException("Asistencia no encontrada");

        var assistanceConfirmed = assistance.ConfirmedAt != null
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsOperatorConfirmed(assistance)
            || TravelApi.Domain.Reservations.ServiceResolutionRules.IsResolved(assistance);
        await EnsureCanRemoveServiceAsync(reservaId, assistanceConfirmed, ct);

        // ADR-031 v2.1 (M1): limpiar asignaciones en la misma transaccion que el borrado.
        await CleanupAssignmentsForDeletedServiceAsync(AssignmentServiceType.Assistance, assistance.Id, reservaId, ct);
        await _assistanceRepo.DeleteAsync(assistance, ct);
        if (assistance.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(assistance.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    #endregion

    // ==================== Status-only updates (cuenta corriente del proveedor) ====================
    // Permiten al operador confirmar/cambiar el status de un servicio sin entrar a la reserva,
    // desde el listado de servicios del proveedor. Reusan los guards existentes de
    // ReservaCapacityRules para mantener coherencia (Operativo requiere Confirmado, Presupuesto
    // fuerza Solicitado, no degradar si hay SupplierPayments).

    public async Task<HotelBookingDto> UpdateHotelStatusAsync(string publicIdOrLegacyId, string newStatus, string? confirmationNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStatus)) throw new ArgumentException("El estado es obligatorio.");
        var hotelId = await ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct);
        var hotel = await _hotelRepo.GetByIdAsync(hotelId, ct)
            ?? throw new KeyNotFoundException("Hotel no encontrado");

        // ADR-035: candado por ESTADO primero. Si la reserva esta en solo-lectura
        // (Closed/Lost/Cancelled/PendingOperatorRefund) ni siquiera se puede cambiar el status del
        // servicio (incluido forzar "Solicitado"); se rechaza de raiz, sin autorizacion que valga.
        await GuardServicesEditableByStateAsync(hotel.ReservaId, ct);

        // ADR-020 decision #8 (2026-06-08): cambiar el ESTADO/RESOLUCION de un servicio NO pide
        // candado, aunque la reserva este confirmada. Esto refleja lo que hizo el OPERADOR afuera
        // (confirmo / cambio / cancelo el servicio), no una decision de la agencia; ademas es
        // justo la accion que dispara la regresion automatica Confirmed->InManagement (que reabre
        // la edicion), asi que pedir candado para esto seria contradictorio. La cancelacion/borrado
        // iniciado por la agencia (papelera, Delete*Async) SI sigue bajo candado.
        var oldStatus = hotel.Status;
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, hotel.ReservaId, ct))
            newStatus = "Solicitado";

        var label = $"Hotel {hotel.HotelName ?? "sin nombre"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, hotel.ReservaId, label, newStatus, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, hotel.ReservaId, label, oldStatus, newStatus, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        var hotelWasResolved = ServiceResolutionRules.IsResolved(hotel);
        hotel.Status = newStatus;
        // ADR-031: si esta operacion RESUELVE el hotel (no lo estaba antes), exigir el titular con nombre.
        await EnsureNominalCoverageBeforeResolvingAsync(
            hotel.ReservaId, hotelWasResolved, ServiceResolutionRules.IsResolved(hotel),
            PassengerNominalRules.ServiceKind.Hotel, AssignmentServiceType.Hotel, hotel.Id, ct);
        StampServiceCancellation(
            wasCancelled: WorkflowStatusHelper.MapGenericStatus(oldStatus ?? string.Empty) == WorkflowStatuses.Cancelado,
            isCancelled: WorkflowStatusHelper.MapGenericStatus(newStatus) == WorkflowStatuses.Cancelado,
            setCancelledAt: value => hotel.CancelledAt = value,
            setCancelledBy: (id, name) => { hotel.CancelledByUserId = id; hotel.CancelledByUserName = name; });
        if (confirmationNumber != null)
            hotel.ConfirmationNumber = string.IsNullOrWhiteSpace(confirmationNumber) ? null : confirmationNumber.Trim();
        await _hotelRepo.UpdateAsync(hotel, ct);
        if (hotel.SupplierId > 0) await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        // ADR-020: el cambio de estado mueve la resolucion -> recalcular saldo (ConfirmedSale) y
        // disparar el motor de estados (puede confirmar/regresar el file).
        await _reservaService.UpdateBalanceAsync(hotel.ReservaId);
        var dto = _mapper.Map<HotelBookingDto>(hotel);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(hotel.RateId, ct);
        // Mismo enmascarado de NetCost que Create/Update: si el caller no es Admin
        // y no tiene cobranzas.see_cost, el costo neto vuelve en 0. El PATCH /status
        // lo consume la cuenta corriente del proveedor, donde un vendedor sin permiso
        // de costos no debe ver el neto.
        await CostMasking.MaskHotelAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<TransferBookingDto> UpdateTransferStatusAsync(string publicIdOrLegacyId, string newStatus, string? confirmationNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStatus)) throw new ArgumentException("El estado es obligatorio.");
        var transferId = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);
        var transfer = await _transferRepo.GetByIdAsync(transferId, ct)
            ?? throw new KeyNotFoundException("Transfer no encontrado");

        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes de tocar el status del servicio.
        await GuardServicesEditableByStateAsync(transfer.ReservaId, ct);

        // ADR-020 decision #8: cambiar el estado/resolucion del traslado refleja lo que hizo el
        // operador afuera -> sin candado (ver detalle en UpdateHotelStatusAsync).
        var oldStatus = transfer.Status;
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, transfer.ReservaId, ct))
            newStatus = "Solicitado";

        var label = $"Transfer {transfer.VehicleType ?? ""}".Trim();
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, transfer.ReservaId, label, newStatus, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, transfer.ReservaId, label, oldStatus, newStatus, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        var transferWasResolved = ServiceResolutionRules.IsResolved(transfer);
        transfer.Status = newStatus;
        // ADR-031: si esta operacion RESUELVE el traslado (no lo estaba antes), exigir el titular con nombre.
        await EnsureNominalCoverageBeforeResolvingAsync(
            transfer.ReservaId, transferWasResolved, ServiceResolutionRules.IsResolved(transfer),
            PassengerNominalRules.ServiceKind.Transfer, AssignmentServiceType.Transfer, transfer.Id, ct);
        StampServiceCancellation(
            wasCancelled: WorkflowStatusHelper.MapGenericStatus(oldStatus ?? string.Empty) == WorkflowStatuses.Cancelado,
            isCancelled: WorkflowStatusHelper.MapGenericStatus(newStatus) == WorkflowStatuses.Cancelado,
            setCancelledAt: value => transfer.CancelledAt = value,
            setCancelledBy: (id, name) => { transfer.CancelledByUserId = id; transfer.CancelledByUserName = name; });
        if (confirmationNumber != null)
            transfer.ConfirmationNumber = string.IsNullOrWhiteSpace(confirmationNumber) ? null : confirmationNumber.Trim();
        await _transferRepo.UpdateAsync(transfer, ct);
        if (transfer.SupplierId > 0) await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        // ADR-020: recalcular saldo (ConfirmedSale) + disparar el motor de estados.
        await _reservaService.UpdateBalanceAsync(transfer.ReservaId);
        var dto = _mapper.Map<TransferBookingDto>(transfer);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(transfer.RateId, ct);
        // Enmascarar NetCost igual que el resto de los endpoints de booking.
        await CostMasking.MaskTransferAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<PackageBookingDto> UpdatePackageStatusAsync(string publicIdOrLegacyId, string newStatus, string? confirmationNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStatus)) throw new ArgumentException("El estado es obligatorio.");
        var packageId = await ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct);
        var package = await _packageRepo.GetByIdAsync(packageId, ct)
            ?? throw new KeyNotFoundException("Paquete no encontrado");

        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes de tocar el status del servicio.
        await GuardServicesEditableByStateAsync(package.ReservaId, ct);

        // ADR-020 decision #8: cambiar el estado/resolucion del paquete refleja lo que hizo el
        // operador afuera -> sin candado (ver detalle en UpdateHotelStatusAsync).
        var oldStatus = package.Status;
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, package.ReservaId, ct))
            newStatus = "Solicitado";

        var label = $"Paquete {package.PackageName ?? "sin nombre"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, package.ReservaId, label, newStatus, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, package.ReservaId, label, oldStatus, newStatus, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        var packageWasResolved = ServiceResolutionRules.IsResolved(package);
        package.Status = newStatus;
        // ADR-031: si esta operacion RESUELVE el paquete (no lo estaba antes), exigir el nombre de TODOS.
        await EnsureNominalCoverageBeforeResolvingAsync(
            package.ReservaId, packageWasResolved, ServiceResolutionRules.IsResolved(package),
            PassengerNominalRules.ServiceKind.Package, AssignmentServiceType.Package, package.Id, ct);
        StampServiceCancellation(
            wasCancelled: WorkflowStatusHelper.MapGenericStatus(oldStatus ?? string.Empty) == WorkflowStatuses.Cancelado,
            isCancelled: WorkflowStatusHelper.MapGenericStatus(newStatus) == WorkflowStatuses.Cancelado,
            setCancelledAt: value => package.CancelledAt = value,
            setCancelledBy: (id, name) => { package.CancelledByUserId = id; package.CancelledByUserName = name; });
        if (confirmationNumber != null)
            package.ConfirmationNumber = string.IsNullOrWhiteSpace(confirmationNumber) ? null : confirmationNumber.Trim();
        await _packageRepo.UpdateAsync(package, ct);
        if (package.SupplierId > 0) await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        // ADR-020: recalcular saldo (ConfirmedSale) + disparar el motor de estados.
        await _reservaService.UpdateBalanceAsync(package.ReservaId);
        var dto = _mapper.Map<PackageBookingDto>(package);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(package.RateId, ct);
        // Enmascarar NetCost igual que el resto de los endpoints de booking.
        await CostMasking.MaskPackageAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    public async Task<FlightSegmentDto> UpdateFlightStatusAsync(string publicIdOrLegacyId, string newStatus, string? confirmationNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStatus)) throw new ArgumentException("El estado es obligatorio.");
        var flightId = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);
        var flight = await _flightRepo.GetByIdAsync(flightId, ct)
            ?? throw new KeyNotFoundException("Vuelo no encontrado");

        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes de tocar el status del servicio
        // (incluido el forzado de "Solicitado" mas abajo).
        await GuardServicesEditableByStateAsync(flight.ReservaId, ct);

        // ADR-020 decision #8: cambiar el estado/resolucion del aereo refleja lo que hizo el
        // operador afuera -> sin candado (ver detalle en UpdateHotelStatusAsync).
        var oldStatus = flight.Status;
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, flight.ReservaId, ct))
            newStatus = "Solicitado";

        // ADR-018: identidad visible via ServiceDisplayName (ProductName si no hay aerolinea/numero).
        var label = $"Vuelo {ServiceDisplayName.ForFlight(flight.ProductName, flight.AirlineCode, flight.FlightNumber)}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, flight.ReservaId, label, newStatus, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, flight.ReservaId, label, oldStatus, newStatus, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        var flightWasResolvedOnStatus = ServiceResolutionRules.IsResolved(flight);
        flight.Status = newStatus;
        // ADR-031: el aereo RESUELVE por TicketIssuedAt (emitido), no por status — confirmar el PNR (HK)
        // no resuelve, asi que hoy este gate es defensivo (no-op). Se cablea igual para que, si en el
        // futuro algun status de vuelo resolviera por esta via, la cobertura nominal se exija ANTES de
        // persistir. La emision real (MarkFlightTicketIssuedAsync) tiene su propio gate efectivo.
        await EnsureNominalCoverageBeforeResolvingAsync(
            flight.ReservaId, flightWasResolvedOnStatus, ServiceResolutionRules.IsResolved(flight),
            PassengerNominalRules.ServiceKind.Flight, AssignmentServiceType.Flight, flight.Id, ct);
        // El aereo mapea por codigo IATA (UN/UC/HX/NO = cancelado), no por texto generico.
        StampServiceCancellation(
            wasCancelled: WorkflowStatusHelper.MapFlightStatus(oldStatus ?? string.Empty) == WorkflowStatuses.Cancelado,
            isCancelled: WorkflowStatusHelper.MapFlightStatus(newStatus) == WorkflowStatuses.Cancelado,
            setCancelledAt: value => flight.CancelledAt = value,
            setCancelledBy: (id, name) => { flight.CancelledByUserId = id; flight.CancelledByUserName = name; });
        // FlightSegment no tiene ConfirmationNumber; el codigo de confirmacion del proveedor
        // se almacena en PNR (ver SupplierService.BuildSupplierServicesQuery).
        if (confirmationNumber != null)
            flight.PNR = string.IsNullOrWhiteSpace(confirmationNumber) ? null : confirmationNumber.Trim();
        await _flightRepo.UpdateAsync(flight, ct);
        if (flight.SupplierId > 0) await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        // ADR-020: recalcular saldo (ConfirmedSale) + disparar el motor de estados.
        await _reservaService.UpdateBalanceAsync(flight.ReservaId);
        var dto = _mapper.Map<FlightSegmentDto>(flight);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(flight.RateId, ct);
        // Enmascarar NetCost igual que el resto de los endpoints de booking.
        await CostMasking.MaskFlightAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    /// <summary>
    /// ADR-020 F2: accion "Marcar emitido" del aereo. Estampa TicketIssuedAt + quien (lo que RESUELVE
    /// el segmento). El TicketNumber es opcional (A3): si viene vacio igual se permite (el ticket suele
    /// llegar despues por mail). Recalcula el saldo (ConfirmedSale) y dispara el motor (puede confirmar).
    /// </summary>
    public async Task<FlightSegmentDto> MarkFlightTicketIssuedAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, string? ticketNumber, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var flightId = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);
        var flight = await _flightRepo.GetByIdAsync(flightId, ct) ?? throw new KeyNotFoundException("Vuelo no encontrado");
        if (flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        // ADR-035: candado por ESTADO primero. Emitir el ticket es una mutacion de servicio: en una reserva
        // de solo-lectura (terminal) se rechaza de raiz, sin autorizacion que valga.
        await GuardServicesEditableByStateAsync(flight.ReservaId, ct);

        // ADR-020 decision #8: marcar emitido RESUELVE el segmento (es la realidad del operador),
        // no es una edicion de la agencia -> sin candado (ver detalle en UpdateHotelStatusAsync).
        var flightWasResolved = ServiceResolutionRules.IsResolved(flight);
        var (userId, userName) = GetActor();
        flight.TicketIssuedAt = DateTime.UtcNow;
        flight.TicketIssuedByUserId = userId;
        flight.TicketIssuedByUserName = userName;
        if (!string.IsNullOrWhiteSpace(ticketNumber)) flight.TicketNumber = ticketNumber.Trim();

        // ADR-031: emitir el ticket RESUELVE el aereo. Exigir nombre + documento de TODOS los pasajeros
        // declarados ANTES de persistir (un ticket sin nombre/documento es inservible y caro de corregir).
        await EnsureNominalCoverageBeforeResolvingAsync(
            flight.ReservaId, flightWasResolved, ServiceResolutionRules.IsResolved(flight),
            PassengerNominalRules.ServiceKind.Flight, AssignmentServiceType.Flight, flight.Id, ct);

        await _flightRepo.UpdateAsync(flight, ct);
        await _reservaService.UpdateBalanceAsync(flight.ReservaId);

        var dto = _mapper.Map<FlightSegmentDto>(flight);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(flight.RateId, ct);
        await CostMasking.MaskFlightAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    /// <summary>
    /// ADR-020 F2: marca "no requiere confirmacion" del traslado (cualquier vendedor). Con la marca el
    /// traslado cuenta como RESUELTO aunque su Status no sea Confirmado. Registra quien/cuando. Recalcula
    /// el saldo y dispara el motor.
    /// </summary>
    public async Task<TransferBookingDto> MarkTransferNoConfirmationRequiredAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var transferId = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);
        var transfer = await _transferRepo.GetByIdAsync(transferId, ct) ?? throw new KeyNotFoundException("Traslado no encontrado");
        if (transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        // ADR-035: candado por ESTADO primero. Marcar "no requiere confirmacion" es una mutacion de servicio:
        // en una reserva de solo-lectura (terminal) se rechaza de raiz, sin autorizacion que valga.
        await GuardServicesEditableByStateAsync(transfer.ReservaId, ct);

        // ADR-020 decision #8: marcar "no requiere confirmacion" RESUELVE el traslado (realidad del
        // operador), no es una edicion de la agencia -> sin candado (ver detalle en UpdateHotelStatusAsync).
        var transferWasResolved = ServiceResolutionRules.IsResolved(transfer);
        var (userId, userName) = GetActor();
        transfer.NoConfirmationRequired = true;
        transfer.NoConfirmationMarkedAt = DateTime.UtcNow;
        transfer.NoConfirmationMarkedByUserId = userId;
        transfer.NoConfirmationMarkedByUserName = userName;

        // ADR-031: la marca "no requiere confirmacion" RESUELVE el traslado (si no lo estaba). Exigir el
        // titular con nombre antes de persistir (mismo criterio que confirmar el traslado por status).
        await EnsureNominalCoverageBeforeResolvingAsync(
            transfer.ReservaId, transferWasResolved, ServiceResolutionRules.IsResolved(transfer),
            PassengerNominalRules.ServiceKind.Transfer, AssignmentServiceType.Transfer, transfer.Id, ct);

        await _transferRepo.UpdateAsync(transfer, ct);
        await _reservaService.UpdateBalanceAsync(transfer.ReservaId);

        var dto = _mapper.Map<TransferBookingDto>(transfer);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(transfer.RateId, ct);
        await CostMasking.MaskTransferAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }

    /// <summary>Identidad del actor desde el HttpContext (claims). Null en contextos sin request.</summary>
    private (string? userId, string? userName) GetActor()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        var userId = user?.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        var userName = user?.FindFirstValue(System.Security.Claims.ClaimTypes.Name) ?? user?.Identity?.Name;
        return (userId, userName);
    }

    /// <summary>
    /// ADR-020 (decision #8): estampa o limpia la marca de cancelacion de un servicio cuando un
    /// cambio de estado via Update*StatusAsync lo lleva a (o lo saca de) CANCELADO. Este camino NO
    /// pide candado — refleja la realidad del operador, no una edicion de la agencia —, pero el
    /// movimiento de plata que provoca (el servicio sale de ConfirmedSale, baja el saldo del cliente)
    /// tiene que quedar auditado con quien/cuando, igual que la cancelacion de la papelera (F5).
    /// Si el servicio sale de cancelado (se re-solicito o re-confirmo), la marca se limpia.
    /// </summary>
    private void StampServiceCancellation(
        bool wasCancelled,
        bool isCancelled,
        Action<DateTime?> setCancelledAt,
        Action<string?, string?> setCancelledBy)
    {
        if (isCancelled && !wasCancelled)
        {
            var (userId, userName) = GetActor();
            setCancelledAt(DateTime.UtcNow);
            setCancelledBy(userId, userName);
        }
        else if (!isCancelled && wasCancelled)
        {
            setCancelledAt(null);
            setCancelledBy(null, null);
        }
    }

    public async Task<AssistanceBookingDto> UpdateAssistanceStatusAsync(string publicIdOrLegacyId, string newStatus, string? confirmationNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newStatus)) throw new ArgumentException("El estado es obligatorio.");
        var assistanceId = await ResolveRequiredIdAsync<AssistanceBooking>(publicIdOrLegacyId, ct);
        var assistance = await _assistanceRepo.GetByIdAsync(assistanceId, ct)
            ?? throw new KeyNotFoundException("Asistencia no encontrada");

        // ADR-035: candado por ESTADO primero (terminal/solo-lectura), antes de tocar el status del servicio.
        await GuardServicesEditableByStateAsync(assistance.ReservaId, ct);

        // ADR-020 decision #8: cambiar el estado/resolucion de la asistencia refleja lo que hizo el
        // operador afuera -> sin candado (ver detalle en UpdateHotelStatusAsync).
        var oldStatus = assistance.Status;
        if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, assistance.ReservaId, ct))
            newStatus = "Solicitado";

        var label = $"Asistencia {assistance.PlanType ?? "seguro"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, assistance.ReservaId, label, newStatus, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, assistance.ReservaId, label, oldStatus, newStatus, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

        var assistanceWasResolved = ServiceResolutionRules.IsResolved(assistance);
        assistance.Status = newStatus;
        // ADR-031: si esta operacion RESUELVE la asistencia (no lo estaba antes), exigir nombre +
        // documento + fecha de nacimiento de TODOS los declarados (la prima depende de la edad).
        await EnsureNominalCoverageBeforeResolvingAsync(
            assistance.ReservaId, assistanceWasResolved, ServiceResolutionRules.IsResolved(assistance),
            PassengerNominalRules.ServiceKind.Assistance, AssignmentServiceType.Assistance, assistance.Id, ct);
        StampServiceCancellation(
            wasCancelled: WorkflowStatusHelper.MapGenericStatus(oldStatus ?? string.Empty) == WorkflowStatuses.Cancelado,
            isCancelled: WorkflowStatusHelper.MapGenericStatus(newStatus) == WorkflowStatuses.Cancelado,
            setCancelledAt: value => assistance.CancelledAt = value,
            setCancelledBy: (id, name) => { assistance.CancelledByUserId = id; assistance.CancelledByUserName = name; });
        if (confirmationNumber != null)
            assistance.ConfirmationNumber = string.IsNullOrWhiteSpace(confirmationNumber) ? null : confirmationNumber.Trim();
        await _assistanceRepo.UpdateAsync(assistance, ct);
        if (assistance.SupplierId > 0) await _supplierService.UpdateBalanceAsync(assistance.SupplierId, ct);
        // ADR-020: recalcular saldo (ConfirmedSale) + disparar el motor de estados.
        await _reservaService.UpdateBalanceAsync(assistance.ReservaId);
        var dto = _mapper.Map<AssistanceBookingDto>(assistance);
        dto.ProductCreatedInSale = await ResolveProductCreatedInSaleAsync(assistance.RateId, ct);
        // Enmascarar NetCost igual que el resto de los endpoints de booking.
        await CostMasking.MaskAssistanceAsync(dto, _httpContextAccessor, _permissionResolver, ct);
        return dto;
    }
}
