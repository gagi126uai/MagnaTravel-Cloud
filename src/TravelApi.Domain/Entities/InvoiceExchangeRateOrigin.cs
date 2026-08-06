namespace TravelApi.Domain.Entities;

/// <summary>
/// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, parte A4 + Parte B): DE DONDE SALIO
/// el tipo de cambio con el que se emitio un comprobante en moneda extranjera.
///
/// <para><b>Por que no alcanzaba con <see cref="ExchangeRateSource"/></b>: aquel enum dice de que
/// FUENTE salio el numero (el organismo, el Banco Nacion, una carga a mano). Este dice como se
/// COMPORTO el sistema con el usuario: si acepto el numero sugerido, si lo escribio el, si el sistema
/// se lo acomodo al techo del dia sin molestarlo, o si lo completo solo. Son dos preguntas distintas y
/// la auditoria necesita las dos: "el numero salio del organismo" no explica por que el usuario habia
/// escrito otro.</para>
///
/// <para>Se persiste como <c>int</c> en <c>Invoice.ExchangeRateOrigin</c>, nullable: los comprobantes
/// emitidos ANTES de esta obra (y todos los que van en pesos) quedan en <c>NULL</c>. NO se hace
/// backfill — no hay forma honesta de reconstruir la intencion del usuario de una factura vieja.</para>
/// </summary>
public enum InvoiceExchangeRateOrigin
{
    /// <summary>Sin dato (comprobante en pesos, o anterior a esta obra).</summary>
    Unset = 0,

    /// <summary>El usuario dejo el numero que el sistema le sugirio (coincidio EXACTO). No se le pide explicacion.</summary>
    SuggestedAccepted = 1,

    /// <summary>El usuario escribio un numero distinto del sugerido (o no habia sugerencia) y explico de donde lo saco.</summary>
    ManualWithJustification = 2,

    /// <summary>
    /// El usuario escribio un numero MAS ALTO que el techo del dia y el sistema lo acomodo solo al techo
    /// para que el comprobante no rebote (spec A4, excepcion firmada a la regla P-21). Lo que el usuario
    /// habia querido poner queda en <c>Invoice.RequestedExchangeRate</c>. No se le pide explicacion: el
    /// numero que quedo no lo eligio el.
    /// </summary>
    ClampedToDailyCeiling = 3,

    /// <summary>
    /// El sistema completo el numero solo, sin mostrarle el casillero al usuario (spec A3, "modo
    /// invisible"): mientras el sistema emite comprobantes de ensayo, el organismo exige un tipo de
    /// cambio propio y cualquier otro rebota. El usuario elige dolares y emite; nada mas.
    /// </summary>
    SystemFilled = 4
}
