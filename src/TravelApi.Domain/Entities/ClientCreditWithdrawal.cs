using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3, 2026-05-13): retiro parcial o total de un
/// <see cref="ClientCreditEntry"/>. Es child del entry, no aggregate root.
/// Modela T3 del flujo: el cliente decide que hacer con su saldo.
///
/// Tipos de retiro (ver <see cref="WithdrawalKind"/>):
///   - <c>KeptAsCredit</c>     : no resta del saldo, marca decision del cliente.
///   - <c>PhysicalCash</c>     : genera <see cref="ManualCashMovement"/> Expense + valida Ley 25.345.
///   - <c>Transfer</c>         : genera <see cref="ManualCashMovement"/> Expense (sin limite Ley 25.345).
///   - <c>AppliedToNewBooking</c>: aplica como Payment de una nueva reserva (TODO FC4).
///   - <c>ReversedToOperator</c>: cliente devuelve dinero recibido. Requiere
///                                 <c>ApprovalRequest.ClientRefundReversal</c>.
///
/// Audit trail reforzado:
///  - Para PhysicalCash el AuditLog.Action es <c>ClientCreditPhysicalRefundExecuted</c>.
///  - Para ReversedToOperator el AuditLog.Action es <c>ClientRefundReversalApproved</c>.
///  - Daily egress report agrega estas filas (ADR-002 §8).
/// </summary>
public class ClientCreditWithdrawal : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int ClientCreditEntryId { get; set; }
    public ClientCreditEntry Entry { get; set; } = null!;

    /// <summary>
    /// FK al movimiento fisico de caja asociado (cuando aplica). Null cuando
    /// <see cref="Kind"/> = <c>KeptAsCredit</c> (no hay flujo fisico). Tambien
    /// puede ser null para <c>AppliedToNewBooking</c> si se modela como ajuste
    /// de Payment directo (decision diferida a FC4).
    /// </summary>
    public int? ManualCashMovementId { get; set; }
    public ManualCashMovement? ManualCashMovement { get; set; }

    /// <summary>Monto del retiro. Para <c>KeptAsCredit</c> = 0 (es solo registro de decision).</summary>
    public decimal Amount { get; set; }

    public WithdrawalKind Kind { get; set; }

    [Required]
    [MaxLength(450)]
    public string ExecutedByUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ExecutedByUserName { get; set; } = string.Empty;

    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    /// <summary>PublicId de la <see cref="ApprovalRequest"/> consumida cuando <see cref="Kind"/> = <c>ReversedToOperator</c>. Audit fiscal.</summary>
    [MaxLength(64)]
    public string? ApprovalRequestId { get; set; }
}
