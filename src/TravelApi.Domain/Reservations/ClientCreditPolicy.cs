using System;
using System.Collections.Generic;
using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-040 (cuenta corriente del cliente, 2026-06-26): resuelve el modo de cobro EFECTIVO de un cliente.
/// Es pura (no toca base de datos): combina el modo propio del cliente (que puede ser null = heredar) con el
/// default de la agencia. Un solo lugar para no divergir entre el gate manual, el job y la cuenta del cliente.
/// </summary>
public static class ClientBillingModeResolver
{
    /// <summary>
    /// Modo efectivo = el del cliente si lo tiene fijado; si es null, el default de la agencia.
    /// </summary>
    public static CustomerBillingMode Resolve(CustomerBillingMode? customerMode, CustomerBillingMode agencyDefault)
        => customerMode ?? agencyDefault;
}

/// <summary>
/// ADR-040: datos para evaluar si un cliente EN CUENTA CORRIENTE puede empezar a viajar (o cerrar) debiendo.
/// La politica es PURA: el caller (infraestructura) ya leyo de base la exposicion y los limites por moneda.
/// </summary>
/// <param name="LimitsByCurrency">
/// Limite de credito por moneda (solo las monedas que TIENEN una fila de limite). Ausencia de una moneda =
/// esa moneda es PREPAGO para el cliente (debe quedar saldado en ella). Claves normalizadas (ARS/USD).
/// </param>
/// <param name="ExposureByCurrency">
/// Deuda viva del cliente por moneda (incluye las reservas ya "En viaje" — review B1). Es la exposicion TOTAL
/// del cliente, ya incluye la reserva que esta por viajar/cerrar. Claves normalizadas.
/// </param>
/// <param name="IsInArrears">
/// FASE 1: SIEMPRE false. Punto de extension para la Fase 2 (vencimientos/aging): cuando haya fechas de
/// vencimiento, "en mora" = tiene al menos una deuda vencida y, si es true, FRENA TODO (cualquier deuda vencida
/// bloquea entero), sin importar la llave avisa/frena ni el limite.
/// </param>
/// <param name="BlockWhenOverLimit">
/// La llave de agencia "frena o avisa" (<c>OperationalFinanceSettings.BlockTravelWhenCreditExceeded</c>).
/// true = pasarse del limite FRENA; false = solo AVISA (deja pasar pero el evaluador igual marca el aviso).
/// </param>
/// <param name="ThisReservaBalance">
/// Saldo escalar de la reserva concreta que dispara la evaluacion. NO entra en la cuenta del limite (la
/// <see cref="ExposureByCurrency"/> ya lo incluye); se conserva para mensajeria/depuracion y como punto de
/// extension. La decision se toma SOBRE LA EXPOSICION TOTAL del cliente.
/// </param>
public sealed record ClientCreditContext(
    IReadOnlyDictionary<string, decimal> LimitsByCurrency,
    IReadOnlyDictionary<string, decimal> ExposureByCurrency,
    bool IsInArrears,
    bool BlockWhenOverLimit,
    decimal ThisReservaBalance);

/// <summary>
/// ADR-040: resultado de evaluar el credito de un cliente a cuenta.
/// <see cref="Allowed"/> = si la reserva puede viajar/cerrar ahora.
/// <see cref="BlockReason"/> = motivo legible (sin montos) cuando NO se permite.
/// <see cref="Warning"/> = aviso legible (sin montos) cuando hay una violacion del limite PERO la agencia eligio
/// "solo avisar" — el caller lo loguea/expone. Cuando todo esta en orden, ambos son null.
/// </summary>
public sealed record ClientCreditDecision(bool Allowed, string? BlockReason, string? Warning)
{
    public static readonly ClientCreditDecision Clear = new(true, null, null);
}

