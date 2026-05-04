using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public class BookingServiceHttpProxy : ReservationsServiceHttpProxyBase, IBookingService
{
    public BookingServiceHttpProxy(HttpClient httpClient)
        : base(httpClient)
    {
    }

    public Task<IEnumerable<FlightSegmentDto>> GetFlightsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
        => GetAsync<IEnumerable<FlightSegmentDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/flights", ct);

    public Task<FlightSegmentDto> CreateFlightAsync(string reservaPublicIdOrLegacyId, CreateFlightRequest req, CancellationToken ct)
        => PostAsync<CreateFlightRequest, FlightSegmentDto>($"api/reservas/{reservaPublicIdOrLegacyId}/flights", req, ct);

    public Task<FlightSegmentDto> UpdateFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateFlightRequest req, CancellationToken ct)
        => PutAsync<UpdateFlightRequest, FlightSegmentDto>($"api/reservas/{reservaPublicIdOrLegacyId}/flights/{publicIdOrLegacyId}", req, ct);

    public Task DeleteFlightAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
        => DeleteAsync($"api/reservas/{reservaPublicIdOrLegacyId}/flights/{publicIdOrLegacyId}", ct);

    public Task<IEnumerable<HotelBookingDto>> GetHotelsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
        => GetAsync<IEnumerable<HotelBookingDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/hotels", ct);

    public Task<HotelBookingDto> GetHotelByIdAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
        => GetAsync<HotelBookingDto>($"api/reservas/{reservaPublicIdOrLegacyId}/hotels/{publicIdOrLegacyId}", ct);

    public Task<HotelBookingDto> CreateHotelAsync(string reservaPublicIdOrLegacyId, CreateHotelRequest req, CancellationToken ct)
        => PostAsync<CreateHotelRequest, HotelBookingDto>($"api/reservas/{reservaPublicIdOrLegacyId}/hotels", req, ct);

    public Task<HotelBookingDto> UpdateHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateHotelRequest req, CancellationToken ct)
        => PutAsync<UpdateHotelRequest, HotelBookingDto>($"api/reservas/{reservaPublicIdOrLegacyId}/hotels/{publicIdOrLegacyId}", req, ct);

    public Task DeleteHotelAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
        => DeleteAsync($"api/reservas/{reservaPublicIdOrLegacyId}/hotels/{publicIdOrLegacyId}", ct);

    public Task<IEnumerable<PackageBookingDto>> GetPackagesAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
        => GetAsync<IEnumerable<PackageBookingDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/packages", ct);

    public Task<PackageBookingDto> CreatePackageAsync(string reservaPublicIdOrLegacyId, CreatePackageRequest req, CancellationToken ct)
        => PostAsync<CreatePackageRequest, PackageBookingDto>($"api/reservas/{reservaPublicIdOrLegacyId}/packages", req, ct);

    public Task<PackageBookingDto> UpdatePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdatePackageRequest req, CancellationToken ct)
        => PutAsync<UpdatePackageRequest, PackageBookingDto>($"api/reservas/{reservaPublicIdOrLegacyId}/packages/{publicIdOrLegacyId}", req, ct);

    public Task DeletePackageAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
        => DeleteAsync($"api/reservas/{reservaPublicIdOrLegacyId}/packages/{publicIdOrLegacyId}", ct);

    public Task<IEnumerable<TransferBookingDto>> GetTransfersAsync(string reservaPublicIdOrLegacyId, CancellationToken ct)
        => GetAsync<IEnumerable<TransferBookingDto>>($"api/reservas/{reservaPublicIdOrLegacyId}/transfers", ct);

    public Task<TransferBookingDto> CreateTransferAsync(string reservaPublicIdOrLegacyId, CreateTransferRequest req, CancellationToken ct)
        => PostAsync<CreateTransferRequest, TransferBookingDto>($"api/reservas/{reservaPublicIdOrLegacyId}/transfers", req, ct);

    public Task<TransferBookingDto> UpdateTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, UpdateTransferRequest req, CancellationToken ct)
        => PutAsync<UpdateTransferRequest, TransferBookingDto>($"api/reservas/{reservaPublicIdOrLegacyId}/transfers/{publicIdOrLegacyId}", req, ct);

    public Task DeleteTransferAsync(string reservaPublicIdOrLegacyId, string publicIdOrLegacyId, CancellationToken ct)
        => DeleteAsync($"api/reservas/{reservaPublicIdOrLegacyId}/transfers/{publicIdOrLegacyId}", ct);

    public Task<HotelBookingDto> UpdateHotelStatusAsync(string publicIdOrLegacyId, string newStatus, CancellationToken ct)
        => PatchAsync<ServiceStatusUpdateRequest, HotelBookingDto>($"api/hotel-bookings/{publicIdOrLegacyId}/status", new ServiceStatusUpdateRequest(newStatus), ct);

    public Task<TransferBookingDto> UpdateTransferStatusAsync(string publicIdOrLegacyId, string newStatus, CancellationToken ct)
        => PatchAsync<ServiceStatusUpdateRequest, TransferBookingDto>($"api/transfer-bookings/{publicIdOrLegacyId}/status", new ServiceStatusUpdateRequest(newStatus), ct);

    public Task<PackageBookingDto> UpdatePackageStatusAsync(string publicIdOrLegacyId, string newStatus, CancellationToken ct)
        => PatchAsync<ServiceStatusUpdateRequest, PackageBookingDto>($"api/package-bookings/{publicIdOrLegacyId}/status", new ServiceStatusUpdateRequest(newStatus), ct);

    public Task<FlightSegmentDto> UpdateFlightStatusAsync(string publicIdOrLegacyId, string newStatus, CancellationToken ct)
        => PatchAsync<ServiceStatusUpdateRequest, FlightSegmentDto>($"api/flight-segments/{publicIdOrLegacyId}/status", new ServiceStatusUpdateRequest(newStatus), ct);
}
