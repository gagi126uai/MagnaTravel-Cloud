using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-041 TANDA 3 (lado proveedor, 2026-06-27): aggregate root del SALDO A FAVOR que la agencia
/// tiene CON UN OPERADOR en UNA moneda, convertido en un CREDITO CONSUMIBLE de primera clase.
/// Es el espejo de <see cref="ClientCreditEntry"/> del lado cliente.
///
/// <para><b>Por que existe</b>: antes el "saldo a favor con el operador" era SOLO un numero derivado
/// (<c>SupplierBalanceByCurrency.Balance &lt; 0</c>): no se podia APLICAR a otra reserva ni dejar
/// trazabilidad de su consumo. Esta entidad lo materializa para poder aplicarlo (con reversa) y
/// auditarlo, sin romper la verdad de caja.</para>
///
/// <para><b>Relacion con la proyeccion derivada</b>: <c>SupplierBalanceByCurrency</c> (compras
/// confirmadas - pagos) queda como SEMAFORO derivado. Este entry es la fuente AUTORITATIVA del credito
/// consumible, con tope del sobrepago. Invariante por proveedor+moneda (ver tests):
/// <c>Σ RemainingBalance == max(0, -Balance) - Σ aplicaciones netas</c>. Es decir: el pool refleja el
/// sobrepago global del operador en esa moneda, MENOS lo que ya se aplico a otras reservas. Sin
/// aplicaciones, el pool == el sobrepago (<c>-Balance</c>) — el mismo numero que el backfill de la
/// migracion siembra.</para>
///
/// <para><b>Inmutabilidad</b>: cada vez que un pago genera mas sobrepago se crea un NUEVO entry (no se
/// modifica uno existente), igual que <see cref="ClientCreditEntry"/>. La columna
/// <see cref="RemainingBalance"/> es DENORMALIZADA (cache de <c>CreditedAmount - Σ aplicaciones netas
/// contra este entry</c>) para validacion atomica con CHECK SQL
/// (<c>chk_SupplierCreditEntries_remaining_non_negative</c>: <c>0 &lt;= RemainingBalance &lt;= CreditedAmount</c>).</para>
/// </summary>
public class SupplierCreditEntry : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>FK al <see cref="Supplier"/> (operador) dueño de este saldo a favor.</summary>
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    /// <summary>
    /// Moneda del bolsillo: "ARS" o "USD" (<c>Monedas.Soportadas</c>). El saldo a favor de una moneda
    /// NUNCA compensa la deuda de otra (regla de oro: la plata no cruza ARS/USD). NOT NULL, default ARS.
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    // --- Origen del credito ---
    // Hoy SIEMPRE nace de un sobrepago a un pago del operador (SourceSupplierPaymentId). El campo del
    // reembolso tardio se deja preparado para una tanda futura, pero NO se usa todavia.

    /// <summary>
    /// SOBREPAGO: el <see cref="SupplierPayment"/> cuyo excedente genero (o agrando) este credito. Es el
    /// origen normal hoy. Nullable porque el backfill de la migracion crea entries SIN pago de origen
    /// (consolidan el sobrepago historico ya presente en <c>SupplierBalanceByCurrency.Balance</c>).
    /// </summary>
    public int? SourceSupplierPaymentId { get; set; }
    public SupplierPayment? SourceSupplierPayment { get; set; }

    /// <summary>
    /// RESERVADO para la tanda futura de "reembolso tardio del operador" (cuando el operador devuelve plata
    /// despues de cerrado el circuito). HOY NO SE USA (siempre null). Se deja la columna para no migrar dos
    /// veces. No tiene FK fisica configurada todavia para no acoplar con el modulo de refunds en esta tanda.
    /// </summary>
    public int? SourceOperatorRefundReceivedId { get; set; }

    /// <summary>
    /// Saldo acreditado (el excedente que genero el entry). NO es estrictamente inmutable: si el sobrepago de
    /// origen se REDUCE (se edita/borra el pago que lo genero), el reconciler baja <see cref="CreditedAmount"/>
    /// y <see cref="RemainingBalance"/> EN LOCKSTEP (el mismo monto), para que el pool siga reflejando el
    /// sobrepago real y se preserve el CHECK <c>RemainingBalance &lt;= CreditedAmount</c>. Lo que NUNCA se toca es
    /// el credito ya consumido por aplicaciones: si el drenaje requeriria tocarlo, la edicion/baja se bloquea.
    /// </summary>
    public decimal CreditedAmount { get; set; }

    /// <summary>
    /// DENORMALIZADO: <c>CreditedAmount - Σ aplicaciones netas (Applied - Reversed) contra este entry</c>.
    /// El service lo mantiene en sync atomicamente. CHECK SQL previene aplicar mas de lo disponible bajo
    /// concurrencia. Es el credito DISPONIBLE para aplicar (NUNCA se calcula <c>max(0,-Balance)</c> al vuelo).
    /// </summary>
    public decimal RemainingBalance { get; set; }

    /// <summary>Flag "todo consumido" (RemainingBalance == 0). Util para listar saldos activos sin agregacion.</summary>
    public bool IsFullyConsumed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(450)]
    public string? CreatedByUserId { get; set; }

    [MaxLength(200)]
    public string? CreatedByUserName { get; set; }

    /// <summary>Aplicaciones (consumos) de este credito a reservas destino, con sus reversas. Child 1:N.</summary>
    public ICollection<SupplierCreditApplication> Applications { get; set; } = new List<SupplierCreditApplication>();
}
