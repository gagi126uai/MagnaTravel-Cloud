using TravelApi.Models;

namespace TravelApi.Services;

public interface IInvoicePdfService
{
    byte[] GenerateInvoicePdf(Invoice invoice, TravelFile travelFile, AfipSettings afipSettings, AgencySettings agencySettings);
}
