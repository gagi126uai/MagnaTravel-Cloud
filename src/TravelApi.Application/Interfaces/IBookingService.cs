using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IBookingService
{
    Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct);
    Task<FlightSegmentDto> CreateFlightAsync(string reservaPublicIdOrLegacyId, CreateFlightRequest req, CancellationToken ct);
    Task<FlightSegmentDto> UpdateFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateFlightRequest req, CancellationToken ct);
    Task DeleteFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct);

    Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct);
    Task<HotelBookingDto> GetHotelByIdAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct);
    Task<HotelBookingDto> CreateHotelAsync(string reservaPublicIdOrLegacyId, CreateHotelRequest req, CancellationToken ct);
    Task<HotelBookingDto> UpdateHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateHotelRequest req, CancellationToken ct);
    Task DeleteHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct);

    Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(string reservaPublicIdOrLegacyId, CancellationToken ct);
    Task<PackageBookingDto> CreatePackageAsync(string reservaPublicIdOrLegacyId, CreatePackageRequest req, CancellationToken ct);
    Task<PackageBookingDto> UpdatePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdatePackageRequest req, CancellationToken ct);
    Task DeletePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct);

    Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(string reservaPublicIdOrLegacyId, CancellationToken ct);
    Task<TransferBookingDto> CreateTransferAsync(string reservaPublicIdOrLegacyId, CreateTransferRequest req, CancellationToken ct);
    Task<TransferBookingDto> UpdateTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateTransferRequest req, CancellationToken ct);
    Task DeleteTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct);
}
