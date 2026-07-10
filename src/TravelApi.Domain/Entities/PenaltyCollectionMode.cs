namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 T2 (Addendum, 2026-07-10): como el operador EFECTIVIZA un cargo
/// (<see cref="BookingCancellationLineOperatorCharge"/>) sobre una cancelacion. Cada operador tiene su propia
/// forma de cobro segun su acuerdo comercial con la agencia; soportar las dos formas a la vez es un requisito
/// de negocio confirmado (no las dos juntas para el MISMO cargo, pero SI conviven varios cargos con distinta
/// forma de cobro sobre la MISMA linea — ej. una comision retenida + un impuesto facturado aparte).
/// </summary>
public enum PenaltyCollectionMode
{
    /// <summary>
    /// El operador se queda la plata AL DEVOLVER el reembolso (default = comportamiento legacy, el UNICO que
    /// existia antes de esta tanda). Resta del <see cref="BookingCancellationLine.RefundCap"/> — salvo que el
    /// cargo sea <see cref="OperatorChargeKind.Withholding"/>, que nunca resta (ver esa regla).
    /// </summary>
    Retenida = 0,

    /// <summary>
    /// El operador devuelve el reembolso INTEGRO (no retiene nada) y factura este cargo APARTE, con su propio
    /// documento de proveedor. Genera una DEUDA NUEVA de la agencia hacia el operador (cuenta a pagar, circuito
    /// ADR-041 existente) en vez de reducir el reembolso esperado: por eso NO resta
    /// <see cref="BookingCancellationLine.RefundCap"/>/<see cref="BookingCancellationLine.RetainedDeductionAmount"/>.
    /// Exige <see cref="BookingCancellationLineOperatorCharge.DocumentRef"/> (el documento del proveedor).
    /// </summary>
    FacturadaAparte = 1,
}
