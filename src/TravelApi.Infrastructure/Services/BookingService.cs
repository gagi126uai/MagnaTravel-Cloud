using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Interfaces;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class BookingService : IBookingService
{
    private readonly IRepository<FlightSegment> _flightRepo;
    private readonly IRepository<HotelBooking> _hotelRepo;
    private readonly IRepository<PackageBooking> _packageRepo;
    private readonly IRepository<TransferBooking> _transferRepo;
    private readonly IRepository<Reserva> _fileRepo;
    private readonly IRepository<Supplier> _supplierRepo;
    private readonly IReservaService _reservaService;
    private readonly ISupplierService _supplierService;
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public BookingService(
        IRepository<FlightSegment> flightRepo,
        IRepository<HotelBooking> hotelRepo,
        IRepository<PackageBooking> packageRepo,
        IRepository<TransferBooking> transferRepo,
        IRepository<Reserva> fileRepo,
        IRepository<Supplier> supplierRepo,
        IReservaService reservaService,
        ISupplierService supplierService,
        AppDbContext db,
        IMapper mapper)
    {
        _flightRepo = flightRepo;
        _hotelRepo = hotelRepo;
        _packageRepo = packageRepo;
        _transferRepo = transferRepo;
        _fileRepo = fileRepo;
        _supplierRepo = supplierRepo;
        _reservaService = reservaService;
        _supplierService = supplierService;
        _db = db;
        _mapper = mapper;
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

    private static void ValidateHotelStay(DateTime checkIn, DateTime checkOut)
    {
        if (checkOut <= checkIn)
        {
            throw new ArgumentException("El check-out debe ser posterior al check-in.");
        }
    }

    private static void ApplyHotelRateSnapshot(HotelBooking hotel, Rate rate)
    {
        hotel.RateId = rate.Id;

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
        if (reserva == null)
        {
            return;
        }

        var startDates = new List<DateTime>();
        var endDates = new List<DateTime>();

        startDates.AddRange(await _db.FlightSegments
            .Where(f => f.ReservaId == reservaId)
            .Select(f => f.DepartureTime)
            .ToListAsync(ct));

        endDates.AddRange(await _db.FlightSegments
            .Where(f => f.ReservaId == reservaId)
            .Select(f => f.ArrivalTime)
            .ToListAsync(ct));

        startDates.AddRange(await _db.HotelBookings
            .Where(h => h.ReservaId == reservaId)
            .Select(h => h.CheckIn)
            .ToListAsync(ct));

        endDates.AddRange(await _db.HotelBookings
            .Where(h => h.ReservaId == reservaId)
            .Select(h => h.CheckOut)
            .ToListAsync(ct));

        startDates.AddRange(await _db.TransferBookings
            .Where(t => t.ReservaId == reservaId)
            .Select(t => t.PickupDateTime)
            .ToListAsync(ct));

        endDates.AddRange(await _db.TransferBookings
            .Where(t => t.ReservaId == reservaId)
            .Select(t => t.ReturnDateTime ?? t.PickupDateTime)
            .ToListAsync(ct));

        startDates.AddRange(await _db.PackageBookings
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.StartDate)
            .ToListAsync(ct));

        endDates.AddRange(await _db.PackageBookings
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.EndDate)
            .ToListAsync(ct));

        startDates.AddRange(await _db.Servicios
            .Where(s => s.ReservaId == reservaId)
            .Select(s => s.DepartureDate)
            .ToListAsync(ct));

        endDates.AddRange(await _db.Servicios
            .Where(s => s.ReservaId == reservaId)
            .Select(s => s.ReturnDate ?? s.DepartureDate)
            .ToListAsync(ct));

        DateTime? nextStartDate = startDates.Count > 0 ? startDates.Min() : null;
        DateTime? nextEndDate = endDates.Count > 0 ? endDates.Max() : null;

        if (reserva.StartDate != nextStartDate || reserva.EndDate != nextEndDate)
        {
            reserva.StartDate = nextStartDate;
            reserva.EndDate = nextEndDate;
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
        return await _flightRepo.Query()
            .Where(f => f.ReservaId == reservaId)
            .Include(f => f.Rate)
            .OrderBy(f => f.DepartureTime)
            .ProjectTo<FlightSegmentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<FlightSegmentDto> CreateFlightAsync(string reservaPublicIdOrLegacyId, CreateFlightRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await CreateFlightAsync(reservaId, req, ct);
    }

    public async Task<FlightSegmentDto> CreateFlightAsync(int reservaId, CreateFlightRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        var flight = _mapper.Map<FlightSegment>(req);
        flight.ReservaId = reservaId;
        flight.SupplierId = supplierId;

        // Snapshot desde tarifario: si viene RateId, congelamos precios del tarifario
        var rate = await GetRateAsync(req.RateId, ct);
        if (rate != null)
        {
            flight.RateId = rate.Id;
            flight.NetCost = rate.NetCost;
            flight.SalePrice = rate.SalePrice;
            flight.Commission = rate.Commission;
            flight.Tax = rate.Tax;
        }

        // Guard: si la reserva esta en Operativo/Closed, el servicio nuevo debe estar confirmado
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Vuelo {flight.AirlineCode}{flight.FlightNumber}", flight.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        await _flightRepo.AddAsync(flight, ct);

        if (flight.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<FlightSegmentDto>(flight);
    }

    public async Task<FlightSegmentDto> UpdateFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateFlightRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var flightId = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);
        return await UpdateFlightAsync(reservaId, flightId, req, ct);
    }

    public async Task<FlightSegmentDto> UpdateFlightAsync(int reservaId, int id, UpdateFlightRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        var oldSupplierId = flight.SupplierId;
        var oldStatus = flight.Status;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, flight);
        flight.SupplierId = supplierId;

        // Si viene un RateId nuevo, actualizar snapshot
        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            flight.RateId = rateId.Value;

        var label = $"Vuelo {flight.AirlineCode}{flight.FlightNumber}";
        // Guard 1: en reserva Operativo/Closed el servicio debe quedar confirmado
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, flight.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        // Guard 2: no degradar de confirmado a no-confirmado si hay pagos al proveedor
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, flight.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

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
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<FlightSegmentDto>(flight);
    }

    public async Task DeleteFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var flightId = await ResolveRequiredIdAsync<FlightSegment>(publicIdOrLegacyId, ct);
        await DeleteFlightAsync(reservaId, flightId, ct);
    }

    public async Task DeleteFlightAsync(int reservaId, int id, CancellationToken ct)
    {
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

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
        return await _hotelRepo.Query()
            .Where(h => h.ReservaId == reservaId)
            .Include(h => h.Rate)
            .OrderBy(h => h.CheckIn)
            .ProjectTo<HotelBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
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
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task<HotelBookingDto> CreateHotelAsync(string reservaPublicIdOrLegacyId, CreateHotelRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await CreateHotelAsync(reservaId, req, ct);
    }

    public async Task<HotelBookingDto> CreateHotelAsync(int reservaId, CreateHotelRequest req, CancellationToken ct)
    {
        ValidateHotelStay(req.CheckIn, req.CheckOut);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var rate = await GetRateAsync(req.RateId, ct);
        var supplierId = rate?.SupplierId ?? await ResolveSupplierIdAsync(req.SupplierId, ct);

        var hotel = _mapper.Map<HotelBooking>(req);
        hotel.ReservaId = reservaId;
        hotel.SupplierId = supplierId;

        if (rate != null)
        {
            ApplyHotelRateSnapshot(hotel, rate);
        }

        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Hotel {hotel.HotelName ?? "sin nombre"}", hotel.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        await _hotelRepo.AddAsync(hotel, ct);

        if (hotel.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task<HotelBookingDto> UpdateHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateHotelRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var hotelId = await ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct);
        return await UpdateHotelAsync(reservaId, hotelId, req, ct);
    }

    public async Task<HotelBookingDto> UpdateHotelAsync(int reservaId, int id, UpdateHotelRequest req, CancellationToken ct)
    {
        ValidateHotelStay(req.CheckIn, req.CheckOut);
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

        var oldSupplierId = hotel.SupplierId;
        var oldStatus = hotel.Status;
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

        var label = $"Hotel {hotel.HotelName ?? "sin nombre"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, hotel.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, hotel.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

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
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task DeleteHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var hotelId = await ResolveRequiredIdAsync<HotelBooking>(publicIdOrLegacyId, ct);
        await DeleteHotelAsync(reservaId, hotelId, ct);
    }

    public async Task DeleteHotelAsync(int reservaId, int id, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

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
        return await _packageRepo.Query()
            .Where(p => p.ReservaId == reservaId)
            .Include(p => p.Rate)
            .OrderBy(p => p.CreatedAt)
            .ProjectTo<PackageBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<PackageBookingDto> CreatePackageAsync(string reservaPublicIdOrLegacyId, CreatePackageRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await CreatePackageAsync(reservaId, req, ct);
    }

    public async Task<PackageBookingDto> CreatePackageAsync(int reservaId, CreatePackageRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        var package = _mapper.Map<PackageBooking>(req);
        package.ReservaId = reservaId;
        package.SupplierId = supplierId;

        // Snapshot desde tarifario
        var rate = await GetRateAsync(req.RateId, ct);
        if (rate != null)
        {
            package.RateId = rate.Id;
            package.NetCost = rate.NetCost;
            package.SalePrice = rate.SalePrice;
            package.Commission = rate.Commission;
        }

        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Paquete {package.PackageName ?? "sin nombre"}", package.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        await _packageRepo.AddAsync(package, ct);

        if (package.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<PackageBookingDto>(package);
    }

    public async Task<PackageBookingDto> UpdatePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdatePackageRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var packageId = await ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct);
        return await UpdatePackageAsync(reservaId, packageId, req, ct);
    }

    public async Task<PackageBookingDto> UpdatePackageAsync(int reservaId, int id, UpdatePackageRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

        var oldNetCost = package.NetCost;
        var oldSupplierId = package.SupplierId;
        var oldStatus = package.Status;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, package);
        package.SupplierId = supplierId;

        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            package.RateId = rateId.Value;

        var label = $"Paquete {package.PackageName ?? "sin nombre"}";
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, package.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, package.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

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
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<PackageBookingDto>(package);
    }

    public async Task DeletePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var packageId = await ResolveRequiredIdAsync<PackageBooking>(publicIdOrLegacyId, ct);
        await DeletePackageAsync(reservaId, packageId, ct);
    }

    public async Task DeletePackageAsync(int reservaId, int id, CancellationToken ct)
    {
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

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
        return await _transferRepo.Query()
            .Where(t => t.ReservaId == reservaId)
            .Include(t => t.Rate)
            .OrderBy(t => t.PickupDateTime)
            .ProjectTo<TransferBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<TransferBookingDto> CreateTransferAsync(string reservaPublicIdOrLegacyId, CreateTransferRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await CreateTransferAsync(reservaId, req, ct);
    }

    public async Task<TransferBookingDto> CreateTransferAsync(int reservaId, CreateTransferRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        var transfer = _mapper.Map<TransferBooking>(req);
        transfer.ReservaId = reservaId;
        transfer.SupplierId = supplierId;

        // Snapshot desde tarifario
        var rate = await GetRateAsync(req.RateId, ct);
        if (rate != null)
        {
            transfer.RateId = rate.Id;
            transfer.NetCost = rate.NetCost;
            transfer.SalePrice = rate.SalePrice;
            transfer.Commission = rate.Commission;
        }

        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(
            _db, reservaId, $"Transfer {transfer.VehicleType ?? ""}".Trim(), transfer.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);

        await _transferRepo.AddAsync(transfer, ct);

        if (transfer.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<TransferBookingDto>(transfer);
    }

    public async Task<TransferBookingDto> UpdateTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateTransferRequest req, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var transferId = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);
        return await UpdateTransferAsync(reservaId, transferId, req, ct);
    }

    public async Task<TransferBookingDto> UpdateTransferAsync(int reservaId, int id, UpdateTransferRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        var oldNetCost = transfer.NetCost;
        var oldSupplierId = transfer.SupplierId;
        var oldStatus = transfer.Status;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, transfer);
        transfer.SupplierId = supplierId;

        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            transfer.RateId = rateId.Value;

        var label = $"Transfer {transfer.VehicleType ?? ""}".Trim();
        var statusBlockReason = await ReservaCapacityRules.GetServiceStatusBlockReasonAsync(_db, reservaId, label, transfer.Status, ct);
        if (statusBlockReason != null) throw new InvalidOperationException(statusBlockReason);
        var downgradeReason = await ReservaCapacityRules.GetStatusDowngradeBlockReasonAsync(_db, reservaId, label, oldStatus, transfer.Status, ct);
        if (downgradeReason != null) throw new InvalidOperationException(downgradeReason);

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
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<TransferBookingDto>(transfer);
    }

    public async Task DeleteTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var transferId = await ResolveRequiredIdAsync<TransferBooking>(publicIdOrLegacyId, ct);
        await DeleteTransferAsync(reservaId, transferId, ct);
    }

    public async Task DeleteTransferAsync(int reservaId, int id, CancellationToken ct)
    {
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

        await _transferRepo.DeleteAsync(transfer, ct);
        if (transfer.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await RecalculateReservationScheduleAsync(reservaId, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    private async Task EnsureNoPaymentsAsync(int reservaId, CancellationToken ct)
    {
        var hasPayments = await _db.Payments.AnyAsync(p => p.ReservaId == reservaId && !p.IsDeleted, ct);
        if (hasPayments)
            throw new InvalidOperationException("No se pueden eliminar servicios de una reserva con pagos realizados.");

        var hasIssuedVoucher = await _db.Vouchers.AnyAsync(v => v.ReservaId == reservaId && v.Status == "Issued", ct);
        if (hasIssuedVoucher)
            throw new InvalidOperationException("No se pueden eliminar servicios de una reserva con vouchers ya emitidos. Anula los vouchers primero.");
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
}
