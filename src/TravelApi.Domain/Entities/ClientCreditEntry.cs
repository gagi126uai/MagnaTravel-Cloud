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

    /// <summary>
    /// FK a la allocation que origino este saldo (1:1 con la allocation no-voided) cuando el
    /// credito nace de una CANCELACION.
    /// <para>ADR-022 §4.9 (Q1): NULLABLE. Un credito de SOBREPAGO no nace de una allocation, asi
    /// que la deja en null y se trazea por <see cref="SourcePaymentId"/>/<see cref="SourceReservaId"/>.</para>
    /// </summary>
    public int? OperatorRefundAllocationId { get; set; }
    public OperatorRefundAllocation? Allocation { get; set; }

    /// <summary>
    /// FK a la cancelacion que dio origen (permite query "saldos pendientes por reserva") cuando
    /// el credito nace de una CANCELACION.
    /// <para>ADR-022 §4.9 (Q1): NULLABLE. Un credito de SOBREPAGO no tiene cancelacion detras (queda
    /// null). Es el discriminador de origen para la guarda B5: si es null, consumir totalmente el
    /// entry NO dispara <c>OnAllCreditConsumedAsync</c> (no hay BC que cerrar).</para>
    /// </summary>
    public int? BookingCancellationId { get; set; }
    public BookingCancellation? BookingCancellation { get; set; }

    /// <summary>
    /// ADR-022 §4.9 (Q1): moneda del bolsillo de saldo a favor. NOT NULL, default ARS a nivel BD
    /// (los creditos legacy quedan en pesos). El bolsillo es POR MONEDA: el saldo a favor en USD no
    /// compensa la deuda en ARS (mismo principio que el saldo de la reserva).
    /// <list type="bullet">
    /// <item>Cancelacion: se puebla con <c>OperatorRefundReceived.Currency</c> (ya existe hoy).</item>
    /// <item>Sobrepago: se puebla con la moneda del saldo de la reserva que se sobre-pago.</item>
    /// </list>
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    // --- Origen "sobrepago" (ADR-022 §4.9 / §10 Q1) ---
    // Solo se setean cuando el credito nace de un sobrepago (no de una cancelacion).
    // Trazabilidad: que reserva quedo sobre-pagada y que cobro genero el excedente.

    /// <summary>SOBREPAGO: el <see cref="Payment"/> cuyo excedente genero este credito. Null en creditos de cancelacion.</summary>
    public int? SourcePaymentId { get; set; }
    public Payment? SourcePayment { get; set; }

    /// <summary>SOBREPAGO: la <see cref="Reserva"/> que quedo sobre-pagada. Null en creditos de cancelacion.</summary>
    public int? SourceReservaId { get; set; }
    public Reserva? SourceReserva { get; set; }

    /// <summary>SOBREPAGO: actor que registro el cobro que dejo el excedente. Audit. Null en creditos de cancelacion.</summary>
    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    [MaxLength(200)]
    public string? CreatedByUserName { get; set; }

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
