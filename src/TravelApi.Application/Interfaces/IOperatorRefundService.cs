using TravelApi.Application.DTOs;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.2.2 v3 §2.2 (2026-05-18): contrato del modulo que gestiona los ingresos
/// fisicos que la agencia recibe del operador (T2 del flujo) y las allocations
/// N:M de ese dinero contra los <c>BookingCancellation</c>.
///
/// <para>
/// <b>Responsabilidad</b>: registrar el ingreso fisico + repartirlo entre BCs
/// con sus deducciones tipificadas + manejar void y reasociacion para corregir
/// errores operativos. El service NO crea los <c>BookingCancellation</c> (eso es
/// responsabilidad de <c>IBookingCancellationService</c>) y NO maneja los
/// retiros del cliente (eso es <c>IClientCreditService</c>) — solo se queda
/// con el "puente" entre el deposito y el saldo del cliente.
/// </para>
///
/// <para>
/// <b>Concurrencia N:M</b>: el invariante critico es
/// <c>SUM(allocations.GrossAmount) &lt;= refund.ReceivedAmount</c>. Lo enforza
/// un CHECK constraint Postgres (<c>chk_OperatorRefundsReceived_allocated_not_exceeds</c>)
/// junto con el concurrency token xmin del refund. El service implementa retry
/// limitado ante <c>DbUpdateConcurrencyException</c> para resolver carreras
/// reales sin pasar al usuario el error tecnico.
/// </para>
///
/// <para>
/// <b>Matriz fiscal</b>: <see cref="AllocateAsync"/> valida la matriz
/// agencia-operador segun el <see cref="Domain.Entities.FiscalSnapshot"/> del
/// BC. Casos rechazados (cierre arca-tax round 2):
/// <list type="bullet">
/// <item>INV-105: deducciones tipo retencion AR (kinds 10..39) con
///       Supplier Monotributo → no aplica regimen.</item>
/// <item>INV-115: deducciones tipo retencion AR con Agency Monotributo → no
///       hay credito fiscal IVA, registrar la retencion seria "regalarla".</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Feature flag</b>: todas las operaciones validan
/// <c>OperationalFinanceSettings.EnableNewCancellationFlow</c>. Si esta off,
/// el service rechaza con <c>InvalidOperationException</c> (mensaje claro al
/// frontend, no 500).
/// </para>
/// </summary>
public interface IOperatorRefundService
{
    /// <summary>
    /// Registra un ingreso fisico de operador (T2 inicial). Crea
    /// <c>OperatorRefundReceived</c> + <c>ManualCashMovement</c> Income asociado
    /// (linkeado via FK <c>OperatorRefundReceivedId</c>) en una sola transaccion.
    ///
    /// <para>
    /// El ingreso queda con <c>AllocatedAmount = 0</c> hasta que se llame
    /// <see cref="AllocateAsync"/> una o mas veces.
    /// </para>
    /// </summary>
    Task<OperatorRefundReceivedDto> RecordReceivedAsync(
        RecordOperatorRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Imputa parte del refund recibido contra UN <c>BookingCancellation</c>.
    ///
    /// <para>
    /// <b>Atomicidad</b>: crea la <c>OperatorRefundAllocation</c> + sus
    /// <c>DeductionLine</c>s + el <c>ClientCreditEntry</c> + actualiza
    /// <c>refund.AllocatedAmount</c> y <c>bc.ReceivedRefundAmount</c> en un solo
    /// <c>SaveChangesAsync</c>. Si el CHECK SQL del cap rechaza, EF tira
    /// <c>BusinessInvariantViolationException</c> (mapeado por el interceptor) y
    /// la transaccion entera revierte.
    /// </para>
    ///
    /// <para>
    /// <b>Retry xmin</b>: ante <c>DbUpdateConcurrencyException</c> el service
    /// reintenta hasta 3 veces (recargando refund y bc) — el segundo cashier
    /// gana o termina rechazado por cap, no por un error tecnico.
    /// </para>
    /// </summary>
    Task<OperatorRefundAllocationDto> AllocateAsync(
        Guid refundPublicId,
        AllocateRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Conveniencia (2026-07-01): registra el ingreso fisico del operador Y lo imputa a UNA cancelacion en
    /// UNA sola operacion atomica (camino SIMPLE, sin deducciones fiscales: todo el bruto va a saldo a favor
    /// del cliente). Reusa internamente <see cref="RecordReceivedAsync"/> + <see cref="AllocateAsync"/>.
    ///
    /// <para>
    /// <b>Atomicidad</b>: contra Postgres envuelve ambos pasos en UNA transaccion; si la imputacion falla, el
    /// ingreso NO queda registrado (rollback total, sin plata huerfana en caja). Antes de registrar corre un
    /// pre-flight que valida que la cancelacion pueda recibir el reembolso (estado, operador, moneda) para
    /// fallar temprano con un mensaje claro y no dejar un ingreso a medio crear.
    /// </para>
    ///
    /// <para>
    /// Para el camino AVANZADO con retenciones tipificadas se siguen usando <see cref="RecordReceivedAsync"/> y
    /// <see cref="AllocateAsync"/> por separado; este metodo no lo reemplaza.
    /// </para>
    /// </summary>
    Task<OperatorRefundAllocationDto> RecordAndAllocateAsync(
        RecordAndAllocateRefundRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Anula una allocation existente (soft-void: la fila se preserva con
    /// <c>IsVoided=true</c> + metadata <c>VoidedAt/By/Reason</c>). Libera el cap
    /// del refund (decrementa <c>AllocatedAmount</c>) para permitir reallocate.
    ///
    /// <para>
    /// <b>Restriccion</b>: si el <see cref="Domain.Entities.ClientCreditEntry"/>
    /// asociado tiene <c>Withdrawals</c> ya consumidos por el cliente, rechazamos
    /// con <c>InvalidOperationException</c>: no podemos "quitar" plata que ya
    /// salio de caja sin un <see cref="Domain.Entities.WithdrawalKind.ReversedToOperator"/>
    /// previo (que viene en FC1.2.3).
    /// </para>
    /// </summary>
    Task<OperatorRefundAllocationDto> VoidAllocationAsync(
        Guid allocationPublicId,
        VoidAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>
    /// Mueve una allocation entre BCs en una sola transaccion (caso: contador
    /// detecta imputacion incorrecta). Atomic: void de la vieja + create de la
    /// nueva con snapshot fiscal recalculado del BC destino.
    ///
    /// <para>
    /// <b>Razon de existencia</b>: si solo dieramos VoidAllocation + Allocate, en
    /// la ventana entre ambas otra operacion podria consumir el cap. Forzar la
    /// atomicidad evita esa carrera y deja UN solo audit log
    /// "...Reassociated" que es trivial de leer.
    /// </para>
    /// </summary>
    Task<OperatorRefundAllocationDto> ReassociateAllocationAsync(
        Guid allocationPublicId,
        ReassociateAllocationRequest request,
        string userId,
        string? userName,
        CancellationToken ct);

    /// <summary>Lectura por PublicId. Null si no existe.</summary>
    Task<OperatorRefundReceivedDto?> GetByPublicIdAsync(
        Guid publicId,
        CancellationToken ct);
}
