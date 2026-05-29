using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): fila hija del caso de reconciliacion. Es el
/// SNAPSHOT de un recibo de pago que estaba vivo (Issued) cuando se abrio el caso.
///
/// <para><b>Por que snapshot Y lectura en vivo (las dos cosas)</b>:
/// <list type="bullet">
///   <item>El <b>snapshot</b> (esta fila) deja constancia historica: "cuando abrimos
///   el caso, estos eran los N recibos vivos y estaban en este estado". Auditable.</item>
///   <item>El <b>estado vigente</b> del recibo NO se guarda aca — se lee en vivo de
///   <c>PaymentReceipts.Status</c> al listar la bandeja, cruzando por
///   <see cref="PaymentReceiptId"/>. Asi, si el encargado anula un recibo desde la
///   pantalla de pagos, la bandeja lo refleja sin tener que actualizar esta fila.</item>
/// </list>
/// La fuente de verdad del estado del recibo es SIEMPRE <c>PaymentReceipt</c>. Esta
/// tabla es puntero + foto historica, NO duplica el ciclo de vida del recibo.</para>
///
/// <para>Se borra en cascada si se borra el caso padre (escenario de test, no operativo:
/// los casos no se borran en produccion, se cierran).</para>
/// </summary>
public class PartialCreditNoteReconciliationReceipt
{
    public int Id { get; set; }

    /// <summary>FK al caso padre. ON DELETE CASCADE.</summary>
    public int ReconciliationId { get; set; }
    public PartialCreditNoteReconciliation Reconciliation { get; set; } = null!;

    /// <summary>FK al recibo vivo (uno de los liveReceiptIds del momento del evento).</summary>
    public int PaymentReceiptId { get; set; }
    public PaymentReceipt PaymentReceipt { get; set; } = null!;

    /// <summary>
    /// FK al Payment del recibo. Se persiste porque el endpoint de anular recibo
    /// (<c>POST /api/payments/{paymentPublicId}/receipt/void</c>) resuelve por Payment,
    /// no por receipt (ADR-010 N1). Asi el DTO puede exponer el PublicId del Payment
    /// sin un join extra al listar.
    /// </summary>
    public int PaymentId { get; set; }

    /// <summary>Monto del recibo al momento del snapshot.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Estado del recibo cuando se abrio el caso. En la practica siempre "Issued"
    /// (solo snapshotamos recibos vivos). Se persiste igual para dejar la foto completa.
    /// </summary>
    [MaxLength(30)]
    public string StatusAtOpen { get; set; } = PaymentReceiptStatuses.Issued;
}
