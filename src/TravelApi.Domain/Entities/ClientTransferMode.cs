namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 T3a (2026-07-10): como se traslada al CLIENTE un cargo puntual del operador
/// (<see cref="BookingCancellationLineOperatorCharge"/>) al armar la Nota de Debito. El ADR-044 (seccion P3) lo
/// describe como "cada multa por operador decide su traslado: tal cual (default) | + fee de gestion | absorber".
///
/// <para><b>Por que es un campo NUEVO en el cargo y no una bandera global del BC</b>: la decision es POR CARGO,
/// no por cancelacion entera — el mismo operador puede tener un cargo administrativo que se traslada tal cual y
/// otro (ej. una retencion, aunque esa nunca llega al cliente) con otro tratamiento. Modelarlo a nivel BC forzaria
/// una unica decision para toda la cancelacion, que no es lo que el negocio necesita.</para>
/// </summary>
public enum ClientTransferMode
{
    /// <summary>
    /// El cargo se traslada al cliente por su monto exacto, sin agregar ni quitar nada. Default: es el
    /// comportamiento de SIEMPRE (el unico que existia antes de esta tanda) — el caso simple sigue siendo "sin
    /// friccion", nadie tiene que elegir nada para que la ND salga igual que hoy.
    /// </summary>
    AsIs = 0,

    /// <summary>
    /// El cargo se traslada al cliente por su monto exacto MAS un cargo de gestion propio de la agencia
    /// (<see cref="BookingCancellationLineOperatorCharge.ManagementFeeAmount"/>), en un renglon APARTE de la
    /// misma Nota de Debito (mismo comprobante, no un documento nuevo). El renglon del fee de gestion usa el
    /// concepto fiscal ya firmado <see cref="CancellationConceptKind.AgencyManagementFee"/> — es ingreso propio y
    /// gravado de la agencia, distinto de la multa del operador que solo se replica (pass-through).
    /// </summary>
    WithManagementFee = 1,

    /// <summary>
    /// La agencia decide NO trasladarle este cargo al cliente (lo absorbe, se achica el margen del servicio). NO
    /// genera ningun renglon en la Nota de Debito. El cargo igual queda persistido con este valor: es el rastro de
    /// auditoria de la decision (quien decidio absorber, cuando, sobre que cargo), sin necesidad de un documento
    /// fiscal — mismo criterio ya cerrado en el ADR ("Absorber = sin documento, margen menor, registrado").
    /// </summary>
    Absorbed = 2,
}
