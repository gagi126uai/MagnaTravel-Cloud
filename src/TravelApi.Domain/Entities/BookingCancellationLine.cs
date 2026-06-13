using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-025 (DT.1.1, 2026-06-13): linea hija de un <see cref="BookingCancellation"/>.
/// Una linea = un servicio cancelado, con SU operador, SU moneda, SU penalidad y SU
/// circuito de reintegro.
///
/// <para><b>Por que existe</b> (modelo BC-padre + lineas hijas, heredado de ADR-015
/// §2): el cliente le compra a la AGENCIA, asi que la cara fiscal hacia el cliente
/// es UNICA por reserva (1 factura -> 1 NC, vive en el padre). Pero por DEBAJO una
/// reserva puede tener varios operadores, cada uno con su penalidad, su politica de
/// refund y su moneda. Esa multiplicidad vive aca, en lineas, NO en multiples
/// cancelaciones por reserva (eso romperia INV-081 y fragmentaria la NC).</para>
///
/// <para><b>Que habilita</b>: (1) cancelar UN servicio dejando el resto del file vivo
/// (<see cref="Scope"/> = Partial); (2) cancelar un file con VARIOS operadores
/// (N lineas Full), levantando el viejo bloqueo INV-152.</para>
///
/// <para><b>Anclas que NO se mueven</b>: lo fiscal hacia el cliente (factura/NC/ND
/// agregada) sigue en el padre; lo de operador/refund/penalidad/pago a proveedor es
/// nivel linea. Ver tambien <see cref="BookingCancellation"/>.</para>
///
/// <para><b>Concurrencia</b>: <c>UseXminAsConcurrencyToken()</c> igual que el padre
/// (cancelar un servicio y editar la reserva en paralelo -> 409 controlado).</para>
/// </summary>
public class BookingCancellationLine : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // ===== Padre (aggregate root del evento fiscal) =====

    /// <summary>FK al <see cref="BookingCancellation"/> padre. OnDelete Cascade (las lineas no sobreviven a su BC).</summary>
    public int BookingCancellationId { get; set; }
    public BookingCancellation BookingCancellation { get; set; } = null!;

    // ===== Operador de ESTA linea (nivel linea, no evento) =====

    /// <summary>FK al operador de ESTE servicio cancelado. Cada linea puede ser de un operador distinto.</summary>
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    // ===== Referencia al servicio cancelado: (tabla, id) (ADR-015 §6.3 opcion a) =====

    /// <summary>En que tabla vive el servicio cancelado. Ver <see cref="CancellableServiceTable"/>.</summary>
    public CancellableServiceTable ServiceTable { get; set; }

    /// <summary>
    /// Id (int) del servicio en su tabla. <b>0 es el centinela de backfill</b> (ADR-025 DT.1.3 / M1):
    /// una linea historica con <see cref="ServiceTable"/>=Generic y ServiceId=0 NO apunta a un servicio
    /// puntual (el BC viejo cancelaba la reserva entera). Las lineas nuevas siempre referencian un servicio
    /// real (ServiceId &gt; 0). NO hay FK a nivel BD: la integridad la cuida la regla de negocio
    /// (un servicio con linea de cancelacion no se borra, se cancela; espejo de borrar-vs-cancelar ADR-020).
    /// </summary>
    public int ServiceId { get; set; }

    // ===== Alcance y montos (por moneda de la linea) =====

    public BookingCancellationLineScope Scope { get; set; }

    /// <summary>Moneda del servicio cancelado (HotelBooking.Currency etc.). El saldo y la deuda son por moneda.</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>SalePrice del servicio cancelado, congelado al armar la linea (auditoria del monto cancelado).</summary>
    public decimal LineSaleAmount { get; set; }

    // ===== Penalidad por operador (baja ADR-013/014 a nivel linea) =====

    /// <summary>
    /// Naturaleza fiscal de la penalidad de ESTA linea. Default
    /// <see cref="CancellationConceptKind.OperatorPenaltyPassThrough"/> (conservador = sin ND propia).
    /// Un operador puede ser pass-through y otro de la misma reserva tener cargo propio de la agencia.
    /// </summary>
    public CancellationConceptKind ConceptKind { get; set; } = CancellationConceptKind.OperatorPenaltyPassThrough;

    /// <summary>Estado de la penalidad de la linea. Solo Confirmed habilita emitir ND (ADR-013/014). Default Estimated.</summary>
    public PenaltyStatus PenaltyStatus { get; set; } = PenaltyStatus.Estimated;

    /// <summary>Monto de la penalidad. Null mientras no haya penalidad confirmada.</summary>
    public decimal? PenaltyAmount { get; set; }

    /// <summary>Momento (sistema) en que se confirmo la penalidad de la linea.</summary>
    public DateTime? PenaltyConfirmedAt { get; set; }

    /// <summary>
    /// Fecha REAL en que el operador confirmo el monto (eje fiscal del plazo, ADR-014). Distinta de
    /// <see cref="PenaltyConfirmedAt"/> (timestamp del acto en el sistema).
    /// </summary>
    public DateTime? OperatorPenaltyConfirmedDate { get; set; }

    [MaxLength(450)]
    public string? ConceptClassifiedByUserId { get; set; }

    [MaxLength(200)]
    public string? ConceptClassifiedByUserName { get; set; }

    public DateTime? ConceptClassifiedAt { get; set; }

    // ===== ND propia de ESTA linea (solo si ConceptKind = cargo propio; Q-F1 default "una por cargo") =====

    /// <summary>
    /// FK a la Invoice ND de ESTA linea. Es el guard de idempotencia por linea: si ya tiene valor, NO se
    /// crea otra ND. Null mientras no haya ND. <b>La emision automatica NO se construye</b> (decision #3 /
    /// firma Q-F1 pendiente): este campo se setea solo cuando se conecte la emision manual.
    /// </summary>
    public int? DebitNoteInvoiceId { get; set; }
    public Invoice? DebitNoteInvoice { get; set; }

    /// <summary>Estado observable de la ND de la linea. Default NotApplicable (sin ND).</summary>
    public DebitNoteStatus DebitNoteStatus { get; set; } = DebitNoteStatus.NotApplicable;

    [MaxLength(1000)]
    public string? DebitNoteArcaErrorMessage { get; set; }

    // ===== Refund del operador de ESTA linea (baja ADR-002/INV-126 a nivel linea) =====

    /// <summary>
    /// Tope del reintegro de ESTA linea = lo pagado a este operador menos su penalidad. Para imputacion,
    /// el cap se opera AGREGADO POR OPERADOR (suma de las lineas del mismo SupplierId), nunca por linea
    /// individual (B2 / decision #2 de Gaston: el operador devuelve un monto, no "por servicio").
    /// </summary>
    public decimal RefundCap { get; set; }

    /// <summary>Lo efectivamente recibido del operador imputado a esta linea (SUM de allocations no-voided del operador).</summary>
    public decimal ReceivedRefundAmount { get; set; }

    public BookingCancellationLineRefundStatus RefundStatus { get; set; } = BookingCancellationLineRefundStatus.None;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
