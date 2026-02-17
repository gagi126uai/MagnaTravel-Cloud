using TravelApi.DTOs;
using TravelApi.Models;

namespace TravelApi.Services;

public interface IInvoiceService
{
    Task ProcessAnnulmentJob(int invoiceId, string userId);
}
