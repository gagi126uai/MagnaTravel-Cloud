using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// (2026-08-19, "descalce devolución-caja") Arma el <c>TargetUrl</c> de un aviso — la ruta relativa a la
/// que navega la campanita al hacer click — SIN persistir una columna nueva en <see cref="Notification"/>.
///
/// <para><b>Por qué no vive en <c>Notification.TargetUrl</c> directo</b>: agregar esa columna hubiera
/// requerido una migración. En su lugar, este resolver DERIVA la URL en el momento de leer/enviar el aviso,
/// a partir de <see cref="Notification.RelatedEntityType"/> (ya persistido) — el mismo dato que ya usa
/// <see cref="Notification.ResolutionKey"/> para deduplicar. Solo reconoce los tipos de aviso que hoy
/// necesitan link (<c>CashLedgerRefundReconciliation</c>); el resto devuelve <c>null</c> y esa fila de la
/// campanita se comporta exactamente igual que hoy (sin navegación).</para>
///
/// <para>Recibe una LISTA para poder resolver en lote (una sola consulta batched a la BD) cuando la
/// campanita trae varios avisos de una — evita N+1 al listar <c>GET /api/notifications</c>.</para>
/// </summary>
public interface INotificationTargetUrlResolver
{
    /// <summary>
    /// Devuelve, por cada <see cref="Notification.Id"/> de la lista que SÍ tiene un destino conocido, la
    /// URL relativa a usar. Los avisos sin destino conocido simplemente no aparecen en el diccionario
    /// (el caller debe tratar la ausencia como <c>null</c>).
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> ResolveManyAsync(
        IReadOnlyList<Notification> notifications, CancellationToken ct = default);
}
