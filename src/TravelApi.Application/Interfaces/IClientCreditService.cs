using TravelApi.Domain.Entities;

namespace TravelApi.Application.Interfaces;

/// <summary>
/// FC1.2.3 v3 §2.3 (2026-05-18, **stub temporal**): contrato del modulo que
/// gestiona el saldo a favor del cliente y los retiros (T3 del flujo).
///
/// <para>
/// <b>Estado actual</b>: solo se expone <see cref="CreateEntryAsync"/> porque es
/// lo unico que necesita FC1.2.2 (<c>OperatorRefundService.AllocateAsync</c> crea
/// un <c>ClientCreditEntry</c> cuando imputa una allocation). El metodo
/// <c>WithdrawAsync</c> y queries no se exponen todavia — llegan en FC1.2.3
/// con la implementacion completa del retiro al cliente.
/// </para>
///
/// <para>
/// <b>Por que se separa de OperatorRefundService</b>: aunque ambos tocan el
/// mismo grafo de entidades, las responsabilidades fiscales son distintas. El
/// refund vive en el aggregate del operador (Supplier); el credit vive en el
/// del cliente (Customer). Separarlos permite que B-IMP-1..5 puedan auditar
/// flujos del cliente sin tocar la logica del operador.
/// </para>
/// </summary>
public interface IClientCreditService
{
    /// <summary>
    /// Crea una <see cref="ClientCreditEntry"/> con saldo inicial igual a
    /// <paramref name="netAmount"/> (el cliente puede retirar ese monto desde
    /// FC1.2.3 en adelante).
    ///
    /// <para>
    /// <b>Llamado solo desde infraestructura</b>: NO expuesto via API publica.
    /// El caller actual es <c>OperatorRefundService.AllocateAsync</c>; en futuras
    /// FCs se puede sumar callers para creditos manuales del admin (out of
    /// scope FC1.2).
    /// </para>
    ///
    /// <para>
    /// <b>Contrato del caller</b>: el entry se agrega al ChangeTracker via
    /// <c>_db.Add</c> pero <b>NO</b> se commitea aca — el caller hace el
    /// <c>SaveChangesAsync</c> envolvente para mantener atomicidad con la
    /// allocation y los demas side-effects (HC1 plan v3).
    /// </para>
    /// </summary>
    Task<ClientCreditEntry> CreateEntryAsync(
        int bookingCancellationId,
        int operatorRefundAllocationId,
        int customerId,
        decimal netAmount,
        string currency,
        string userId,
        string? userName,
        CancellationToken ct);
}
