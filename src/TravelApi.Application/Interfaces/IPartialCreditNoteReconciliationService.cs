using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): logica de aplicacion de la bandeja de
/// reconciliacion de NC parciales con recibos vivos.
///
/// <para>El controller delega aca (controllers finos, patron del proyecto). La
/// creacion de casos NO esta aca: nace en <c>AfipService.ApplyPartialCreditNoteReversalAsync</c>
/// junto al Payment reversal (transaccional). Este servicio solo LISTA y CIERRA.</para>
/// </summary>
public interface IPartialCreditNoteReconciliationService
{
    /// <summary>
    /// Lista los casos de la bandeja, paginado. Filtra por estado (pending/resolved/all)
    /// y, opcionalmente, por mes (year + month, estilo MonthNavigator). El estado vigente
    /// de cada recibo se lee EN VIVO de PaymentReceipts (no del snapshot).
    /// </summary>
    Task<PagedResponse<PartialCreditNoteReconciliationDto>> ListAsync(
        PartialCreditNoteReconciliationListQuery query,
        CancellationToken ct);

    /// <summary>
    /// Cierra manualmente un caso (lo marca Resolved). Aplica la regla de 4-ojos con
    /// bypass de admin unico (G5) y exige notas si se cierra con recibos vivos (R4).
    ///
    /// <para>Lanza:
    /// <list type="bullet">
    ///   <item><c>KeyNotFoundException</c> si el caso no existe -> 404.</item>
    ///   <item><c>InvalidOperationException</c> si ya esta Resolved, o si el 4-ojos /
    ///   las notas no se cumplen -> 409.</item>
    ///   <item><c>DbUpdateConcurrencyException</c> si otro encargado lo cerro a la vez
    ///   (xmin) -> el controller lo mapea a 409.</item>
    /// </list></para>
    /// </summary>
    /// <returns>El caso ya cerrado (para que el frontend refresque la fila).</returns>
    Task<PartialCreditNoteReconciliationDto> ResolveAsync(
        Guid publicId,
        ResolvePartialCreditNoteReconciliationRequest request,
        string currentUserId,
        string? currentUserName,
        CancellationToken ct);
}
