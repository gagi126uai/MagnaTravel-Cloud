using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3, 2026-05-13): aggregate root del saldo a favor que
/// queda para el cliente luego de un refund del operador (T2 del flujo).
///
/// Aggregate boundary:
///  - Pertenece logicamente al <see cref="Customer"/> (vive en su ficha, no en la reserva).
///  - Linkea retro a la allocation que lo origino (audit + reconciliacion).
///  - Owns N <see cref="ClientCreditWithdrawal"/> (cada retiro parcial del saldo).
///
/// Invariante critico (INV-085 / chk_credit_remaining_non_negative):
///   <c>RemainingBalance &gt;= 0</c> y <c>RemainingBalance &lt;= CreditedAmount</c>.
/// La columna <c>RemainingBalance</c> es DENORMALIZADA (cache de
/// <c>CreditedAmount - SUM(Withdrawals.Amount WHERE Kind != KeptAsCredit)</c>)
/// para validacion atomica con CHECK SQL.
///
/// Regla de negocio (regla 5 policy): saldo SIN caducidad. El cliente puede
/// dejarlo indefinido como <c>KeptAsCredit</c>. Si el operador hace una segunda
/// devolucion para la misma BC (regla 12: N retiros), se crea un NUEVO
/// <c>ClientCreditEntry</c>, no se modifica el existente (inmutabilidad).
/// </summary>
public class ClientCreditEntry : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>FK a la allocation que origino este saldo. 1:1 con la allocation no-voided.</summary>
    public int OperatorRefundAllocationId { get; set; }
    public OperatorRefundAllocation Allocation { get; set; } = null!;

    /// <summary>FK a la cancelacion que dio origen. Permite query "saldos pendientes por reserva".</summary>
    public int BookingCancellationId { get; set; }
    public BookingCancellation BookingCancellation { get; set; } = null!;

    /// <summary>Saldo inicial acreditado. Igual a <c>Allocation.NetAmount</c> al crear. Inmutable.</summary>
    public decimal CreditedAmount { get; set; }

    /// <summary>
    /// DENORMALIZADO: <c>CreditedAmount - SUM(Withdrawals.Amount excluyendo KeptAsCredit)</c>.
    /// Service lo mantiene en sync atomicamente. CHECK SQL (<c>chk_credit_remaining_non_negative</c>)
    /// previene retiros que dejarian saldo negativo bajo concurrencia.
    /// </summary>
    public decimal RemainingBalance { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Flag de "todo gastado". Util para queries de saldos activos sin agregacion.</summary>
    public bool IsFullyConsumed { get; set; }

    public ICollection<ClientCreditWithdrawal> Withdrawals { get; set; } = new List<ClientCreditWithdrawal>();
}
