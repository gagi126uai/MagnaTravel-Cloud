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

    /// <summary>
    /// CAMBIO 3 (2026-06-24): moneda en la que el operador RETUVO la multa, en espacio <b>ISO 4217 puro</b>
    /// (USD/ARS). Al operador se le paga en USD, asi que la penalidad puede ser USD o ARS — no necesariamente
    /// la misma que <see cref="Currency"/> (la del servicio). Es SOLO registro de la verdad de lo que retuvo el
    /// operador. Default = moneda de la linea (<see cref="Currency"/>) via backfill de la migracion y default
    /// explicito al confirmar la penalidad.
    ///
    /// <para><b>OJO — NO confundir con <see cref="BookingCancellation.PenaltyCurrencyAtEvent"/></b> (el campo
    /// del BC padre): aquel es la moneda en la que se EMITE la Nota de Debito al cliente y vive en el espacio
    /// ARCA hibrido (ARS/DOL, derivado del MonId de la factura). Este (<c>PenaltyCurrency</c>) es ISO puro y
    /// solo describe lo que retuvo el operador. Son conceptos y espacios de codigos DISTINTOS: <b>NO cablear
    /// esta moneda a la emision/FX de la ND sin un mapper ISO->ARCA</b> (ej. USD->DOL) — y ese wire requiere
    /// firma del contador (follow-up).</para>
    /// </summary>
    [MaxLength(3)]
    public string? PenaltyCurrency { get; set; }

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

    // ===== ADR-044 T2 (Addendum, 2026-07-10): cargos tipificados del operador + snapshot de su modo de facturacion =====

    /// <summary>
    /// Snapshot del <see cref="Domain.Entities.SupplierInvoicingMode"/> del operador de ESTA linea, congelado UNA
    /// vez al CONSTRUIR la linea (mismo momento en que hoy se fija el default de <see cref="ConceptKind"/>),
    /// copiando <c>Supplier.InvoicingMode</c> vigente en ese instante. Null para lineas legacy previas a esta
    /// tanda (nunca se llego a poblar el campo equivalente del padre en produccion).
    ///
    /// <para><b>Por que existe (Decision A del Addendum)</b>: el campo equivalente del padre
    /// (<see cref="FiscalSnapshot.InvoicingModeAtEvent"/>) nunca se llego a poblar en produccion — el sistema real
    /// SIEMPRE lee el modo VIVO del <see cref="Supplier"/> (mismo patron de fallback que
    /// <c>FiscalLiquidationCalculator.cs:61</c>). Toda lectura del gate por modo de facturacion usa
    /// <c>SupplierInvoicingModeAtEvent ?? Supplier.InvoicingMode</c>: el snapshot evita que un cambio de modo del
    /// proveedor DESPUES de mover plata real sobre esta linea reinterprete el extracto historico en silencio.</para>
    ///
    /// <para><b>Cero regresion</b>: como el campo del padre nunca estuvo poblado, toda linea existente arranca en
    /// null y cae al fallback vivo -> comportamiento identico al de hoy. Sin backfill obligatorio.</para>
    /// </summary>
    public SupplierInvoicingMode? SupplierInvoicingModeAtEvent { get; set; }

    /// <summary>
    /// Cargos tipificados del operador sobre esta linea (ADR-044 T2 Addendum, Decision B). Una linea puede tener
    /// VARIOS cargos simultaneos de distinta naturaleza fiscal (ej. un cargo administrativo Y una retencion fiscal
    /// del mismo operador en la misma cancelacion — caso confirmado real por el contador, no hipotetico).
    /// </summary>
    public ICollection<BookingCancellationLineOperatorCharge> OperatorCharges { get; set; }
        = new List<BookingCancellationLineOperatorCharge>();

    /// <summary>
    /// ADR-044 T2 (Addendum, Decision B1, 2026-07-10): eje CAJA/RefundCap = SUM de los
    /// <see cref="OperatorCharges"/> de esta linea con <c>Kind != Withholding AND CollectionMode == Retenida</c>.
    /// Es lo UNICO que resta de <see cref="RefundCap"/>: <c>Withholding</c> es credito fiscal de la agencia (no
    /// resta, nunca es perdida real) y <c>FacturadaAparte</c> tampoco resta (el operador devuelve el bruto; ese
    /// cargo se cobra por la cuenta a pagar del circuito ADR-041, no descontando el reembolso).
    ///
    /// <para><b>Columna FISICA persistida</b> (NO calculada sobre <see cref="OperatorCharges"/> en lectura): se
    /// reescribe SOLO dentro de la misma transaccion que crea o modifica cargos de esta linea (el mismo metodo de
    /// servicio que hoy escribe <see cref="PenaltyAmount"/>). Un recalculo perezoso por lectura pondria en 0
    /// multas confirmadas historicas en silencio si el <c>Include</c> de <see cref="OperatorCharges"/> faltara.</para>
    ///
    /// <para><b>Invariante que preserva</b>: <c>RefundCap + RetainedDeductionAmount == capBeforePenalty</c> (el
    /// tope de reembolso ANTES de netear la multa), exacta, incluso con cargos mixtos (Retenida + FacturadaAparte,
    /// o Retenida + Withholding) sobre la misma linea.</para>
    ///
    /// <para><b>Backfill legacy (migracion T2c)</b>: para lineas <c>PenaltyStatus=Confirmed</c> con
    /// <c>PenaltyAmount</c> cargado ANTES de esta tanda, <c>RetainedDeductionAmount = PenaltyAmount</c>: toda
    /// multa confirmada antes de T2 era, sin excepcion, un cargo administrativo retenido (el UNICO camino que
    /// existia), asi que los dos agregados coinciden exactamente para el historico.</para>
    /// </summary>
    public decimal RetainedDeductionAmount { get; set; }

    // ===== ADR-044 T5 (Addendum, Decision B, 2026-07-11): anulacion PARCIAL — a que factura y por cuanto =====

    /// <summary>
    /// A que FACTURA DE VENTA de la reserva le corresponde el credito (NC) de ESTE servicio cancelado. Mismo
    /// patron que <see cref="BookingCancellationLineOperatorCharge.TargetInvoiceId"/> (T3b Decision 1), pero
    /// aplicado al lado NC/credito de la linea (no al cargo del operador): con 1 sola factura activa se
    /// autocompleta sin fricción; con 2+ facturas activas, el vendedor la elige al confirmar (sin eleccion,
    /// la linea no participa de emision automatica — cae a revision manual).
    ///
    /// <para><b>Null</b> = todavia no resuelto (lineas legacy previas a esta tanda, o casos ambiguos sin
    /// eleccion). Sin backfill: cero regresion, cae al mismo fallback de revision manual que existia antes.</para>
    /// </summary>
    public int? TargetInvoiceId { get; set; }
    public Invoice? TargetInvoice { get; set; }

    /// <summary>
    /// Monto BRUTO (sin netear la multa, criterio matriculado 2026-06-01) que el vendedor confirmo para el
    /// credito de este servicio contra <see cref="TargetInvoiceId"/>. Se PROPONE con
    /// <see cref="LineSaleAmount"/> (default, cero friccion visual) pero se CONFIRMA por el vendedor: no existe
    /// ninguna tabla servicio-a-renglon-de-factura (verificado T3b Decision 1), asi que <c>LineSaleAmount</c>
    /// es lo que el SERVICIO vale en el sistema, no necesariamente lo que la factura (armada 100% manual) le
    /// asigno si comparte comprobante con otros servicios.
    ///
    /// <para>Comparado contra el remanente vivo de <see cref="TargetInvoiceId"/> decide, SIN pedir un segundo
    /// dato al vendedor: monto == remanente -> NC total de esa factura (reusa el circuito de siempre); monto
    /// &lt; remanente -> NC parcial (<c>CreditNoteKind.PartialOnOriginal</c>); monto &gt; remanente -> se
    /// rechaza, no se persiste (excederia lo que la factura vale).</para>
    /// </summary>
    public decimal? ConfirmedGrossCreditAmount { get; set; }

    [MaxLength(450)]
    public string? CreditAmountConfirmedByUserId { get; set; }

    [MaxLength(200)]
    public string? CreditAmountConfirmedByUserName { get; set; }

    public DateTime? CreditAmountConfirmedAt { get; set; }
}
