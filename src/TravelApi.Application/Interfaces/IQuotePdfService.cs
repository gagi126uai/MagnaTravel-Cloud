using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Genera el PDF de presupuesto que se le manda al cliente (obra "PDF de presupuesto", maqueta v2
/// firmada por el dueño, 2026-08-11/12). Espejo de <see cref="IInvoicePdfService"/>: recibe la
/// <see cref="Reserva"/> y la configuración YA CARGADAS por el caller (mismo patrón — este service NO
/// toca la base de datos, solo arma bytes de PDF con QuestPDF).
/// </summary>
public interface IQuotePdfService
{
    /// <summary>
    /// Arma el PDF. <paramref name="conditions"/> son los bloques de "letra chica" (solo los que tienen
    /// texto cargado); <paramref name="logoBytes"/> es null si la agencia no cargó logo (la banda sale
    /// sin logo, nunca con un placeholder inventado). <paramref name="porPersona"/> decide si la tarifa
    /// de cada servicio se imprime dividida entre <paramref name="cantidadPasajerosCargados"/> o como
    /// total (ver <c>TravelApi.Domain.Reservations.QuoteBudgetPdfRules.ResolveDisplayPrice</c> para el
    /// caso 0 pasajeros).
    /// </summary>
    byte[] GenerateQuotePdf(
        Reserva reserva,
        AgencySettings agencySettings,
        IReadOnlyList<BudgetConditionBlock> conditions,
        byte[]? logoBytes,
        bool porPersona,
        int cantidadPasajerosCargados);
}
