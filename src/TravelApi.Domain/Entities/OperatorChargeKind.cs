namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 T2 (Addendum, 2026-07-10): tipifica la naturaleza fiscal de UN cargo puntual que el OPERADOR
/// aplica sobre una <see cref="BookingCancellationLine"/> al confirmar (o resolver) su multa/deduccion.
///
/// <para><b>Por que NO es <see cref="DeductionKind"/></b> (M1 del Addendum, decision explicita): ya existe
/// <c>DeductionKind</c> (ADR-002/FC1), que tipifica lo que el operador retiene AL DEVOLVER FONDOS en un
/// refund del circuito viejo (<c>OperatorRefundService</c>). Es un eje DISTINTO — otro momento del flujo,
/// otro dueño de dato — y reusar el mismo nombre en un area sensible de plata generaria confusion real
/// (autocompletado, code review). Por eso esta entidad se llama "charge" (cargo del operador sobre la
/// cancelacion), no "deduction". Dejar este comentario cruzado: si tocás <see cref="DeductionKind"/> pensando
/// que es lo mismo que esto, NO lo es — son dos tablas y dos flujos distintos.</para>
///
/// <para><b>Regla de negocio (spec contable firmada, T2)</b>: <see cref="Withholding"/> es el UNICO valor que
/// NUNCA reduce lo que el operador debe devolver
/// (<see cref="BookingCancellationLine.RefundCap"/>/<see cref="BookingCancellationLine.RetainedDeductionAmount"/>)
/// ni el credito del cliente: es una retencion fiscal que queda como CREDITO FISCAL de la agencia (algo que la
/// agencia puede usar para pagar sus propios impuestos), NO una perdida real de plata. Confundirlo con
/// <see cref="AdministrativeFee"/> violaria esa regla dura del contador.</para>
/// </summary>
public enum OperatorChargeKind
{
    /// <summary>
    /// Cargo administrativo del operador (gestion, papeleo). Default: es el UNICO kind que existia antes de
    /// esta tanda (comportamiento legacy) — el caso simple sigue siendo "una multa, un cargo, este tipo".
    /// </summary>
    AdministrativeFee = 0,

    /// <summary>
    /// Impuesto que el operador traslada a la agencia (no es una retencion fiscal PROPIA de la agencia).
    /// Reduce el reembolso esperado igual que <see cref="AdministrativeFee"/> (misma naturaleza economica:
    /// plata que el operador se queda), pero con leyenda propia en el extracto para que el usuario entienda
    /// de que se trata.
    /// </summary>
    Tax = 1,

    /// <summary>
    /// Retencion fiscal que el operador practica (ej. retencion de IVA/Ganancias/IIBB sobre el pago que le
    /// hizo la agencia). Es CREDITO FISCAL de la agencia, NUNCA una perdida real: por eso NO reduce el
    /// reembolso esperado (<c>RefundCap</c>/<c>RetainedDeductionAmount</c>) ni el credito que se le carga
    /// al cliente. Regla dura del contador, no negociable en el codigo.
    /// </summary>
    Withholding = 2,

    /// <summary>Cajon de sastre: sin regla automatica, requiere revision manual (mismo criterio que <c>DeductionKind.Other</c>).</summary>
    Other = 3,
}
