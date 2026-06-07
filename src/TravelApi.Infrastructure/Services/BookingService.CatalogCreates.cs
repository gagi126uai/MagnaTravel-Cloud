using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Services.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-017 F1.3: los 5 <c>Create*WithCatalogAsync</c> (path con flag ON) + la accion "Confirmar costo".
/// Todo corre dentro de la transaccion atomica de <see cref="RunCatalogTransactionAsync{T}"/>.
/// </summary>
public partial class BookingService
{
    /// <summary>
    /// Resuelve los costos de una venta segun el permiso del caller (regla "request manda" + excepcion D7).
    ///  - CON ver-costos: el request manda (net/tax/commission tal cual), sin marca.
    ///  - SIN ver-costos: producto NUEVO -> 0 + "sin costo conocido" (nota 4); producto EXISTENTE -> cadena
    ///    D7 (RateSupplierSale -> Rate -> 0) con marca si es dudoso; sin producto -> request (0) sin marca.
    /// La ganancia se recalcula canonica (SalePrice - Net - Tax) cuando el caller no ve costos.
    /// </summary>
    private async Task<(decimal Net, decimal Tax, decimal Commission, bool ToConfirm, string? Reason)>
        ResolveCatalogCostsAsync(
            bool canSeeCost, Rate? rate, bool isNewProduct, int supplierId, string currency, int divisor,
            int staleDays, decimal requestNet, decimal requestTax, decimal requestCommission, decimal salePrice,
            CancellationToken ct)
    {
        if (canSeeCost)
        {
            // Request manda: el vendedor VIO el costo y lo pudo editar. Sin marca, sin cadena.
            // Decision del dueño 1 (2026-06-05): este es el unico punto del alta donde el costo entra
            // desde el request (en el path enmascarado el server lo resuelve), asi que aca se rechaza
            // un costo negativo (no existe una compra a valor negativo; 0 si vale).
            EnsureNonNegativeCost(requestNet, requestTax);
            return (requestNet, requestTax, requestCommission, false, null);
        }

        MaskedCostResolution resolution;
        if (isNewProduct)
        {
            // Producto nuevo creado por un caller sin permiso: no hay de donde reponer -> 0 + a confirmar.
            // El Rate nace con costo 0 y QUEDA asi (no se pisa en la confirmacion).
            resolution = new MaskedCostResolution(0m, 0m, true, "NoKnownCost");
        }
        else if (rate != null)
        {
            resolution = await ResolveMaskedCostChainAsync(
                rate.Id, rate, supplierId, currency, divisor, staleDays, ct);
        }
        else
        {
            // Sin producto (alta manual con flag ON): no hay cadena ni upsert; se preserva el request (0).
            // GAP CONOCIDO (N3, ADR §2.3.b.3-bis): un caller SIN ver-costos en un alta manual (sin producto
            // y sin RateId) preserva NetCost=0 del request SIN marca "a confirmar" -> queda un costo perdido
            // sin rastro. Es el comportamiento legacy sancionado por el ADR; NO se "arregla" aca (cambiarlo
            // alteraria el alta manual de siempre). Si en el futuro molesta, marcar tambien estos como dudosos.
            resolution = new MaskedCostResolution(requestNet, requestTax, false, null);
        }

        var commission = salePrice - resolution.Net - resolution.Tax;
        return (resolution.Net, resolution.Tax, commission, resolution.ToConfirm, resolution.Reason);
    }

    /// <summary>
    /// Decision del dueño 1 (2026-06-05): un costo (neto o impuesto) NEGATIVO no tiene sentido de negocio
    /// (no existe una compra a valor negativo) y descuadraria la deuda al operador. El 0 SI es valido
    /// (D8c: "confirmar 0 vale"). Se valida en los dos puntos donde un costo entra desde afuera: el alta
    /// con caller que ve costos (request manda) y el boton "Confirmar costo". Lanza
    /// <see cref="ArgumentException"/> (que el controller traduce a 400 con mensaje humano).
    /// </summary>
    private static void EnsureNonNegativeCost(decimal netCost, decimal tax)
    {
        if (netCost < 0m) throw new ArgumentException("El costo no puede ser menor a cero.");
        if (tax < 0m) throw new ArgumentException("El impuesto no puede ser menor a cero.");
    }

