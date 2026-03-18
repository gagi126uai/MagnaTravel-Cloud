using TravelApi.Domain.Entities;
using TravelApi.Domain.Entities.Afip;

namespace TravelApi.Application.Interfaces;

public interface IInvoicePdfService
{
    byte[] GenerateInvoicePdf(Invoice invoice, Reserva reserva, AfipSettings afipSettings, AgencySettings agencySettings);
}