/// <summary>
/// ADR-040 (cuenta corriente del cliente): CANDADO de credito para el lado del cliente EN CUENTA CORRIENTE.
/// Es el reemplazo del candado prepago <c>ReservationEconomicPolicy.IsClientFullyPaid</c> SOLO para clientes
/// en modo <see cref="CustomerBillingMode.Account"/> para PASAR A VIAJAR. Funcion pura, sin base de datos, para
/// que la usen identico el pase MANUAL, el JOB y el re-chequeo de concurrencia del apply.
///
/// <para><b>Que decide</b>: un cliente a cuenta puede VIAJAR debiendo MIENTRAS su deuda total por moneda no
/// supere el limite de esa moneda y no este en mora. Reglas (en orden):</para>
/// <list type="number">
///   <item>Si esta EN MORA (Fase 2) -&gt; FRENA siempre (cualquier deuda vencida bloquea entero). Fase 1: nunca.</item>
///   <item><b>Deber en una moneda SIN limite definido</b> (= prepago de esa moneda, credito cero) -&gt; FRENA
///         SIEMPRE, aun con la llave en "solo avisar" (decision del dueño 2026-06-26: la moneda sin limite es una
///         garantia de prepago que la agencia no relajó).</item>
///   <item><b>Superar un limite que SI tiene definido</b> -&gt; la llave decide: FRENA -&gt; bloquea;
///         "solo avisar" -&gt; deja pasar pero SIEMPRE marca el aviso (nunca sin control).</item>
///   <item>Sin violacion -&gt; pasa limpio.</item>
/// </list>
///
/// <para><b>El CIERRE de un Account NO usa esta politica</b>: cerrar con deuda es INCONDICIONAL (el viaje ya
/// ocurrió; la deuda queda viva en su cuenta corriente). Ver <see cref="EvaluateCanTravel"/>.</para>
///
/// <para><b>Por que la decision es sobre la EXPOSICION TOTAL y no sobre el saldo de la reserva</b>: el limite es
/// del CLIENTE (lo que te debe en total), no de una venta suelta. Una reserva totalmente paga igual no viaja si
/// el cliente esta por encima de su limite por otras reservas (esta sobre-extendido). La exposicion ya incluye
/// esta reserva (CreditExposureStatuses incluye Confirmed y Traveling), asi que no se suma dos veces.</para>
///
/// <para><b>Mensajes sin montos</b> (review de seguridad): el motivo/aviso es texto neutro de estado; nunca
/// menciona el monto debido ni el limite (dato de plata enmascarable). Mismo criterio que
/// <c>ReservationEconomicPolicy.ClientNotFullyPaidForTravelingMessage</c>.</para>
/// </summary>
public static class ClientCreditPolicy
{
    public const string OverCreditLimitTravelingMessage =
        "No se puede pasar a En viaje: el cliente superó su límite de cuenta corriente " +
        "(o tiene saldo en una moneda sin límite asignado). Revisá su cuenta corriente antes de continuar.";

    public const string InArrearsTravelingMessage =
        "No se puede pasar a En viaje: el cliente tiene deuda vencida. Debe regularizar su cuenta antes de viajar.";

    public const string OverCreditLimitWarning =
        "Aviso: el cliente está por encima de su límite de cuenta corriente, " +
        "pero la agencia configuró dejar pasar. Revisá su cuenta corriente.";

