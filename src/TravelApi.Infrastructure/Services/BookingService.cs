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
    private readonly IRepository<TravelFile> _fileRepo;
    private readonly IRepository<Supplier> _supplierRepo;
    private readonly IMapper _mapper;

    public BookingService(
        IRepository<FlightSegment> flightRepo,
        IRepository<HotelBooking> hotelRepo,
        IRepository<PackageBooking> packageRepo,
        IRepository<TransferBooking> transferRepo,
        IRepository<TravelFile> fileRepo,
        IRepository<Supplier> supplierRepo,
        IMapper mapper)
    {
        _flightRepo = flightRepo;
        _hotelRepo = hotelRepo;
        _packageRepo = packageRepo;
        _transferRepo = transferRepo;
        _fileRepo = fileRepo;
        _supplierRepo = supplierRepo;
        _mapper = mapper;
    }

    #region Flights

    public async Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(int fileId, CancellationToken ct)
    {
        return await _flightRepo.Query()
            .Where(f => f.TravelFileId == fileId)
            .OrderBy(f => f.DepartureTime)
            .ProjectTo<FlightSegmentDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<FlightSegmentDto> CreateFlightAsync(int fileId, CreateFlightRequest req, CancellationToken ct)
    {
        var file = await _fileRepo.GetByIdAsync(fileId, ct);
        if (file == null) throw new KeyNotFoundException("File no encontrado");

        var flight = _mapper.Map<FlightSegment>(req);
        flight.TravelFileId = fileId;

        await _flightRepo.AddAsync(flight, ct);

        if (flight.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(flight.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += flight.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        return _mapper.Map<FlightSegmentDto>(flight);
    }

    public async Task<FlightSegmentDto> UpdateFlightAsync(int fileId, int id, UpdateFlightRequest req, CancellationToken ct)
    {
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.TravelFileId != fileId) throw new KeyNotFoundException("Vuelo no encontrado");

        var oldNetCost = flight.NetCost;
        var oldSupplierId = flight.SupplierId;

        _mapper.Map(req, flight);

        if (oldSupplierId > 0 && oldSupplierId == flight.SupplierId)
        {
            var supplier = await _supplierRepo.GetByIdAsync(flight.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += (flight.NetCost - oldNetCost);
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }
        else if (oldSupplierId != flight.SupplierId)
        {
            if (oldSupplierId > 0)
            {
                var oldSupplier = await _supplierRepo.GetByIdAsync(oldSupplierId, ct);
                if (oldSupplier != null)
                {
                    oldSupplier.CurrentBalance -= oldNetCost;
                    await _supplierRepo.UpdateAsync(oldSupplier, ct);
                }
            }
            if (flight.SupplierId > 0)
            {
                var newSupplier = await _supplierRepo.GetByIdAsync(flight.SupplierId, ct);
                if (newSupplier != null)
                {
                    newSupplier.CurrentBalance += flight.NetCost;
                    await _supplierRepo.UpdateAsync(newSupplier, ct);
                }
            }
        }

        await _flightRepo.UpdateAsync(flight, ct);
        return _mapper.Map<FlightSegmentDto>(flight);
    }

    public async Task DeleteFlightAsync(int fileId, int id, CancellationToken ct)
    {
        var flight = await _flightRepo.GetByIdAsync(id, ct);
        if (flight == null || flight.TravelFileId != fileId) throw new KeyNotFoundException("Vuelo no encontrado");

        if (flight.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(flight.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance -= flight.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        await _flightRepo.DeleteAsync(flight, ct);
    }

    #endregion

    #region Hotels

    public async Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(int fileId, CancellationToken ct)
    {
        return await _hotelRepo.Query()
            .Where(h => h.TravelFileId == fileId)
            .OrderBy(h => h.CheckIn)
            .ProjectTo<HotelBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<HotelBookingDto> GetHotelByIdAsync(int fileId, int id, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.TravelFileId != fileId) throw new KeyNotFoundException("Hotel no encontrado");
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task<HotelBookingDto> CreateHotelAsync(int fileId, CreateHotelRequest req, CancellationToken ct)
    {
        var file = await _fileRepo.GetByIdAsync(fileId, ct);
        if (file == null) throw new KeyNotFoundException("File no encontrado");

        var hotel = _mapper.Map<HotelBooking>(req);
        hotel.TravelFileId = fileId;

        await _hotelRepo.AddAsync(hotel, ct);

        if (hotel.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(hotel.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += hotel.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task<HotelBookingDto> UpdateHotelAsync(int fileId, int id, UpdateHotelRequest req, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.TravelFileId != fileId) throw new KeyNotFoundException("Hotel no encontrado");

        var oldNetCost = hotel.NetCost;
        var oldSupplierId = hotel.SupplierId;

        _mapper.Map(req, hotel);

        if (oldSupplierId > 0 && oldSupplierId == hotel.SupplierId)
        {
            var supplier = await _supplierRepo.GetByIdAsync(hotel.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += (hotel.NetCost - oldNetCost);
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }
        else if (oldSupplierId != hotel.SupplierId)
        {
            if (oldSupplierId > 0)
            {
                var oldSupplier = await _supplierRepo.GetByIdAsync(oldSupplierId, ct);
                if (oldSupplier != null)
                {
                    oldSupplier.CurrentBalance -= oldNetCost;
                    await _supplierRepo.UpdateAsync(oldSupplier, ct);
                }
            }
            if (hotel.SupplierId > 0)
            {
                var newSupplier = await _supplierRepo.GetByIdAsync(hotel.SupplierId, ct);
                if (newSupplier != null)
                {
                    newSupplier.CurrentBalance += hotel.NetCost;
                    await _supplierRepo.UpdateAsync(newSupplier, ct);
                }
            }
        }

        await _hotelRepo.UpdateAsync(hotel, ct);
        return _mapper.Map<HotelBookingDto>(hotel);
    }

    public async Task DeleteHotelAsync(int fileId, int id, CancellationToken ct)
    {
        var hotel = await _hotelRepo.GetByIdAsync(id, ct);
        if (hotel == null || hotel.TravelFileId != fileId) throw new KeyNotFoundException("Hotel no encontrado");

        if (hotel.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(hotel.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance -= hotel.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        await _hotelRepo.DeleteAsync(hotel, ct);
    }

    #endregion

    #region Packages

    public async Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(int fileId, CancellationToken ct)
    {
        return await _packageRepo.Query()
            .Where(p => p.TravelFileId == fileId)
            .OrderBy(p => p.CreatedAt)
            .ProjectTo<PackageBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<PackageBookingDto> CreatePackageAsync(int fileId, CreatePackageRequest req, CancellationToken ct)
    {
        var file = await _fileRepo.GetByIdAsync(fileId, ct);
        if (file == null) throw new KeyNotFoundException("File no encontrado");

        var package = _mapper.Map<PackageBooking>(req);
        package.TravelFileId = fileId;

        await _packageRepo.AddAsync(package, ct);

        if (package.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(package.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += package.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        return _mapper.Map<PackageBookingDto>(package);
    }

    public async Task<PackageBookingDto> UpdatePackageAsync(int fileId, int id, UpdatePackageRequest req, CancellationToken ct)
    {
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.TravelFileId != fileId) throw new KeyNotFoundException("Paquete no encontrado");

        var oldNetCost = package.NetCost;
        var oldSupplierId = package.SupplierId;

        _mapper.Map(req, package);

        if (oldSupplierId > 0 && oldSupplierId == package.SupplierId)
        {
            var supplier = await _supplierRepo.GetByIdAsync(package.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += (package.NetCost - oldNetCost);
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }
        else if (oldSupplierId != package.SupplierId)
        {
            if (oldSupplierId > 0)
            {
                var oldSupplier = await _supplierRepo.GetByIdAsync(oldSupplierId, ct);
                if (oldSupplier != null)
                {
                    oldSupplier.CurrentBalance -= oldNetCost;
                    await _supplierRepo.UpdateAsync(oldSupplier, ct);
                }
            }
            if (package.SupplierId > 0)
            {
                var newSupplier = await _supplierRepo.GetByIdAsync(package.SupplierId, ct);
                if (newSupplier != null)
                {
                    newSupplier.CurrentBalance += package.NetCost;
                    await _supplierRepo.UpdateAsync(newSupplier, ct);
                }
            }
        }

        await _packageRepo.UpdateAsync(package, ct);
        return _mapper.Map<PackageBookingDto>(package);
    }

    public async Task DeletePackageAsync(int fileId, int id, CancellationToken ct)
    {
        var package = await _packageRepo.GetByIdAsync(id, ct);
        if (package == null || package.TravelFileId != fileId) throw new KeyNotFoundException("Paquete no encontrado");

        if (package.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(package.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance -= package.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        await _packageRepo.DeleteAsync(package, ct);
    }

    #endregion

    #region Transfers

    public async Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(int fileId, CancellationToken ct)
    {
        return await _transferRepo.Query()
            .Where(t => t.TravelFileId == fileId)
            .OrderBy(t => t.PickupDateTime)
            .ProjectTo<TransferBookingDto>(_mapper.ConfigurationProvider)
            .ToListAsync(ct);
    }

    public async Task<TransferBookingDto> CreateTransferAsync(int fileId, CreateTransferRequest req, CancellationToken ct)
    {
        var file = await _fileRepo.GetByIdAsync(fileId, ct);
        if (file == null) throw new KeyNotFoundException("File no encontrado");

        var transfer = _mapper.Map<TransferBooking>(req);
        transfer.TravelFileId = fileId;

        await _transferRepo.AddAsync(transfer, ct);

        if (transfer.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(transfer.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += transfer.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        return _mapper.Map<TransferBookingDto>(transfer);
    }

    public async Task<TransferBookingDto> UpdateTransferAsync(int fileId, int id, UpdateTransferRequest req, CancellationToken ct)
    {
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.TravelFileId != fileId) throw new KeyNotFoundException("Traslado no encontrado");

        var oldNetCost = transfer.NetCost;
        var oldSupplierId = transfer.SupplierId;

        _mapper.Map(req, transfer);

        if (oldSupplierId > 0 && oldSupplierId == transfer.SupplierId)
        {
            var supplier = await _supplierRepo.GetByIdAsync(transfer.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance += (transfer.NetCost - oldNetCost);
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }
        else if (oldSupplierId != transfer.SupplierId)
        {
            if (oldSupplierId > 0)
            {
                var oldSupplier = await _supplierRepo.GetByIdAsync(oldSupplierId, ct);
                if (oldSupplier != null)
                {
                    oldSupplier.CurrentBalance -= oldNetCost;
                    await _supplierRepo.UpdateAsync(oldSupplier, ct);
                }
            }
            if (transfer.SupplierId > 0)
            {
                var newSupplier = await _supplierRepo.GetByIdAsync(transfer.SupplierId, ct);
                if (newSupplier != null)
                {
                    newSupplier.CurrentBalance += transfer.NetCost;
                    await _supplierRepo.UpdateAsync(newSupplier, ct);
                }
            }
        }

        await _transferRepo.UpdateAsync(transfer, ct);
        return _mapper.Map<TransferBookingDto>(transfer);
    }

    public async Task DeleteTransferAsync(int fileId, int id, CancellationToken ct)
    {
        var transfer = await _transferRepo.GetByIdAsync(id, ct);
        if (transfer == null || transfer.TravelFileId != fileId) throw new KeyNotFoundException("Traslado no encontrado");

        if (transfer.SupplierId > 0)
        {
            var supplier = await _supplierRepo.GetByIdAsync(transfer.SupplierId, ct);
            if (supplier != null)
            {
                supplier.CurrentBalance -= transfer.NetCost;
                await _supplierRepo.UpdateAsync(supplier, ct);
            }
        }

        await _transferRepo.DeleteAsync(transfer, ct);
    }

    #endregion
}
