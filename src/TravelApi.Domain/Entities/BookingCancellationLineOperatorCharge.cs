using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 T2 (Addendum, 2026-07-10): UN cargo puntual que el OPERADOR aplica sobre una linea de cancelacion
/// (<see cref="BookingCancellationLine"/>). Tabla hija 1:N (no un campo escalar en la linea): el contador
/// confirmo que un operador Responsable Inscripto puede aplicar, en la MISMA cancelacion, un cargo
/// administrativo Y una retencion fiscal SIMULTANEOS, y esos dos montos NUNCA deben mezclarse en un solo
/// numero (uno es perdida real de la agencia, el otro es credito fiscal — confundirlos violaria la regla del
/// contador). Con un campo escalar por linea, esos dos montos de distinta naturaleza fiscal quedarian
/// forzados a un solo <see cref="OperatorChargeKind"/>: por eso la tabla hija.
///
/// <para><b>Rol de cada agregado derivado en la linea</b> (ver <see cref="BookingCancellationLine"/>):
/// <see cref="BookingCancellationLine.PenaltyAmount"/> es el eje CLIENTE (lo que eventualmente se traslada
/// via Nota de Debito, SUM de cargos con <c>Kind != Withholding</c>); <see cref="BookingCancellationLine.RetainedDeductionAmount"/>
/// es el eje CAJA (lo UNICO que resta del <see cref="BookingCancellationLine.RefundCap"/>, SUM de cargos
/// <c>Kind != Withholding AND CollectionMode == Retenida</c>). Ambos son columnas FISICAS de la linea,
/// reescritas SOLO en la misma transaccion que crea/modifica cargos de esa linea (nunca se recalculan por
/// lectura: un <c>Include</c> faltante pondria en 0 multas confirmadas historicas en silencio).</para>
///
/// <para><b>Caso simple (2 clics, sin friccion)</b>: al confirmar la multa del operador con el flujo de
/// siempre (monto + moneda + concepto), el servicio crea UNA charge <c>Kind=AdministrativeFee</c>
/// <c>CollectionMode=Retenida</c> por detras, transparente para el usuario. "Agregar otro cargo de este
/// operador" (ej. una retencion fiscal ademas del cargo) es una accion SECUNDARIA y OPCIONAL: no se muestra
/// ni se pregunta por default (regla del dueño: la complejidad se esconde con defaults).</para>
/// </summary>
public class BookingCancellationLineOperatorCharge : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    /// <summary>FK a la linea de cancelacion duena de este cargo. OnDelete Cascade (el cargo no tiene sentido sin su linea).</summary>
    public int BookingCancellationLineId { get; set; }
    public BookingCancellationLine BookingCancellationLine { get; set; } = null!;

    /// <summary>Naturaleza fiscal del cargo. Default <see cref="OperatorChargeKind.AdministrativeFee"/> = comportamiento legacy.</summary>
    public OperatorChargeKind Kind { get; set; } = OperatorChargeKind.AdministrativeFee;

    /// <summary>Como lo efectiviza el operador. Default <see cref="PenaltyCollectionMode.Retenida"/> = comportamiento legacy.</summary>
    public PenaltyCollectionMode CollectionMode { get; set; } = PenaltyCollectionMode.Retenida;

    public decimal Amount { get; set; }

    /// <summary>
    /// Moneda ISO 4217 del cargo. INVARIANTE DURA (B2 del Addendum): SIEMPRE igual a la moneda de
    /// <see cref="BookingCancellationLine"/> (<c>Line.Currency</c>) — nunca ARS+USD mezclados dentro de la
    /// misma linea. Un CHECK SQL no puede cruzar tablas en Postgres, asi que esto se valida en el SERVICIO al
    /// escribir (mismo punto que crea el cargo), no en un CHECK de base. Un charge <c>Retenida</c> DEBE estar
    /// en la moneda de la linea porque <c>RetainedDeductionAmount</c> se resta de <c>RefundCap</c>, que esta
    /// en esa misma moneda: restar USD de un cap en ARS mezclaria unidades.
    /// </summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>
    /// Referencia al documento del proveedor (numero de factura/ND del operador). CHECK SQL: obligatoria
    /// cuando <see cref="CollectionMode"/> = <see cref="PenaltyCollectionMode.FacturadaAparte"/> (esa forma de
    /// cobro exige el documento del operador; <c>Retenida</c> no lo requiere).
    /// </summary>
    [MaxLength(200)]
    public string? DocumentRef { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>
    /// ADR-044 T3a (2026-07-10): como se traslada ESTE cargo al cliente en la Nota de Debito. Default
    /// <see cref="Entities.ClientTransferMode.AsIs"/> = comportamiento de siempre (el cargo automatico que crea
    /// el confirm de la multa nace con este valor, transparente para el usuario). Ver
    /// <see cref="Entities.ClientTransferMode"/> para el detalle de cada valor.
    /// </summary>
    public ClientTransferMode ClientTransferMode { get; set; } = ClientTransferMode.AsIs;

    /// <summary>
    /// ADR-044 T3a (2026-07-10): monto ADICIONAL del cargo de gestion propio de la agencia, SOLO usado cuando
    /// <see cref="ClientTransferMode"/> = <see cref="Entities.ClientTransferMode.WithManagementFee"/>. Sale como
    /// renglon APARTE en la misma Nota de Debito (no reemplaza el monto de <see cref="Amount"/>, se SUMA en un
    /// renglon propio). CHECK SQL: obligatorio (y mayor a cero) cuando el modo es WithManagementFee; debe quedar
    /// vacio en cualquier otro modo (evita un monto "fantasma" que nadie factura).
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(18,2)")]
    public decimal? ManagementFeeAmount { get; set; }

    /// <summary>Auditoria: quien confirmo/cargo este cargo (mismo patron que el resto del modulo).</summary>
    [MaxLength(450)]
    public string ConfirmedByUserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? ConfirmedByUserName { get; set; }

    public DateTime ConfirmedAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ====================================================================================
    // ADR-044 T3b Decision 1 (2026-07-10): a que FACTURA DE VENTA del cliente se traslada
    // este cargo. Con 1 sola factura activa de la reserva (el 95%+ de los casos) se
    // autocompleta transparente; con 2+ facturas activas (ADR-042, ej. USD+ARS) el humano
    // tiene que elegir CUAL porque no hay ningun dato mecanico que lo infiera (ver el
    // XML-doc del Addendum: InvoiceItem.SourceServicioReservaId existe pero nunca se puebla
    // en produccion). Sin eleccion, el cargo NO se factura solo (revision manual).
    // ====================================================================================

    /// <summary>
    /// FK a la <see cref="Entities.Invoice"/> de venta a la que se traslada este cargo. Autocompletada
    /// cuando la reserva tiene 1 sola factura activa; con 2+ requiere que un humano la elija
    /// (<c>SetOperatorChargeTargetInvoiceAsync</c>). <c>null</c> = todavia sin resolver -&gt; el motor de
    /// emision de la Nota de Debito rutea a revision manual (nunca adivina).
    ///
    /// <para><b>M2 (invariante dura)</b>: todos los cargos trasladables al cliente (<c>Kind != Withholding</c>)
    /// de la MISMA <see cref="BookingCancellationLine"/> tienen que apuntar a la MISMA factura — una linea es
    /// un servicio, y un servicio vive en UNA sola factura del cliente. Los <c>Withholding</c> quedan exentos
    /// (nunca llegan al cliente, no forman renglon de ND). Se valida en el SERVICIO al escribir, no en un
    /// CHECK SQL (cruzaria filas de la misma tabla agrupadas por linea).</para>
    /// </summary>
    public int? TargetInvoiceId { get; set; }
    public Invoice? TargetInvoice { get; set; }

    // ====================================================================================
    // ADR-044 T3b Decision 2 (2026-07-10): conversion de moneda cuando este cargo esta en una
    // moneda DISTINTA a la de su factura destino (ej. cargo en USD, factura del cliente en ARS).
    // NO es lo mismo que "el TC del comprobante" (ese sigue SIEMPRE congelado de la factura
    // original, regla firmada e inamovible): esto convierte el MONTO EMBEBIDO del cargo para
    // que entre como renglon en pesos de esa ND. Mismo patron tipado que Payment.ExchangeRate*
    // (ADR-021), replicado sin inventar vocabulario nuevo.
    //
    // Dos juegos de 4 campos: ESTIMADO (el TC del DIA DEL CARGO del operador, cargado al confirmar/cargar el
    // cargo) y DEFINITIVO (el que viaja al renglon de la ND). M1 lectura (i) CONFIRMADA por Gaston 2026-07-10:
    // el TC definitivo ES el del dia del cargo del operador — NO el del dia de emision de la ND. La "promocion"
    // estimado->definitivo (al emitir) copia el VALOR y la FECHA originales del estimado; no se recotiza al
    // momento de emitir. El estimado NUNCA se pisa: queda como rastro del dato con que se cargo el cargo.
    // ====================================================================================

    /// <summary>
    /// TC ESTIMADO (preview, no fiscal) para convertir <see cref="Amount"/> a la moneda de la factura
    /// destino. Convencion FIJA: unidades de ARS por 1 USD (misma que <c>Payment.ExchangeRate</c>).
    /// <c>null</c> cuando <see cref="Currency"/> coincide con la moneda de la factura destino (no hay
    /// conversion que hacer: caso simple T3a, sin cambios).
    /// </summary>
    [Column(TypeName = "numeric(18,6)")]
    public decimal? EstimatedExchangeRateToClientInvoiceCurrency { get; set; }

    /// <summary>Origen del TC estimado. <c>null</c> si no hubo conversion prevista.</summary>
    public ExchangeRateSource? EstimatedExchangeRateSource { get; set; }

    /// <summary>Fecha en que se tomo el TC estimado. <c>null</c> si no hubo conversion prevista.</summary>
    public DateTime? EstimatedExchangeRateAt { get; set; }

    /// <summary>
    /// Justificacion obligatoria cuando <see cref="EstimatedExchangeRateSource"/> = <see cref="ExchangeRateSource.Manual"/>
    /// (mismo criterio INV-120 que rige toda factura en moneda extranjera del sistema).
    /// </summary>
    [MaxLength(500)]
    public string? EstimatedExchangeRateJustification { get; set; }

    /// <summary>
    /// TC DEFINITIVO del cargo = TC del DIA DEL CARGO del operador (M1 lectura (i), CONFIRMADO por Gaston
    /// 2026-07-10). Se fija AL EMITIR la Nota de Debito copiando el VALOR del estimado (que ya es el TC del dia
    /// del cargo) — NO se recotiza al dia de emision. Es el valor que viaja a auditoria y al calculo del ajuste
    /// de diferencia de cambio de tesoreria (Decision 3). Vive en CAMPOS PROPIOS del cargo (no en la
    /// <see cref="Entities.Invoice"/>: una ND en ARS con un cargo embebido en USD no tiene donde guardar ESA
    /// conversion en la factura, que describe la valuacion del COMPROBANTE, no de un renglon). Convencion FIJA:
    /// unidades de ARS por 1 USD. (El nombre <c>...AtNdEmission</c> refiere a CUANDO se persiste — al emitir la
    /// ND —, no a la fecha de referencia del TC, que es la del dia del cargo.)
    /// </summary>
    [Column(TypeName = "numeric(18,6)")]
    public decimal? DefinitiveExchangeRateAtNdEmission { get; set; }

    /// <summary>Origen del TC definitivo. <c>null</c> si el cargo nunca necesito conversion (misma moneda que su ND).</summary>
    public ExchangeRateSource? DefinitiveExchangeRateSource { get; set; }

    /// <summary>Fecha de referencia del TC definitivo = FECHA DEL CARGO del operador (copiada del estimado, M1 (i)), NO la de emision. <c>null</c> si no hubo conversion.</summary>
    public DateTime? DefinitiveExchangeRateAt { get; set; }

    /// <summary>Justificacion obligatoria cuando el TC definitivo es <see cref="ExchangeRateSource.Manual"/> (INV-120).</summary>
    [MaxLength(500)]
    public string? DefinitiveExchangeRateJustification { get; set; }

    /// <summary>
    /// ADR-044 T3b Decision 3 (S2, 2026-07-10): si este cargo es <see cref="PenaltyCollectionMode.FacturadaAparte"/>
    /// y ya se pago al operador su documento, el <c>Id</c> del <see cref="SupplierPayment"/> que lo liquido.
    /// <c>null</c> = todavia sin liquidar. Es la RED DURA anti doble-liquidacion: un segundo pago sobre un cargo
    /// que ya tiene este campo seteado se rechaza (sin esto, se podia pagar dos veces el mismo cargo al operador
    /// y generar dos ajustes de diferencia de cambio). Se limpia (vuelve a null) al ELIMINAR ese pago, para que
    /// el cargo pueda volver a liquidarse con el pago correcto.
    ///
    /// <para>No es FK de base a proposito (no queremos cascade ni bloquear el borrado del pago por esta
    /// referencia debil): la integridad la maneja el servicio al crear/borrar el pago, mismo criterio que
    /// <see cref="SupplierPayment.ServicePublicId"/> (referencia polimorfica sin FK).</para>
    /// </summary>
    public int? SettledBySupplierPaymentId { get; set; }

    /// <summary>
    /// ADR-044 T3b Decision 3: historial de ajustes de diferencia de cambio de tesoreria de este cargo
    /// (normalmente 0 o 1 VIGENTE + N superseded tras un soft-void/reemplazo, M4). Filtrar por
    /// <see cref="BookingCancellationLineTreasuryFxAdjustment.IsSuperseded"/> = false para el vigente.
    /// </summary>
    public ICollection<BookingCancellationLineTreasuryFxAdjustment> TreasuryFxAdjustments { get; set; }
        = new List<BookingCancellationLineTreasuryFxAdjustment>();
}
