using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

public interface ITravelFileService
{
    Task<IEnumerable<TravelFileListDto>> GetFilesAsync();
    Task<TravelFileDto> GetFileAsync(int id);
    Task<TravelFile> CreateFileAsync(CreateFileRequest request);
    
    Task<(Reservation Reservation, string? Warning)> AddServiceAsync(int fileId, AddServiceRequest request);
    Task<Reservation> UpdateServiceAsync(int serviceId, AddServiceRequest request);
    Task RemoveServiceAsync(int serviceId);

    Task<IEnumerable<PassengerDto>> GetPassengersAsync(int fileId);
    Task<PassengerDto> AddPassengerAsync(int fileId, Passenger passenger);
    Task<PassengerDto> UpdatePassengerAsync(int passengerId, Passenger updated);
    Task RemovePassengerAsync(int passengerId);

    Task<IEnumerable<PaymentDto>> GetFilePaymentsAsync(int fileId);
    Task<PaymentDto> AddPaymentAsync(int fileId, Payment payment);
    Task<PaymentDto> UpdatePaymentAsync(int fileId, int paymentId, Payment updatedPayment);
    Task DeletePaymentAsync(int fileId, int paymentId);

    Task<TravelFile> UpdateStatusAsync(int id, string status);
    Task<TravelFile> ArchiveFileAsync(int id);
    Task DeleteFileAsync(int id);
}
