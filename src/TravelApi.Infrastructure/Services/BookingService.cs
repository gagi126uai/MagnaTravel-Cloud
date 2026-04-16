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

    #region Flights

    public async Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(int reservaId, CancellationToken ct)
    {
        return await _flightRepo.Query()
            .Where(f => f.ReservaId == reservaId)
            .Include(f => f.Rate)
            .OrderBy(f => f.DepartureTime)
            .ProjectTo<FlightSegmentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
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

        await _flightRepo.AddAsync(flight, ct);

        if (flight.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }

        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<FlightSegmentDto>(flight);
    }

    public async Task<FlightSegmentDto> UpdateFlightAsync(int reservaId, int id, UpdateFlightRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        var oldNetCost = flight.NetCost;
        var oldSupplierId = flight.SupplierId;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, flight);
        flight.SupplierId = supplierId;

        // Si viene un RateId nuevo, actualizar snapshot
        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            flight.RateId = rateId.Value;

        if (oldSupplierId > 0 && oldSupplierId == flight.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }
        else if (oldSupplierId != flight.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (flight.SupplierId > 0) await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }

        await _flightRepo.UpdateAsync(flight, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<FlightSegmentDto>(flight);
    }

    public async Task DeleteFlightAsync(int reservaId, int id, CancellationToken ct)
    {
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.ReservaId != reservaId) throw new KeyNotFoundException("Vuelo no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

        if (flight.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(flight.SupplierId, ct);
        }

        await _flightRepo.DeleteAsync(flight, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    #endregion

    #region Hotels

    public async Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(int reservaId, CancellationToken ct)
    {
        return await _hotelRepo.Query()
            .Where(h => h.ReservaId == reservaId)
            .Include(h => h.Rate)
            .OrderBy(h => h.CheckIn)
            .ProjectTo<HotelBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<HotelBookingDto> GetHotelByIdAsync(int reservaId, int id, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task<HotelBookingDto> CreateHotelAsync(int reservaId, CreateHotelRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var file = await _fileRepo.GetByIdAsync(reservaId, ct);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        var hotel = _mapper.Map<HotelBooking>(req);
        hotel.ReservaId = reservaId;
        hotel.SupplierId = supplierId;

        // Snapshot desde tarifario
        var rate = await GetRateAsync(req.RateId, ct);
        if (rate != null)
        {
            hotel.RateId = rate.Id;
            hotel.NetCost = rate.NetCost;
            hotel.SalePrice = rate.SalePrice;
            hotel.Commission = rate.Commission;
        }

        await _hotelRepo.AddAsync(hotel, ct);

        if (hotel.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }

        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task<HotelBookingDto> UpdateHotelAsync(int reservaId, int id, UpdateHotelRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

        var oldNetCost = hotel.NetCost;
        var oldSupplierId = hotel.SupplierId;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, hotel);
        hotel.SupplierId = supplierId;

        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            hotel.RateId = rateId.Value;

        if (oldSupplierId > 0 && oldSupplierId == hotel.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }
        else if (oldSupplierId != hotel.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (hotel.SupplierId > 0) await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }

        await _hotelRepo.UpdateAsync(hotel, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task DeleteHotelAsync(int reservaId, int id, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.ReservaId != reservaId) throw new KeyNotFoundException("Hotel no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

        if (hotel.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(hotel.SupplierId, ct);
        }

        await _hotelRepo.DeleteAsync(hotel, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    #endregion

    #region Packages

    public async Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(int reservaId, CancellationToken ct)
    {
        return await _packageRepo.Query()
            .Where(p => p.ReservaId == reservaId)
            .Include(p => p.Rate)
            .OrderBy(p => p.CreatedAt)
            .ProjectTo<PackageBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
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

        await _packageRepo.AddAsync(package, ct);

        if (package.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }

        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<PackageBookingDto>(package);
    }

    public async Task<PackageBookingDto> UpdatePackageAsync(int reservaId, int id, UpdatePackageRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

        var oldNetCost = package.NetCost;
        var oldSupplierId = package.SupplierId;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, package);
        package.SupplierId = supplierId;

        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            package.RateId = rateId.Value;

        if (oldSupplierId > 0 && oldSupplierId == package.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }
        else if (oldSupplierId != package.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (package.SupplierId > 0) await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }

        await _packageRepo.UpdateAsync(package, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<PackageBookingDto>(package);
    }

    public async Task DeletePackageAsync(int reservaId, int id, CancellationToken ct)
    {
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.ReservaId != reservaId) throw new KeyNotFoundException("Paquete no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

        if (package.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(package.SupplierId, ct);
        }

        await _packageRepo.DeleteAsync(package, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    #endregion

    #region Transfers

    public async Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(int reservaId, CancellationToken ct)
    {
        return await _transferRepo.Query()
            .Where(t => t.ReservaId == reservaId)
            .Include(t => t.Rate)
            .OrderBy(t => t.PickupDateTime)
            .ProjectTo<TransferBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
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

        await _transferRepo.AddAsync(transfer, ct);

        if (transfer.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<TransferBookingDto>(transfer);
    }

    public async Task<TransferBookingDto> UpdateTransferAsync(int reservaId, int id, UpdateTransferRequest req, CancellationToken ct)
    {
        if (req.SalePrice <= 0) throw new ArgumentException("El valor de venta debe ser mayor a 0.");
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        var oldNetCost = transfer.NetCost;
        var oldSupplierId = transfer.SupplierId;
        var supplierId = await ResolveSupplierIdAsync(req.SupplierId, ct);

        _mapper.Map(req, transfer);
        transfer.SupplierId = supplierId;

        var rateId = await ResolveRateIdAsync(req.RateId, ct);
        if (rateId.HasValue)
            transfer.RateId = rateId.Value;

        if (oldSupplierId > 0 && oldSupplierId == transfer.SupplierId)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }
        else if (oldSupplierId != transfer.SupplierId)
        {
            if (oldSupplierId > 0) await _supplierService.UpdateBalanceAsync(oldSupplierId, ct);
            if (transfer.SupplierId > 0) await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await _transferRepo.UpdateAsync(transfer, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
        return _mapper.Map<TransferBookingDto>(transfer);
    }

    public async Task DeleteTransferAsync(int reservaId, int id, CancellationToken ct)
    {
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.ReservaId != reservaId) throw new KeyNotFoundException("Traslado no encontrado");

        await EnsureNoPaymentsAsync(reservaId, ct);

        if (transfer.SupplierId > 0)
        {
            await _supplierService.UpdateBalanceAsync(transfer.SupplierId, ct);
        }

        await _transferRepo.DeleteAsync(transfer, ct);
        await _reservaService.UpdateBalanceAsync(reservaId);
    }

    private async Task EnsureNoPaymentsAsync(int reservaId, CancellationToken ct)
    {
        var hasPayments = await _db.Payments.AnyAsync(p => p.ReservaId == reservaId && !p.IsDeleted, ct);
        if (hasPayments)
            throw new InvalidOperationException("No se pueden eliminar servicios de una reserva con pagos realizados.");
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
