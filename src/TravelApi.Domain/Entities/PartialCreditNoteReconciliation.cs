using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 Fase 3 (ADR-010, 2026-05-29): un "caso" de la bandeja de reconciliacion
/// de NC parciales con recibos vivos. Es la fuente de verdad CONSULTABLE del
/// residuo operativo que dejo la decision F2.3 "no cascade-void receipts".
///
/// <para><b>El problema que resuelve (ejemplo de kiosco)</b>: el cliente paga una
/// factura de $1.000 en tres recibos ($300 + $300 + $400). Despues cancela parte
/// y el sistema emite una nota de credito parcial de $750. No hay UN recibo de
/// $750, asi que el sistema deja los tres recibos "abiertos" (vivos) y anota que
/// alguien tiene que ordenarlos. Esta entidad es ese "anote" hecho dato
/// consultable: una fila por cada NC parcial que dejo recibos sin anular.</para>
///
/// <para><b>Por que una tabla propia y no un flag sobre Invoice/BookingCancellation</b>:
/// el ciclo de vida fiscal (emitir la NC) cierra en Fase 2. El ciclo operativo
/// (acomodar recibos) recien abre en Fase 3. Son dos vidas distintas. Ademas el
/// audit append-only no sirve como fuente operativa porque el estado
/// Pendiente -> Resuelto es MUTABLE. Ver ADR-010 §3.1.</para>
///
/// <para><b>Solo casos NUEVOS</b> (decision D3): la tabla arranca vacia. No hay
/// backfill de NC parciales historicas. El caso lo crea el mismo punto que hoy
/// escribe el audit no-cascade (<c>AfipService.ApplyPartialCreditNoteReversalAsync</c>),
/// dentro del MISMO SaveChanges que el Payment reversal — asi nunca queda un
/// reversal aplicado con recibos vivos pero SIN caso en la bandeja (ver ADR-010 §3.3, B1).</para>
///
/// <para><b>Concurrency</b>: <c>UseXminAsConcurrencyToken()</c>. Si dos encargados
/// intentan cerrar el mismo caso a la vez, uno recibe <c>DbUpdateConcurrencyException</c>
/// y el endpoint devuelve 409.</para>
/// </summary>
public class PartialCreditNoteReconciliation : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // ===== Vinculos fiscales (de donde salio el caso) =====

    /// <summary>
    /// FK a la NC parcial emitida (la <c>Invoice</c> que disparo el caso). UNIQUE en BD:
    /// un caso por NC. Ademas de modelar la relacion, ese indice unico es la red de
    /// defensa de idempotencia (B2): si el job de reversal reintenta, el segundo intento
    /// choca contra el indice. La idempotencia primaria igual la garantiza el guard del
    /// wrapper <c>ApplyCreditNoteEconomicReversalAsync</c> (no re-aplica el reversal).
    /// </summary>
    public int CreditNoteInvoiceId { get; set; }
    public Invoice CreditNoteInvoice { get; set; } = null!;

    /// <summary>
    /// FK a la factura ORIGINAL (la que se cancelo parcialmente). De ahi salen los
    /// recibos vivos. Restrict en BD (evidencia fiscal, no se borra en cascada).
    /// </summary>
    public int OriginalInvoiceId { get; set; }
    public Invoice OriginalInvoice { get; set; } = null!;

    /// <summary>
    /// FK opcional a la reserva. Sirve de contexto para que el encargado ubique el caso
    /// ("¿de que viaje es esto?"). Nullable porque una NC podria no tener reserva atada
    /// en escenarios viejos.
    /// </summary>
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    // ===== Snapshot economico al abrir =====

    /// <summary>
    /// Monto fiscal acreditado por la NC parcial (el <c>ImporteTotal</c> de la NC, en
    /// positivo). Es informativo para la UI: "esta NC acredito $750". NO es el monto a
    /// devolver al cliente (eso lo decide caja/cta cte, la bandeja solo avisa, D2).
    /// </summary>
    public decimal FiscalAmountCredited { get; set; }

    /// <summary>Moneda del caso (ISO: ARS/USD). Hoy siempre ARS (multimoneda llega en F2.5).</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = "ARS";

    // ===== Estado + apertura =====

    public PartialCreditNoteReconciliationStatus Status { get; set; }
        = PartialCreditNoteReconciliationStatus.Pending;

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Quien disparo la cancelacion que abrio el caso. Sale de
    /// <c>OriginalInvoice.AnnulledByUserId ?? "system"</c>.
    ///
    /// <para><b>OJO (ADR-010 N3)</b>: si la cancelacion la disparo un job/proceso
    /// automatico, este valor es "system". Como ningun usuario real tiene userId
    /// "system", el chequeo de self-close NUNCA matchea para esos casos -> cualquier
    /// encargado puede cerrarlos sin pedir bypass de 4-ojos. Es el comportamiento
    /// esperado: no hay una "persona que abrio" a la cual exigirle 4-ojos.</para>
    /// </summary>
    [MaxLength(450)]
    public string OpenedByUserId { get; set; } = "system";

    [MaxLength(200)]
    public string? OpenedByUserName { get; set; }

    // ===== Cierre manual (4-ojos) =====

    public DateTime? ResolvedAt { get; set; }

    [MaxLength(450)]
    public string? ResolvedByUserId { get; set; }

    [MaxLength(200)]
    public string? ResolvedByUserName { get; set; }

    /// <summary>
    /// Motivo del cierre. Obligatorio cuando se cierra con recibos todavia vivos (R4) o
    /// cuando se uso el bypass de admin unico (G5, exige >= 100 chars). Opcional en el
    /// cierre limpio "otra persona cierra y todos los recibos ya estan anulados".
    /// </summary>
    [MaxLength(1000)]
    public string? ResolutionNotes { get; set; }

    /// <summary>
    /// FC1.3 Fase 3 (ADR-010 R4): el caso se cerro con al menos un recibo todavia
    /// <c>Issued</c> (plata potencialmente no devuelta). Se deja trazable: la bandeja
    /// NO obliga a anular (decision D2), pero si obliga a justificar (ResolutionNotes).
    /// </summary>
    public bool ClosedWithLiveReceipts { get; set; }

    /// <summary>
    /// El cierre uso el bypass de 4-ojos para agencia de un solo admin (G5/GR-005):
    /// el mismo que abrio el caso lo cerro, justificado con >= 100 chars, siendo el
    /// unico admin activo. Mismo patron que <c>BookingCancellation</c>.
    /// </summary>
    public bool FourEyesBypassApplied { get; set; }

    // ===== Hijas: snapshot de los recibos vivos al abrir =====

    /// <summary>
    /// Foto de los recibos que estaban vivos cuando se abrio el caso. El estado VIGENTE
    /// de cada recibo NO se lee de aca (es snapshot historico) sino en vivo de
    /// <c>PaymentReceipts.Status</c> al listar la bandeja. Asi la UI puede mostrar
    /// "2 de 3 recibos ya anulados" aunque el snapshot diga que los 3 estaban Issued.
    /// </summary>
    public List<PartialCreditNoteReconciliationReceipt> Receipts { get; set; } = new();
}
