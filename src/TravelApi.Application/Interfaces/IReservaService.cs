using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface IReservaService
{
    Task<ReservaListPageDto> GetReservasAsync(ReservaListQuery query, CancellationToken cancellationToken);
    Task<ReservaDto> GetReservaByIdAsync(int id);
    Task<Reserva> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId);
    
    Task<(ServicioReserva Reservation, string? Warning)> AddServiceAsync(int reservaId, AddServiceRequest request);
    Task<ServicioReserva> UpdateServiceAsync(int serviceId, AddServiceRequest request);
    Task RemoveServiceAsync(int serviceId);

    Task<IEnumerable<PassengerDto>> GetPassengersAsync(int reservaId);
    Task<PassengerDto> AddPassengerAsync(int reservaId, Passenger passenger);
    Task<PassengerDto> UpdatePassengerAsync(int passengerId, Passenger updated);
    Task RemovePassengerAsync(int passengerId);

    Task<IEnumerable<PaymentDto>> GetReservaPaymentsAsync(int reservaId);
    Task<PaymentDto> AddPaymentAsync(int reservaId, Payment payment);
    Task<PaymentDto> UpdatePaymentAsync(int reservaId, int paymentId, Payment updatedPayment);
    Task DeletePaymentAsync(int reservaId, int paymentId);

    Task<Reserva> UpdateStatusAsync(int id, string status);
    Task UpdateBalanceAsync(int reservaId);
    Task<Reserva> ArchiveReservaAsync(int id);
    Task DeleteReservaAsync(int id);
}
