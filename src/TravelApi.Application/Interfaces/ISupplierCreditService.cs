using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// ADR-041 TANDA 3 (lado proveedor, 2026-06-27): gestiona el SALDO A FAVOR CONSUMIBLE con un operador
/// (<c>SupplierCreditEntry</c>) y sus aplicaciones/reversas (<c>SupplierCreditApplication</c>). Espejo del
/// <c>IClientCreditService</c> del lado cliente.
///
/// <para>La CREACION del saldo a favor (cuando un pago al operador genera sobrepago) NO vive aca: la hace el
/// recalculo de la deuda del proveedor en la misma transaccion del pago (ver <c>SupplierCreditReconciler</c>).
/// Este servicio expone LECTURA, APLICACION y REVERSA.</para>
/// </summary>
public interface ISupplierCreditService
{
    /// <summary>
    /// Saldo a favor disponible con el operador, agrupado por moneda (entries con <c>RemainingBalance &gt; 0</c>).
    /// Los montos respetan el masking <c>cobranzas.see_cost</c>. <paramref name="supplierId"/> es el id interno.
    /// </summary>
    Task<SupplierCreditOverviewDto> GetSupplierCreditAsync(int supplierId, CancellationToken ct);

    /// <summary>
    /// Aplica saldo a favor del operador a OTRA reserva del MISMO operador y MISMA moneda. Drena el pool y baja
    /// la deuda-por-reserva del destino, NETO-CERO sobre el Balance agregado del operador. Atomico, con retry de
    /// concurrencia. Valida: monto &gt; 0 y &lt;= disponible en esa moneda; moneda no cruzada; reserva destino del
    /// mismo operador. Devuelve el resultado con el saldo disponible restante.
    /// </summary>
    Task<SupplierCreditApplicationResultDto> ApplyCreditAsync(
        int supplierId,
        ApplySupplierCreditRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Revierte una aplicacion previa (contra-fila inmutable): repone el pool y deshace la imputacion en la
    /// reserva destino. Atomico, con retry de concurrencia. Exige motivo (&gt;= 10 chars) y bloquea la doble-reversa.
    /// </summary>
    Task<SupplierCreditApplicationResultDto> ReverseApplicationAsync(
        int supplierId,
        Guid applicationPublicId,
        ReverseSupplierCreditApplicationRequest request,
        string userId,
        string? userName,
        CancellationToken ct);
}