    /// <summary>
    /// ADR-018 (§4-bis): resuelve el nombre de producto que se guarda como identidad visible del
    /// servicio (FlightSegment/TransferBooking.ProductName). Fuente UNICA = lo que el vendedor vio:
    /// el texto explicito del request manda; si no vino (path producto NUEVO sin ProductName), cae al
    /// nombre del producto de catalogo que se esta creando. NUNCA se re-deriva del Rate despues
    /// (eso romperia el snapshot de ADR-017 §6). Devuelve null si no hay ninguno (carga manual).
    /// </summary>
    private static string? ResolveCatalogProductName(string? requestProductName, NewCatalogProductRequest? newProduct)
        => !string.IsNullOrWhiteSpace(requestProductName) ? requestProductName.Trim() : newProduct?.Name;

    // ============================================================ HOTEL ============================================================

    private async Task<HotelBookingDto> CreateHotelWithCatalogAsync(int reservaId, CreateHotelRequest req, CancellationToken ct)
    {
        ValidateHotelStay(req.CheckIn, req.CheckOut);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        ValidateCatalogCreateInputs(req.Currency, req.RateId, req.NewCatalogProduct, isHotel: true);

        var currency = NormalizeCurrency(req.Currency);
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var staleDays = await GetStaleCostReferenceDaysAsync(ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var file = await _fileRepo.GetByIdAsync(reservaId, ct)
                ?? throw new KeyNotFoundException("Reserva no encontrada");

            var isNewProduct = req.NewCatalogProduct != null;
            var supplierId = isNewProduct
                ? await ResolveSupplierIdAsync(req.NewCatalogProduct!.SupplierPublicId, ct)
                : await ResolveSupplierIdAsync(req.SupplierId, ct);

            var existingRate = isNewProduct ? null : await GetRateAsync(req.RateId, ct);

            var hotel = _mapper.Map<HotelBooking>(req);
            hotel.ReservaId = reservaId;
            hotel.SupplierId = supplierId;
            hotel.Currency = currency;
            // Bug 2026-06-06 (reportado por el dueño con flag ON): la ficha inline manda CheckIn/CheckOut
            // como fecha pelada ("2026-08-12") -> Kind=Unspecified -> Npgsql tira DbUpdateException en el
            // INSERT a timestamptz. Normalizamos a fecha de pared (medianoche Kind=Utc). Ver NormalizeCalendarDate.
            hotel.CheckIn = NormalizeCalendarDate(hotel.CheckIn);
            hotel.CheckOut = NormalizeCalendarDate(hotel.CheckOut);

            var divisor = CatalogUnitization.HotelDivisor(hotel.Nights, hotel.Rooms);
            var (net, tax, commission, toConfirm, reason) = await ResolveCatalogCostsAsync(
                canSeeCost, existingRate, isNewProduct, supplierId, currency, divisor, staleDays,
                req.NetCost, req.Tax, req.Commission, req.SalePrice, ct);
            hotel.NetCost = net; hotel.Tax = tax; hotel.Commission = commission;
            hotel.CostToConfirm = toConfirm; hotel.CostToConfirmReason = reason;

            var unit = CatalogUnitization.ForHotel(net, tax, req.SalePrice, hotel.Nights, hotel.Rooms);

            Rate? rate = existingRate;
            if (isNewProduct)
            {
                rate = await FindOrCreateRateAsync(
                    CatalogServiceTypes.Hotel, req.NewCatalogProduct!.Name, req.NewCatalogProduct.City,
                    supplierId, currency, unit, reservaId, isHotel: true, ct);
            }
            else if (existingRate != null)
            {
                // Request manda + relleno de huecos: solo se completan atributos que el request dejo vacios.
                FillHotelGapsFromRate(hotel, existingRate);
            }

            if (rate != null) hotel.RateId = rate.Id;

            if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
                hotel.Status = "Solicitado";
            var statusBlock = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
                _db, reservaId, $"Hotel {hotel.HotelName ?? "sin nombre"}", hotel.Status, ct);
            if (statusBlock != null) throw new InvalidOperationException(statusBlock);

            await _hotelRepo.AddAsync(hotel, ct);
            if (hotel.SupplierId > 0) await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
            await RecalculateReservationScheduleAsync(reservaId, ct);
            await _reservaService.UpdateBalanceAsync(reservaId);

            await UpsertSaleIfRecordableAsync(rate, supplierId, toConfirm, unit, currency, hotel.CreatedAt, ct);

            var dto = _mapper.Map<HotelBookingDto>(hotel);
            // Pill "creado en esta venta": el rate vinculado ya esta en memoria (recien creado o existente),
            // se usa directo en vez de re-consultar dentro de la transaccion Serializable.
            dto.ProductCreatedInSale = rate?.CreatedInSale ?? false;
            await CostMasking.MaskHotelAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    /// <summary>
    /// Rellena solo los atributos descriptivos del hotel que el request dejo vacios, tomandolos del Rate
    /// (request manda + relleno de huecos, §2.3.b.3).
    ///
    /// <para>ASIMETRIA CONOCIDA (N2): el relleno de huecos SOLO existe para Hotel. El ADR regla 3 dice
    /// "todos los tipos", pero en Aereo/Traslado/Paquete/Asistencia los campos requeridos SIEMPRE llegan en
    /// el request (no hay hueco que rellenar), asi que el riesgo es nulo y no se duplica el relleno. Si algun
    /// tipo empieza a permitir campos opcionales reponibles desde el Rate, agregar su propio Fill*GapsFromRate.</para>
    /// </summary>
    private static void FillHotelGapsFromRate(HotelBooking hotel, Rate rate)
    {
        if (string.IsNullOrWhiteSpace(hotel.HotelName) && !string.IsNullOrWhiteSpace(rate.HotelName))
            hotel.HotelName = rate.HotelName;
        if (string.IsNullOrWhiteSpace(hotel.City) && !string.IsNullOrWhiteSpace(rate.City))
            hotel.City = rate.City;
        if (string.IsNullOrWhiteSpace(hotel.RoomType) && !string.IsNullOrWhiteSpace(rate.RoomType))
            hotel.RoomType = rate.RoomType;
        if (string.IsNullOrWhiteSpace(hotel.MealPlan) && !string.IsNullOrWhiteSpace(rate.MealPlan))
            hotel.MealPlan = rate.MealPlan;
        hotel.StarRating ??= rate.StarRating;
    }

    // ============================================================ FLIGHT ============================================================

    private async Task<FlightSegmentDto> CreateFlightWithCatalogAsync(int reservaId, CreateFlightRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        ValidateCatalogCreateInputs(req.Currency, req.RateId, req.NewCatalogProduct, isHotel: false);

        var currency = NormalizeCurrency(req.Currency);
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var staleDays = await GetStaleCostReferenceDaysAsync(ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var file = await _fileRepo.GetByIdAsync(reservaId, ct)
                ?? throw new KeyNotFoundException("Reserva no encontrada");

            var isNewProduct = req.NewCatalogProduct != null;
            var supplierId = isNewProduct
                ? await ResolveSupplierIdAsync(req.NewCatalogProduct!.SupplierPublicId, ct)
                : await ResolveSupplierIdAsync(req.SupplierId, ct);

            var existingRate = isNewProduct ? null : await GetRateAsync(req.RateId, ct);

            var flight = _mapper.Map<FlightSegment>(req);
            flight.ReservaId = reservaId;
            flight.SupplierId = supplierId;
            flight.Currency = currency;
            flight.DepartureTime = NormalizeAirportWallClock(flight.DepartureTime);
            flight.ArrivalTime = NormalizeAirportWallClock(flight.ArrivalTime);
            // ADR-018 (§4-bis): la identidad visible = el texto que vio el vendedor. Fuente unica = req.ProductName;
            // si no vino (path producto NUEVO), caemos al nombre del producto de catalogo. NUNCA se re-deriva del
            // Rate despues (preserva el snapshot de ADR-017 §6).
            flight.ProductName = ResolveCatalogProductName(req.ProductName, req.NewCatalogProduct);
            // ADR-018 Ronda 7 (2026-06-06): la cabina es OPCIONAL — vacio/null se persiste null
            // ("Sin especificar"). Derogado el coalesce a "Economy" de ADR-018 §2.
            flight.CabinClass = NormalizeOptionalText(flight.CabinClass);

            var divisor = CatalogUnitization.FlightDivisor(flight.PassengerCount ?? 1);
            var (net, tax, commission, toConfirm, reason) = await ResolveCatalogCostsAsync(
                canSeeCost, existingRate, isNewProduct, supplierId, currency, divisor, staleDays,
                req.NetCost, req.Tax, req.Commission, req.SalePrice, ct);
            flight.NetCost = net; flight.Tax = tax; flight.Commission = commission;
            flight.CostToConfirm = toConfirm; flight.CostToConfirmReason = reason;

            var unit = CatalogUnitization.ForFlight(net, tax, req.SalePrice, flight.PassengerCount ?? 1);

            Rate? rate = existingRate;
            if (isNewProduct)
            {
                rate = await FindOrCreateRateAsync(
                    CatalogServiceTypes.Aereo, req.NewCatalogProduct!.Name, req.NewCatalogProduct.City,
                    supplierId, currency, unit, reservaId, isHotel: false, ct);
            }
            if (rate != null) flight.RateId = rate.Id;

            if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
                flight.Status = "Solicitado";
            // ADR-018: identidad visible via ServiceDisplayName (ProductName si la ficha no cargo aerolinea/numero).
            var statusBlock = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
                _db, reservaId, $"Vuelo {ServiceDisplayName.ForFlight(flight.ProductName, flight.AirlineCode, flight.FlightNumber)}", flight.Status, ct);
            if (statusBlock != null) throw new InvalidOperationException(statusBlock);

            await _flightRepo.AddAsync(flight, ct);
            if (flight.SupplierId > 0) await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
            await RecalculateReservationScheduleAsync(reservaId, ct);
            await _reservaService.UpdateBalanceAsync(reservaId);

            await UpsertSaleIfRecordableAsync(rate, supplierId, toConfirm, unit, currency, flight.CreatedAt, ct);

            var dto = _mapper.Map<FlightSegmentDto>(flight);
            // Pill "creado en esta venta": el rate vinculado ya esta en memoria (ver nota en Hotel).
            dto.ProductCreatedInSale = rate?.CreatedInSale ?? false;
            await CostMasking.MaskFlightAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // ============================================================ TRANSFER ============================================================

    private async Task<TransferBookingDto> CreateTransferWithCatalogAsync(int reservaId, CreateTransferRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        ValidateCatalogCreateInputs(req.Currency, req.RateId, req.NewCatalogProduct, isHotel: false);

        var currency = NormalizeCurrency(req.Currency);
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var staleDays = await GetStaleCostReferenceDaysAsync(ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var file = await _fileRepo.GetByIdAsync(reservaId, ct)
                ?? throw new KeyNotFoundException("Reserva no encontrada");

            var isNewProduct = req.NewCatalogProduct != null;
            var supplierId = isNewProduct
                ? await ResolveSupplierIdAsync(req.NewCatalogProduct!.SupplierPublicId, ct)
                : await ResolveSupplierIdAsync(req.SupplierId, ct);

            var existingRate = isNewProduct ? null : await GetRateAsync(req.RateId, ct);

            var transfer = _mapper.Map<TransferBooking>(req);
            transfer.ReservaId = reservaId;
            transfer.SupplierId = supplierId;
            transfer.Currency = currency;
            transfer.PickupDateTime = NormalizeAirportWallClock(transfer.PickupDateTime);
            if (transfer.ReturnDateTime.HasValue)
                transfer.ReturnDateTime = NormalizeAirportWallClock(transfer.ReturnDateTime.Value);
            // ADR-018 (§4-bis): identidad visible = texto del vendedor (ver Flight).
            transfer.ProductName = ResolveCatalogProductName(req.ProductName, req.NewCatalogProduct);
            // ADR-018 Ronda 7 (2026-06-06): el tipo de vehiculo es OPCIONAL — vacio/null se persiste
            // null (no informado). Derogado el coalesce a "Sedan" de ADR-018 §2.
            transfer.VehicleType = NormalizeOptionalText(transfer.VehicleType);

            var divisor = CatalogUnitization.TransferDivisor();
            var (net, tax, commission, toConfirm, reason) = await ResolveCatalogCostsAsync(
                canSeeCost, existingRate, isNewProduct, supplierId, currency, divisor, staleDays,
                req.NetCost, req.Tax, req.Commission, req.SalePrice, ct);
            transfer.NetCost = net; transfer.Tax = tax; transfer.Commission = commission;
            transfer.CostToConfirm = toConfirm; transfer.CostToConfirmReason = reason;

            var unit = CatalogUnitization.ForTransfer(net, tax, req.SalePrice);

            Rate? rate = existingRate;
            if (isNewProduct)
            {
                rate = await FindOrCreateRateAsync(
                    CatalogServiceTypes.Traslado, req.NewCatalogProduct!.Name, req.NewCatalogProduct.City,
                    supplierId, currency, unit, reservaId, isHotel: false, ct);
            }
            if (rate != null) transfer.RateId = rate.Id;

            if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
                transfer.Status = "Solicitado";
            var statusBlock = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
                _db, reservaId, $"Transfer {transfer.VehicleType ?? ""}".Trim(), transfer.Status, ct);
            if (statusBlock != null) throw new InvalidOperationException(statusBlock);

            await _transferRepo.AddAsync(transfer, ct);
            if (transfer.SupplierId > 0) await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
            await RecalculateReservationScheduleAsync(reservaId, ct);
            await _reservaService.UpdateBalanceAsync(reservaId);

            await UpsertSaleIfRecordableAsync(rate, supplierId, toConfirm, unit, currency, transfer.CreatedAt, ct);

            var dto = _mapper.Map<TransferBookingDto>(transfer);
            // Pill "creado en esta venta": el rate vinculado ya esta en memoria (ver nota en Hotel).
            dto.ProductCreatedInSale = rate?.CreatedInSale ?? false;
            await CostMasking.MaskTransferAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // ============================================================ PACKAGE ============================================================

    private async Task<PackageBookingDto> CreatePackageWithCatalogAsync(int reservaId, CreatePackageRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        ValidateCatalogCreateInputs(req.Currency, req.RateId, req.NewCatalogProduct, isHotel: false);

        var currency = NormalizeCurrency(req.Currency);
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var staleDays = await GetStaleCostReferenceDaysAsync(ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var file = await _fileRepo.GetByIdAsync(reservaId, ct)
                ?? throw new KeyNotFoundException("Reserva no encontrada");

            var isNewProduct = req.NewCatalogProduct != null;
            var supplierId = isNewProduct
                ? await ResolveSupplierIdAsync(req.NewCatalogProduct!.SupplierPublicId, ct)
                : await ResolveSupplierIdAsync(req.SupplierId, ct);

            var existingRate = isNewProduct ? null : await GetRateAsync(req.RateId, ct);

            var package = _mapper.Map<PackageBooking>(req);
            package.ReservaId = reservaId;
            package.SupplierId = supplierId;
            package.Currency = currency;
            // Bug 2026-06-06: la ficha inline manda StartDate/EndDate como fecha pelada (Kind=Unspecified)
            // y Npgsql las rechaza en timestamptz. Normalizamos a fecha de pared. Ver NormalizeCalendarDate.
            package.StartDate = NormalizeCalendarDate(package.StartDate);
            package.EndDate = NormalizeCalendarDate(package.EndDate);

            var divisor = CatalogUnitization.PackageDivisor(package.Adults, package.Children);
            var (net, tax, commission, toConfirm, reason) = await ResolveCatalogCostsAsync(
                canSeeCost, existingRate, isNewProduct, supplierId, currency, divisor, staleDays,
                req.NetCost, req.Tax, req.Commission, req.SalePrice, ct);
            package.NetCost = net; package.Tax = tax; package.Commission = commission;
            package.CostToConfirm = toConfirm; package.CostToConfirmReason = reason;

            var unit = CatalogUnitization.ForPackage(net, tax, req.SalePrice, package.Adults, package.Children);

            Rate? rate = existingRate;
            if (isNewProduct)
            {
                rate = await FindOrCreateRateAsync(
                    CatalogServiceTypes.Paquete, req.NewCatalogProduct!.Name, req.NewCatalogProduct.City,
                    supplierId, currency, unit, reservaId, isHotel: false, ct);
            }
            if (rate != null) package.RateId = rate.Id;

            if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
                package.Status = "Solicitado";
            var statusBlock = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
                _db, reservaId, $"Paquete {package.PackageName ?? "sin nombre"}", package.Status, ct);
            if (statusBlock != null) throw new InvalidOperationException(statusBlock);

            await _packageRepo.AddAsync(package, ct);
            if (package.SupplierId > 0) await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
            await RecalculateReservationScheduleAsync(reservaId, ct);
            await _reservaService.UpdateBalanceAsync(reservaId);

            await UpsertSaleIfRecordableAsync(rate, supplierId, toConfirm, unit, currency, package.CreatedAt, ct);

            var dto = _mapper.Map<PackageBookingDto>(package);
            // Pill "creado en esta venta": el rate vinculado ya esta en memoria (ver nota en Hotel).
            dto.ProductCreatedInSale = rate?.CreatedInSale ?? false;
            await CostMasking.MaskPackageAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    // ============================================================ ASSISTANCE ============================================================

    private async Task<AssistanceBookingDto> CreateAssistanceWithCatalogAsync(int reservaId, CreateAssistanceRequest req, CancellationToken ct)
    {
        ValidateAssistanceValidity(req.ValidFrom, req.ValidTo);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        ValidateCatalogCreateInputs(req.Currency, req.RateId, req.NewCatalogProduct, isHotel: false);

        var currency = NormalizeCurrency(req.Currency);
        var canSeeCost = await CostMasking.CanSeeCostAsync(_httpContextAccessor, _permissionResolver, ct);
        var staleDays = await GetStaleCostReferenceDaysAsync(ct);

        return await RunCatalogTransactionAsync(async () =>
        {
            var file = await _fileRepo.GetByIdAsync(reservaId, ct)
                ?? throw new KeyNotFoundException("Reserva no encontrada");

            var isNewProduct = req.NewCatalogProduct != null;
            var supplierId = isNewProduct
                ? await ResolveSupplierIdAsync(req.NewCatalogProduct!.SupplierPublicId, ct)
                : await ResolveSupplierIdAsync(req.SupplierId, ct);

            var existingRate = isNewProduct ? null : await GetRateAsync(req.RateId, ct);

            var assistance = _mapper.Map<AssistanceBooking>(req);
            assistance.ReservaId = reservaId;
            assistance.SupplierId = supplierId;
            assistance.Currency = currency;
            // Bug 2026-06-06: la ficha inline manda ValidFrom/ValidTo como fecha pelada (Kind=Unspecified)
            // y Npgsql las rechaza en timestamptz. Normalizamos a fecha de pared ANTES de calcular los dias
            // de vigencia. Ver NormalizeCalendarDate.
            assistance.ValidFrom = NormalizeCalendarDate(assistance.ValidFrom);
            assistance.ValidTo = NormalizeCalendarDate(assistance.ValidTo);

            var days = CatalogUnitization.AssistanceDays(assistance.ValidFrom, assistance.ValidTo);
            var divisor = CatalogUnitization.AssistanceDivisor(assistance.Adults, assistance.Children, days);
            var (net, tax, commission, toConfirm, reason) = await ResolveCatalogCostsAsync(
                canSeeCost, existingRate, isNewProduct, supplierId, currency, divisor, staleDays,
                req.NetCost, req.Tax, req.Commission, req.SalePrice, ct);
            assistance.NetCost = net; assistance.Tax = tax; assistance.Commission = commission;
            assistance.CostToConfirm = toConfirm; assistance.CostToConfirmReason = reason;

            var unit = CatalogUnitization.ForAssistance(net, tax, req.SalePrice, assistance.Adults, assistance.Children, days);

            Rate? rate = existingRate;
            if (isNewProduct)
            {
                rate = await FindOrCreateRateAsync(
                    CatalogServiceTypes.Asistencia, req.NewCatalogProduct!.Name, req.NewCatalogProduct.City,
                    supplierId, currency, unit, reservaId, isHotel: false, ct);
            }
            if (rate != null) assistance.RateId = rate.Id;

            if (await ReservaCapacityRules.ShouldForceSolicitadoStatusAsync(_db, reservaId, ct))
                assistance.Status = "Solicitado";
            var statusBlock = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
                _db, reservaId, $"Asistencia {assistance.PlanType ?? "seguro"}", assistance.Status, ct);
            if (statusBlock != null) throw new InvalidOperationException(statusBlock);

            await _assistanceRepo.AddAsync(assistance, ct);
            if (assistance.SupplierId > 0) await _supplierService.UpdateBalanceAsync(assistance.SupplierId, ct);
            await RecalculateReservationScheduleAsync(reservaId, ct);
            await _reservaService.UpdateBalanceAsync(reservaId);

            await UpsertSaleIfRecordableAsync(rate, supplierId, toConfirm, unit, currency, assistance.CreatedAt, ct);

            var dto = _mapper.Map<AssistanceBookingDto>(assistance);
            // Pill "creado en esta venta": el rate vinculado ya esta en memoria (ver nota en Hotel).
            dto.ProductCreatedInSale = rate?.CreatedInSale ?? false;
            await CostMasking.MaskAssistanceAsync(dto, _httpContextAccessor, _permissionResolver, ct);
            return dto;
        }, ct);
    }

    /// <summary>
    /// Upsertea la venta SOLO si hay producto, hay operador real y el servicio NO quedo "a confirmar".
    /// Un servicio marcado difiere el upsert hasta que alguien con permiso confirme el costo (§2.8) — asi
    /// no se envenena <c>LastNetCost</c> con ceros para todos.
    /// </summary>
    private async Task UpsertSaleIfRecordableAsync(
        Rate? rate, int supplierId, bool costToConfirm, CatalogUnitization.Unitized unit, string currency,
        DateTime soldAt, CancellationToken ct)
    {
        if (rate == null || supplierId <= 0 || costToConfirm) return;
        await UpsertRateSupplierSaleAsync(rate.Id, supplierId, unit, currency, soldAt, ct);
    }
}
