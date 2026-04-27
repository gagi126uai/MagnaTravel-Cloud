namespace TravelApi.Application.Interfaces;

public interface IWhatsAppDeliveryService
{
    Task<bool> TryHandleIncomingOperationalMessageAsync(string phone, string message, CancellationToken cancellationToken);
}
