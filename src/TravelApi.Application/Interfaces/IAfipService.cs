using TravelApi.Domain.Entities;
using TravelApi.Domain.Entities.Afip;
using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

public interface IAfipService
{
    // Configuration
    Task<bool> ValidateCertificate(byte[] certData, string password);
    Task<string> GetStatus();
    Task<AfipSettings?> GetSettingsAsync();
    Task<AfipSettings> UpdateSettingsAsync(long cuit, int puntoDeVenta, bool isProduction, string taxCondition, byte[]? certificateData, string? certificateFileName, string? password);

    // Core
    Task<Invoice> CreatePendingInvoice(int ReservaId, CreateInvoiceRequest request);
    Task ProcessInvoiceJob(int invoiceId);
    Task<AfipVoucherDetails?> GetVoucherDetails(int cbteTipo, int ptoVta, long cbteNro);
    Task<object?> GetPersonaDetailsAsync(long cuit);
}
