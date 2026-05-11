using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// B1.15 Fase D' (2026-05-11): vista unificada de movimientos financieros
/// (Payment + Invoice + CreditNoteReversal en una sola tabla cronologica).
///
/// El service NO modifica datos — es pura lectura para el rediseño UX de
/// Cobranza y Facturacion. Filter mine respeta ownership de Vendedor.
/// </summary>
public interface IMovementsService
{
    Task<PagedResponse<MovementDto>> GetAsync(MovementsListQuery query, CancellationToken ct = default);
}
