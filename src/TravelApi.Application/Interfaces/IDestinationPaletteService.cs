using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// Elige el color de ACENTO del PDF de presupuesto según el destino del viaje (spec "PDF minimalista
/// elegante", 2026-08-14 §5): un caribe se pinta distinto de una nieve, sin que el vendedor tenga que
/// elegir un color a mano en cada presupuesto.
///
/// <para><b>La IA sugiere, nunca decide el color final</b>: el modelo solo puede elegir UNA palabra de
/// un set CURADO y CERRADO de categorías (caribe/playa/nieve/ciudad/naturaleza/vino/otro). Esta interfaz
/// ya devuelve el color HEX final de esa categoría — el caller (el que arma el PDF) nunca ve la palabra
/// cruda del modelo, así que no hay forma de que una alucinación termine pintando el PDF con un color
/// que no está en la paleta de la agencia.</para>
///
/// <para><b>Contrato de degradación</b> (mismo espíritu que <see cref="IAiChatProvider"/>, ADR-016): esto
/// NUNCA lanza por una falla de IA. Un <c>null</c> significa "no hay categoría clara para este destino,
/// usá el color de respaldo de <c>AgencySettings.PdfBandColorHex</c>" — que es el comportamiento normal
/// cuando no hay IA configurada, cuando el destino no se pudo clasificar, o cuando el modelo contestó
/// cualquier cosa fuera del set.</para>
/// </summary>
public interface IDestinationPaletteService
{
    /// <summary>
    /// Devuelve el color de acento HEX ("#0e7c86") para este destino, o <c>null</c> si corresponde usar
    /// el color de respaldo de la agencia. <paramref name="destinationTitle"/> es el título ya resuelto
    /// (ver <c>QuoteBudgetPdfRules.ResolveDestinationTitle</c>); <paramref name="cityHints"/> son las
    /// ciudades de los servicios cargados (ver <c>QuoteBudgetPdfRules.CollectDestinationCityHints</c>) —
    /// contexto extra para que la IA no confunda un destino homónimo. Ninguno de los dos debe llevar
    /// datos de pasajeros, clientes ni números internos (gate data-exposure): son SOLO nombres de lugar.
    /// </summary>
    Task<string?> ResolveAccentColorHexAsync(
        string? destinationTitle,
        IReadOnlyList<string> cityHints,
        CancellationToken cancellationToken);
}
