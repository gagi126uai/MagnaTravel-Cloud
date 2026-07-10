namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-044 T3b Decision 3 (2026-07-10): quien ASUME la diferencia de cambio de tesoreria de un cargo de
/// operador liquidado (ver <see cref="BookingCancellationLineTreasuryFxAdjustment"/>). Decision final de
/// Gaston (2026-07-10, ADR-044): por default la asume el CLIENTE (se lo cobra convertido al TC del dia del
/// cargo); configurable por agencia. Este campo es un SNAPSHOT de esa configuracion en el momento en que se
/// calculo el ajuste — no una lectura en vivo (si la agencia cambia la config despues, los ajustes historicos
/// no se reinterpretan; mismo criterio "congelado al evento" que <c>FiscalSnapshot</c>).
/// </summary>
public enum TreasuryFxAssumedBy
{
    /// <summary>Default: la diferencia de cambio la asume el cliente (se le trasladaria con una ND complementaria, R5).</summary>
    Client = 0,

    /// <summary>La agencia decide absorber la diferencia de cambio (menor margen, registrado igual para auditoria).</summary>
    Agency = 1,
}
