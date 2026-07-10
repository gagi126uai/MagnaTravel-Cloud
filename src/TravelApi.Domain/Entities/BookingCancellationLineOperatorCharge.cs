using System.ComponentModel.DataAnnotations;

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
}
