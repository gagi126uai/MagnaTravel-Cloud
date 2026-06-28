using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-041 TANDA 4 (2026-06-28): read-model SOLO LECTURA de "reembolsos a cobrar del operador" — las
/// cancelaciones que estan esperando (o se dieron por perdidas esperando) el reintegro del operador. Existe para
/// que la agencia pueda VER esa cuenta por cobrar desde la ficha del proveedor y desde una bandeja global, sin
/// tener que abrir Caja. No muta nada: reusa <c>BookingCancellation</c> + sus lineas.
///
/// <para><b>Montos enmascarados</b>: los estimados de reembolso son COSTO; sin <c>cobranzas.see_cost</c> se
/// devuelven en 0 (la estructura — que reservas, que operador, semaforo — se ve igual). Mismo criterio que el
/// resto de la cuenta del proveedor.</para>
/// </summary>
public interface IOperatorRefundReadModelService
{
    /// <summary>
    /// Reembolsos pendientes del operador <paramref name="supplierId"/>: sus cancelaciones en
    /// <c>AwaitingOperatorRefund</c> (con semaforo A tiempo / Por vencer / Vencido) o ya abandonadas
    /// (<c>AbandonedByOperator</c>). Una fila por cancelacion donde este operador debe reembolsar.
    /// </summary>
    Task<IReadOnlyList<OperatorRefundPendingItemDto>> GetSupplierPendingRefundsAsync(
        int supplierId, CancellationToken ct);

    /// <summary>
    /// Bandeja GLOBAL: todos los reembolsos a cobrar de TODOS los operadores. Una fila por (cancelacion, operador).
    /// Cruza clientes/operadores -> el controller la gatea con un permiso mas fuerte que el de ver un proveedor.
    /// </summary>
    Task<IReadOnlyList<OperatorRefundPendingItemDto>> GetAllPendingRefundsAsync(CancellationToken ct);
}
