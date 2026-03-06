using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IBookingService
{
    // Flights
    Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(int fileId, CancellationToken ct);
    Task<FlightSegmentDto> CreateFlightAsync(int fileId, CreateFlightRequest req, CancellationToken ct);
    Task<FlightSegmentDto> UpdateFlightAsync(int fileId, int id, UpdateFlightRequest req, CancellationToken ct);
    Task DeleteFlightAsync(int fileId, int id, CancellationToken ct);

    // Hotels
    Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(int fileId, CancellationToken ct);
    Task<HotelBookingDto> GetHotelByIdAsync(int fileId, int id, CancellationToken ct);
    Task<HotelBookingDto> CreateHotelAsync(int fileId, CreateHotelRequest req, CancellationToken ct);
    Task<HotelBookingDto> UpdateHotelAsync(int fileId, int id, UpdateHotelRequest req, CancellationToken ct);
    Task DeleteHotelAsync(int fileId, int id, CancellationToken ct);

    // Packages
    Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(int fileId, CancellationToken ct);
    Task<PackageBookingDto> CreatePackageAsync(int fileId, CreatePackageRequest req, CancellationToken ct);
    Task<PackageBookingDto> UpdatePackageAsync(int fileId, int id, UpdatePackageRequest req, CancellationToken ct);
    Task DeletePackageAsync(int fileId, int id, CancellationToken ct);

    // Transfers
    Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(int fileId, CancellationToken ct);
    Task<TransferBookingDto> CreateTransferAsync(int fileId, CreateTransferRequest req, CancellationToken ct);
    Task<TransferBookingDto> UpdateTransferAsync(int fileId, int id, UpdateTransferRequest req, CancellationToken ct);
    Task DeleteTransferAsync(int fileId, int id, CancellationToken ct);
}
