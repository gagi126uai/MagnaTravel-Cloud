using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-022 §4.1: tipos de origen de un asiento del Libro de Caja. El asiento SIEMPRE
/// nace de UN hecho economico ya existente (su "puerta unica"); el SourceType dice cual.
/// Se persiste como string corto para que la lectura del libro sea legible sin joins.
/// </summary>
public static class CashLedgerSourceTypes
{
    /// <summary>Cobro a cliente. Origen: <see cref="Payment"/> (puerta <c>PaymentService</c>).</summary>
    public const string CustomerPayment = "CustomerPayment";

    /// <summary>Pago a proveedor. Origen: <see cref="SupplierPayment"/> (puerta <c>SupplierService</c>).</summary>
    public const string SupplierPayment = "SupplierPayment";

    /// <summary>Devolucion recibida del operador. Origen: el <see cref="ManualCashMovement"/> del refund (flujo cancelacion).</summary>
    public const string OperatorRefund = "OperatorRefund";

    /// <summary>Devolucion fisica al cliente. Origen: el <see cref="ManualCashMovement"/> del retiro de saldo (flujo cancelacion).</summary>
    public const string ClientCreditWithdrawal = "ClientCreditWithdrawal";

    /// <summary>Gasto/ajuste de caja que no es ninguno de los anteriores. Origen: <see cref="ManualCashMovement"/> puro.</summary>
    public const string ManualAdjustment = "ManualAdjustment";
}

/// <summary>
/// ADR-022 §4.1: el Libro de Caja persistido. Un asiento INMUTABLE por cada hecho economico
/// que mueve caja, en su MONEDA REAL (la que entro/salio de caja, nunca la imputada), ligado
/// obligatoriamente a su origen. El asiento es la fuente de verdad de la CAJA; los saldos
/// (reserva/cliente/proveedor) son derivados y conservan su propia fuente.
///
/// <para><b>Por que FKs tipadas y no una FK polimorfica (SourceType, SourceId)</b>: EF Core no
/// modela bien el polimorfismo por par (tipo, id) — no hay FK real, no hay integridad referencial
/// ni joins confiables. Cinco FKs nullable con un CHECK "exactamente una no-null" da integridad
/// real, indices usables y queries por origen sin switch sobre strings. Es el mismo patron que
/// <see cref="ManualCashMovement"/> ya usa con <c>OperatorRefundReceivedId</c>/<c>ClientCreditWithdrawalId</c>.</para>
///
/// <para><b>Anular != borrar (reversa)</b>: el libro NUNCA borra. Para anular un cobro/pago se marca
/// el asiento original <see cref="IsReversed"/>=true y se crea un asiento espejo con <see cref="Direction"/>
/// invertida, <see cref="IsReversal"/>=true y <see cref="ReversedEntryId"/> al original. El neto
/// (original + reversa) es 0 y la historia queda intacta.</para>
/// </summary>
public class CashLedgerEntry : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // --- Hecho economico (inmutable una vez escrito) ---

    /// <summary>Income | Expense. Reusa <see cref="CashMovementDirections"/> (mismo vocabulario que ManualCashMovement).</summary>
    [MaxLength(20)]
    public string Direction { get; set; } = CashMovementDirections.Income;

    /// <summary>Monto SIEMPRE positivo; el signo lo da <see cref="Direction"/>. CHECK SQL <c>Amount &gt; 0</c>.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    /// <summary>Moneda REAL del movimiento (la que entro/salio de caja). NUNCA la imputada. Default ARS.</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Forma de pago del hecho al ocurrir (Cash, Transfer, Card, Check...). Foto inmutable.</summary>
    [MaxLength(50)]
    public string Method { get; set; } = "Transfer";

    /// <summary>Cuando ocurrio el hecho (PaidAt / ReceivedAt / OccurredAt del origen). Distinto de <see cref="CreatedAt"/>.</summary>
    public DateTime OccurredAt { get; set; }

    // --- Trazabilidad / auditoria ---

    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }

    /// <summary>Cuando se ESCRIBIO el asiento (puede ser != <see cref="OccurredAt"/> si se asienta tarde o en backfill).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ADR-028 fix (2026-06-13): la columna nacio en varchar(20), pero la constante mas larga
    // "ClientCreditWithdrawal" mide 22. El path de refund de operador ("OperatorRefund", 14) cabia,
    // por eso el bug quedo latente; recien explota al asentar un RETIRO de saldo del cliente (Withdraw).
    // InMemory no valida longitud -> solo se veia en Postgres (trap M2). La subimos a 50 (igual que Method).
    [MaxLength(50)]
    public string SourceType { get; set; } = CashLedgerSourceTypes.ManualAdjustment;

    // --- Origen (exactamente UNO no-null segun SourceType; CHECK SQL lo garantiza) ---

    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }

    public int? SupplierPaymentId { get; set; }
    public SupplierPayment? SupplierPayment { get; set; }

    public int? OperatorRefundReceivedId { get; set; }
    public OperatorRefundReceived? OperatorRefundReceived { get; set; }

    public int? ClientCreditWithdrawalId { get; set; }
    public ClientCreditWithdrawal? ClientCreditWithdrawal { get; set; }

    public int? ManualCashMovementId { get; set; }
    public ManualCashMovement? ManualCashMovement { get; set; }

    // --- Trazabilidad de negocio (opcional, para filtros/reportes; NO afecta saldos) ---

    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // --- Reversa (contra-asiento; anular != borrar) ---

    /// <summary>true = este asiento revierte a otro (contra-asiento, Direction invertida).</summary>
    public bool IsReversal { get; set; }

    /// <summary>FK al asiento original que este reversa (solo cuando <see cref="IsReversal"/>=true).</summary>
    public int? ReversedEntryId { get; set; }
    public CashLedgerEntry? ReversedEntry { get; set; }

    /// <summary>true = este asiento YA fue revertido (sale del indice de "vigentes"; no se cuenta dos veces).</summary>
    public bool IsReversed { get; set; }
}
