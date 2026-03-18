using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IBookingService
{
    // Flights
    Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(int reservaId, CancellationToken ct);
    Task<FlightSegmentDto> CreateFlightAsync(int reservaId, CreateFlightRequest req, CancellationToken ct);
    Task<FlightSegmentDto> UpdateFlightAsync(int reservaId, int id, UpdateFlightRequest req, CancellationToken ct);
    Task DeleteFlightAsync(int reservaId, int id, CancellationToken ct);

    // Hotels
    Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(int reservaId, CancellationToken ct);
    Task<HotelBookingDto> GetHotelByIdAsync(int reservaId, int id, CancellationToken ct);
    Task<HotelBookingDto> CreateHotelAsync(int reservaId, CreateHotelRequest req, CancellationToken ct);
    Task<HotelBookingDto> UpdateHotelAsync(int reservaId, int id, UpdateHotelRequest req, CancellationToken ct);
    Task DeleteHotelAsync(int reservaId, int id, CancellationToken ct);

    // Packages
    Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(int reservaId, CancellationToken ct);
    Task<PackageBookingDto> CreatePackageAsync(int reservaId, CreatePackageRequest req, CancellationToken ct);
    Task<PackageBookingDto> UpdatePackageAsync(int reservaId, int id, UpdatePackageRequest req, CancellationToken ct);
    Task DeletePackageAsync(int reservaId, int id, CancellationToken ct);

    // Transfers
    Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(int reservaId, CancellationToken ct);
    Task<TransferBookingDto> CreateTransferAsync(int reservaId, CreateTransferRequest req, CancellationToken ct);
    Task<TransferBookingDto> UpdateTransferAsync(int reservaId, int id, UpdateTransferRequest req, CancellationToken ct);
    Task DeleteTransferAsync(int reservaId, int id, CancellationToken ct);
}
