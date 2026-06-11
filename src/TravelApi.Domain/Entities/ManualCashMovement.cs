using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public static class CashMovementDirections
{
    public const string Income = "Income";
    public const string Expense = "Expense";
}

public class ManualCashMovement : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    [MaxLength(20)]
    public string Direction { get; set; } = CashMovementDirections.Expense;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>
    /// ADR-022 §4.12 (T2): moneda REAL del gasto/ajuste manual. NOT NULL, default ARS a nivel BD
    /// (los movimientos manuales legacy quedan en pesos automaticamente). Su <see cref="CashLedgerEntry"/>
    /// de tipo <c>ManualAdjustment</c> toma la moneda de aca.
    ///
    /// <para><b>OJO (B1)</b>: esta columna llena la moneda SOLO de los gastos/ajustes que se cargan a mano.
    /// Los <see cref="ManualCashMovement"/> de cancelacion (refund/withdrawal) nacen con <c>'ARS'</c> por
    /// default y su asiento NO toma la moneda de aca, sino del origen real (<c>OperatorRefundReceived.Currency</c>
    /// o <c>ClientCreditEntry.Currency</c>). No derivar la moneda del asiento de cancelacion de esta columna.</para>
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    [MaxLength(50)]
    public string Method { get; set; } = "Transfer";

    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Reference { get; set; }

    [MaxLength(200)]
    public string CreatedBy { get; set; } = "System";

    public bool IsVoided { get; set; }
    public DateTime? VoidedAt { get; set; }

    public int? RelatedReservaId { get; set; }
    public Reserva? RelatedReserva { get; set; }

    public int? RelatedSupplierId { get; set; }
    public Supplier? RelatedSupplier { get; set; }

    /// <summary>
    /// FC1 (ADR-002 §2.3.2 / §3.1, 2026-05-13): link al retiro de saldo del
    /// cliente que origino este egreso fisico. Cuando se genera un retiro de
    /// tipo <c>PhysicalCash</c>, <c>Transfer</c> o <c>AppliedToNewBooking</c>,
    /// el service crea (en la misma transaccion) un <see cref="ManualCashMovement"/>
    /// con direction <c>Expense</c> y este FK seteado. De esta forma el egreso
    /// aparece en el Libro de Caja (<c>TreasuryService.GetCashSummaryAsync</c>)
    /// y se cierra el bug INV-CONT-09 (NC sin reflejo en caja).
    /// Null = movimiento no relacionado con el modulo de cancelacion.
    /// </summary>
    public int? ClientCreditWithdrawalId { get; set; }
    public ClientCreditWithdrawal? ClientCreditWithdrawal { get; set; }

    /// <summary>
    /// FC1 (ADR-002 §2.3.2, 2026-05-13): link al ingreso fisico recibido del
    /// operador (T2 del flujo de cancelacion). Cuando el cashier registra un
    /// <see cref="OperatorRefundReceived"/>, el service crea (en la misma
    /// transaccion) un <see cref="ManualCashMovement"/> con direction
    /// <c>Income</c> y este FK seteado, para que el ingreso aparezca en caja.
    /// Null = movimiento no relacionado con un refund de operador.
    /// </summary>
    public int? OperatorRefundReceivedId { get; set; }
    public OperatorRefundReceived? OperatorRefundReceived { get; set; }
}
