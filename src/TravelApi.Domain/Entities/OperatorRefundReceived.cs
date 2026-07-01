using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3, 2026-05-13): aggregate root del ingreso fisico que la
/// agencia recibe de un operador. Es un ingreso unico que puede cubrir N
/// cancelaciones (1 operador puede mandar una transferencia para 5 reservas
/// distintas) — por eso es aggregate root y NO un child de
/// <see cref="BookingCancellation"/>.
///
/// Aggregate boundary:
///  - Pertenece logicamente al <see cref="Supplier"/> que lo emite.
///  - Owns N <see cref="OperatorRefundAllocation"/> (cada una linkea este ingreso
///    contra UN BookingCancellation especifico).
///  - Tambien linkea a un <see cref="ManualCashMovement"/> (Income) para que el
///    ingreso fisico aparezca en el Libro de Caja (TreasuryService).
///
/// Invariante critico (INV-114 / chk_refund_allocated_not_exceeds):
///   <c>SUM(allocations.NetAmount) &lt;= ReceivedAmount</c> SIEMPRE.
/// La columna <c>AllocatedAmount</c> es DENORMALIZADA (cache de esa suma) para
/// poder validarse con un CHECK SQL atomico (ver ADR-002 §2.5 estrategia
/// concurrencia N:M).
/// </summary>
public class OperatorRefundReceived : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>FK al operador que envio el dinero.</summary>
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    /// <summary>Fecha real del ingreso (cuando el dinero llego, NO cuando se cargo al sistema).</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Monto total recibido. Valida CHECK con <see cref="AllocatedAmount"/>.</summary>
    public decimal ReceivedAmount { get; set; }

    /// <summary>
    /// DENORMALIZADO (ADR-002 §2.5): cache de <c>SUM(allocations.NetAmount WHERE NOT IsVoided)</c>.
    /// El service lo mantiene en sync con UPDATE atomico antes de SaveChanges.
    /// CHECK constraint en BD (<c>chk_refund_allocated_not_exceeds</c>) garantiza
    /// <c>0 &lt;= AllocatedAmount &lt;= ReceivedAmount</c> incluso bajo concurrencia
    /// (race conditions terminan con uno de los UPDATE rechazado por la BD).
    /// </summary>
    public decimal AllocatedAmount { get; set; }

    /// <summary>Metodo de pago del operador (Transfer/Cash/Cheque). Limitado por longitud para coherencia con PaymentMethod.</summary>
    [Required]
    [MaxLength(50)]
    public string Method { get; set; } = "Transfer";

    /// <summary>Referencia externa: numero de cheque, ID de transferencia, comprobante. Audit trail.</summary>
    [MaxLength(100)]
    public string? Reference { get; set; }

    /// <summary>Moneda ISO 4217 del ingreso. Soporta multimoneda (ADR-002 §2.7).</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "ARS";

    /// <summary>TC del dia en que se recibe. Tipicamente coincide con <c>FiscalSnapshot.ExchangeRateAtOperatorRefundReceipt</c> de las BC asociadas.</summary>
    public decimal ExchangeRateAtReceipt { get; set; }

    /// <summary>Cashier que registro el ingreso. Audit fiscal obligatorio.</summary>
    [Required]
    [MaxLength(450)]
    public string ReceivedByUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ReceivedByUserName { get; set; } = string.Empty;

    /// <summary>Collection de allocations contra BookingCancellations. Cero al crear, &gt;= 1 antes de operar.</summary>
    public ICollection<OperatorRefundAllocation> Allocations { get; set; } = new List<OperatorRefundAllocation>();

    /// <summary>
    /// Idempotencia (2026-07-01): llave que genera el frontend UNA vez al abrir la ficha del boton
    /// "Registrar reembolso recibido" y REUSA en cada reintento (doble clic, reintento de red, dos pestañas).
    /// La escribe SOLO el atajo <c>RecordAndAllocateAsync</c>; el flujo de 2 pasos la deja null.
    ///
    /// <para>Nullable a proposito: las filas historicas (registradas antes de esta columna) quedan en null y no
    /// participan del candado. El indice UNICO en BD esta FILTRADO a NOT NULL
    /// (<c>IX_OperatorRefundsReceived_IdempotencyKey</c>): garantiza que dos requests con la MISMA llave no
    /// puedan crear dos ingresos (uno gana el INSERT, el otro choca 23505 y se resuelve idempotentemente),
    /// sin chocar entre todas las filas viejas con null.</para>
    /// </summary>
    public Guid? IdempotencyKey { get; set; }
}
