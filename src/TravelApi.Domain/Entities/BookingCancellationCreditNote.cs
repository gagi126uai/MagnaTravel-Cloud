using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-042 (2026-07-01): estado fiscal de UNA Nota de Credito dentro de una cancelacion
/// multi-factura. Una cancelacion emite una NC por cada factura de venta viva; esta hija
/// lleva el estado individual de cada NC. La completitud del BC (todas OK / parcial / todas
/// fallan) se decide contando estas hijas.
/// </summary>
public enum BookingCancellationCreditNoteStatus
{
    /// <summary>La NC se encolo (o esta por encolarse) y todavia espera el CAE de ARCA.</summary>
    Pending = 0,

    /// <summary>La NC obtuvo CAE aprobado por ARCA. Estado terminal exitoso.</summary>
    Succeeded = 1,

    /// <summary>ARCA rechazo la NC (CAE no aprobado). Reintentable via retry-credit-notes.</summary>
    Failed = 2,
}

/// <summary>
/// ADR-042 (2026-07-01): fila hija del aggregate <see cref="BookingCancellation"/>. Modela el
/// par (factura de venta a anular -> su Nota de Credito) cuando una reserva tiene 2+ facturas
/// con CAE (caso legitimo multimoneda USD+ARS). Una fila por factura.
///
/// <para><b>Por que existe</b>: el <see cref="BookingCancellation"/> nacio single-factura
/// (<see cref="BookingCancellation.OriginatingInvoiceId"/> singular). Para representar N NCs y
/// mover la decision de "reserva anulada" del "primer callback exitoso" a "todos los callbacks
/// resueltos y todos exitosos", se necesita una fila por NC con su propio estado.</para>
///
/// <para><b>Compat mono-factura</b>: en el caso de una sola factura (el 99% historico) hay una
/// unica fila hija que espeja el puntero principal del padre — comportamiento byte-equivalente.
/// El backfill de <c>Adr042_M1</c> crea esa fila para todo BC existente.</para>
///
/// <para><b>Ortogonal a <see cref="BookingCancellationLine"/></b> (ADR-025): las Lines modelan
/// "a quien le pido el reembolso" (una por servicio/operador); estas hijas modelan "que
/// comprobante fiscal anulo" (una por factura). No hay correlacion 1:1 entre ambas colecciones.</para>
/// </summary>
public class BookingCancellationCreditNote : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>FK al aggregate padre. Cascade con el BC (la hija no tiene sentido sin su cancelacion).</summary>
    public int BookingCancellationId { get; set; }
    public BookingCancellation BookingCancellation { get; set; } = null!;

    /// <summary>
    /// FK a la factura de venta (A/B/C) que anula esta NC. Es la clave por la que los callbacks de
    /// ARCA (que llegan keyeados por la factura originante) encuentran esta hija.
    /// UNIQUE junto con <see cref="BookingCancellationId"/> (no se anula dos veces la misma factura en un BC).
    /// </summary>
    public int OriginatingInvoiceId { get; set; }
    public Invoice OriginatingInvoice { get; set; } = null!;

    /// <summary>
    /// FK a la Invoice NC emitida. Null hasta que ARCA devuelve el CAE. Cuando esta seteado (Succeeded)
    /// significa "NC viva sobre esta factura" — el guard de liberacion INV-081 (§3.4) lo mira para
    /// NUNCA liberar un BC con una NC viva.
    /// </summary>
    public int? CreditNoteInvoiceId { get; set; }
    public Invoice? CreditNoteInvoice { get; set; }

    /// <summary>
    /// Denormalizado para observabilidad: <c>MonId</c> (espacio ARCA, ej. "PES"/"DOL") de la factura
    /// origen. Permite ver la moneda de cada NC sin joinear a Invoices.
    /// </summary>
    [MaxLength(3)]
    public string ArcaCurrency { get; set; } = "PES";

    public BookingCancellationCreditNoteStatus Status { get; set; } = BookingCancellationCreditNoteStatus.Pending;

    /// <summary>
    /// Mensaje que ARCA devolvio si esta NC fallo su CAE. Truncado a 1000. Es un dato interno/tecnico:
    /// el DTO lo mapea a copy amigable antes de exponerlo al usuario (gate data-exposure, §3.7).
    /// </summary>
    [MaxLength(1000)]
    public string? ArcaErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