    /// <summary>
    /// Evalua si un cliente a cuenta puede empezar a VIAJAR. Ver reglas en la cabecera de la clase.
    ///
    /// <para><b>NO hay un evaluador de CIERRE</b> (decision del dueño 2026-06-26): un cliente a cuenta CIERRA
    /// SIEMPRE aunque esté pasado del límite — el viaje ya ocurrió, no tiene sentido re-chequear el crédito al
    /// cerrar; la deuda queda viva en su cuenta corriente. Por eso el cierre Account es INCONDICIONAL en los
    /// gates (no llama a esta politica) y no existe un EvaluateCanClose.</para>
    /// </summary>
    public static ClientCreditDecision EvaluateCanTravel(ClientCreditContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Regla 1: mora frena TODO, sin importar limites ni la llave avisa/frena (Fase 2; en Fase 1 es false).
        if (context.IsInArrears)
        {
            return new ClientCreditDecision(Allowed: false, BlockReason: InArrearsTravelingMessage, Warning: null);
        }

        // Regla 2 (DECISION del dueño 2026-06-26): deber en una moneda SIN CREDITO = esa moneda es PREPAGO. FRENA
        // SIEMPRE, sin importar la llave avisa/frena. "Sin credito" = no hay fila de limite O la fila tiene
        // Limit <= 0. La fila en CERO se trata IDENTICO a la ausencia (N1): poner el limite explicito en cero es
        // lo MAS restrictivo, no puede resultar menos estricto que no ponerlo. La llave "solo avisar" NO lo afloja.
        if (HasDebtInCurrencyWithoutCredit(context.LimitsByCurrency, context.ExposureByCurrency))
        {
            return new ClientCreditDecision(Allowed: false, BlockReason: OverCreditLimitTravelingMessage, Warning: null);
        }

        // Regla 3: superar un limite que SI tiene definido. ESTE es el unico caso que la llave afloja: con FRENA
        // bloquea; con "solo avisar" deja pasar pero SIEMPRE emite el aviso (nunca queda sin ningun control).
        if (HasExposureOverDefinedLimit(context.LimitsByCurrency, context.ExposureByCurrency))
        {
            return context.BlockWhenOverLimit
                ? new ClientCreditDecision(Allowed: false, BlockReason: OverCreditLimitTravelingMessage, Warning: null)
                : new ClientCreditDecision(Allowed: true, BlockReason: null, Warning: OverCreditLimitWarning);
        }

        return ClientCreditDecision.Clear;
    }

    /// <summary>
    /// True si el cliente DEBE (exposicion &gt; 0) en alguna moneda SIN CREDITO. "Sin credito" =
    /// no hay fila de limite para esa moneda, O la fila existe pero con <c>Limit &lt;= 0</c>. Ambos casos son
    /// "prepago de esa moneda" (credito cero) y disparan la violacion DURA que la llave avisa/frena NO afloja.
    /// La fila en cero se trata IDENTICO a la ausencia (N1): el cero explicito es lo mas restrictivo.
    /// </summary>
    private static bool HasDebtInCurrencyWithoutCredit(
        IReadOnlyDictionary<string, decimal> limitsByCurrency,
        IReadOnlyDictionary<string, decimal> exposureByCurrency)
    {
        foreach (var (currency, owed) in exposureByCurrency)
        {
            // Una exposicion <= 0 (saldada o saldo a favor) no consume credito en esa moneda.
            if (ReservationEconomicPolicy.RoundCurrency(owed) <= 0m)
            {
                continue;
            }

            // Sin fila, o fila con limite no positivo = sin credito en esa moneda.
            if (!limitsByCurrency.TryGetValue(currency, out var limit)
                || ReservationEconomicPolicy.RoundCurrency(limit) <= 0m)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True si en alguna moneda con limite POSITIVO (<c>Limit &gt; 0</c>), la exposicion lo SUPERA (con
    /// tolerancia de redondeo). Este es el UNICO caso que la llave avisa/frena gobierna. Las monedas sin credito
    /// (sin fila o <c>Limit &lt;= 0</c>) NO entran aca: las maneja <see cref="HasDebtInCurrencyWithoutCredit"/>
    /// como violacion dura.
    /// </summary>
    private static bool HasExposureOverDefinedLimit(
        IReadOnlyDictionary<string, decimal> limitsByCurrency,
        IReadOnlyDictionary<string, decimal> exposureByCurrency)
    {
        foreach (var (currency, owed) in exposureByCurrency)
        {
            decimal owedRounded = ReservationEconomicPolicy.RoundCurrency(owed);
            if (owedRounded <= 0m)
            {
                continue;
            }

            if (limitsByCurrency.TryGetValue(currency, out var limit))
            {
                decimal limitRounded = ReservationEconomicPolicy.RoundCurrency(limit);
                // Solo limites POSITIVOS se evaluan aca; los <= 0 son "sin credito" (violacion dura, ya cubierta).
                if (limitRounded > 0m && owedRounded > limitRounded)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
