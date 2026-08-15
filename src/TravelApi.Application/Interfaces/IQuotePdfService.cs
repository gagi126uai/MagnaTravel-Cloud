using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Genera el PDF de presupuesto que se le manda al cliente (maqueta "minimalista elegante", spec firmada
/// 2026-08-14). Espejo de <see cref="IInvoicePdfService"/>: recibe la <see cref="Reserva"/> y la
/// configuración YA CARGADAS por el caller (mismo patrón — este service NO toca la base de datos ni hace
/// ninguna llamada de red, solo arma bytes de PDF con QuestPDF).
/// </summary>
public interface IQuotePdfService
{
    /// <summary>
    /// Arma el PDF. <paramref name="conditions"/> son los bloques de "letra chica" (solo los que tienen
    /// texto cargado); <paramref name="logoBytes"/> es null si la agencia no cargó logo (la cabecera sale
    /// con el nombre de la agencia en texto, nunca con un placeholder inventado). <paramref name="porPersona"/>
    /// decide si la tarifa de cada servicio se imprime dividida entre <paramref name="cantidadPasajerosCargados"/>
    /// o como total (ver <c>TravelApi.Domain.Reservations.QuoteBudgetPdfRules.ResolveDisplayPrice</c> para
    /// el caso 0 pasajeros).
    ///
    /// <para><paramref name="accentColorHex"/> es el color de acento de la maqueta (spec §5, "paleta según
    /// el destino"): lo resuelve el CALLER de antemano (ver <c>IDestinationPaletteService</c>) porque este
    /// renderer no hace I/O. <c>null</c> (destino no clasificado, IA no configurada, o el caller no la
    /// resolvió) cae al color de respaldo de <c>AgencySettings.PdfBandColorHex</c> — nunca rompe la
    /// emisión por falta de color.</para>
    /// </summary>
    byte[] GenerateQuotePdf(
        Reserva reserva,
        AgencySettings agencySettings,
        IReadOnlyList<BudgetConditionBlock> conditions,
        byte[]? logoBytes,
        bool porPersona,
        int cantidadPasajerosCargados,
        string? accentColorHex = null);
}
