namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 2.
///
/// <para><b>Por que es AVISO y no candado</b> (decision firmada del dueño): en la agencia se carga al
/// pasajero con el pasaporte que tiene HOY, y despues lo renueva antes de viajar. Si el sistema rechazara
/// un pasaporte vencido, el vendedor no podria terminar de cargar la reserva y —lo peor— empezaria a
/// inventar fechas para que el sistema lo deje pasar (el dato quedaria peor que antes). Entonces: se
/// guarda igual, y la respuesta trae el aviso para que la pantalla lo muestre.</para>
///
/// <para>Este helper NO tira excepcion: devuelve el texto del aviso (o null si no hay nada que avisar).
/// Quien llama decide donde ponerlo — hoy viaja en el campo <c>Warning</c> del pasajero devuelto, el mismo
/// riel que ya usa el aviso de fechas de la reserva.</para>
/// </summary>
public static class PassportExpiryRules
{
    /// <summary>Texto unico del aviso "vencido a secas" (sin fechas de viaje) — lo devuelve <see cref="GetAlertOrNull"/>.</summary>
    public const string ExpiredPassportWarning = "El pasaporte de este pasajero está vencido.";

    /// <summary>Texto del ROJO cuando SI hay fechas de viaje: el pasaporte no le alcanza para volver.</summary>
    public const string ExpiredBeforeTripEndWarning =
        "El pasaporte de este pasajero se vence antes del fin del viaje.";

    /// <summary>
    /// Texto del AMBAR: sirve para viajar, pero no le sobra margen despues. Reformulado (decision firmada
    /// del dueño, 2026-08-05, PARTE 2): la version vieja afirmaba "6 meses" como SI fuera LA regla de
    /// todos los destinos, y eso es un aviso de menos o de mas segun a donde se viaje (EEUU no exige ese
    /// margen; el espacio Schengen pide 3 meses y pasaporte emitido hace menos de 10 años). El disparador
    /// del AMBAR sigue usando 6 meses como umbral interno (es el margen mas exigente y comun, asi que
    /// avisa "temprano" y nunca se queda corto), pero el TEXTO ya no lo presenta como un hecho fijo:
    /// manda a revisar el requisito puntual del destino.
    /// </summary>
    public const string TightMarginAfterTripWarning =
        "El pasaporte vence cerca de la fecha del viaje. Verificá el requisito del destino: cada país pide una vigencia distinta.";

    /// <summary>
    /// D2 (decision firmada del dueño, 2026-07-31 tarde): amplia el aviso de pasaporte para que mire las
    /// FECHAS DEL VIAJE, no solo si ya vencio hoy. Sigue siendo un AVISO (P-20): nunca frena el guardado,
    /// solo informa. Se usa tanto al guardar el pasajero (riel <c>Warning</c>) como al listar la reserva
    /// (chip fijo en la fila, F11): por eso vive en Domain puro, sin EF ni DB, para que ambos caminos
    /// llamen SIEMPRE a la misma regla y nunca se desincronicen (F-1: una sola regla por entidad).
    /// </summary>
    /// <param name="passportExpiry">Vencimiento cargado del pasaporte. Null = nada que avisar.</param>
    /// <param name="tripStart">Fecha de inicio del viaje (Reserva.StartDate).</param>
    /// <param name="tripEnd">Fecha de fin del viaje (Reserva.EndDate). Si falta, se usa <paramref name="tripStart"/>.</param>
    /// <param name="todayInArgentina">Solo para fijar el "hoy" en los tests (T-14: hora argentina siempre).</param>
    public static PassportAlert? GetAlertOrNull(
        DateTime? passportExpiry,
        DateTime? tripStart,
        DateTime? tripEnd,
        DateTime? todayInArgentina = null)
    {
        if (!passportExpiry.HasValue)
        {
            return null;
        }

        var today = (todayInArgentina ?? ArgentinaTime.GetArgentinaToday()).Date;
        var expiry = passportExpiry.Value.Date;

        // "Fin del viaje" para esta cuenta: si no hay fecha de fin cargada, usamos el inicio (mejor una
        // fecha aproximada que no avisar nada).
        var tripEndOrStart = tripEnd ?? tripStart;

        if (tripEndOrStart is null)
        {
            // Sin NINGUNA fecha de viaje cargada en la reserva: nos quedamos con la regla historica
            // (vencido hoy = ROJO, nada mas). No relajar: la cubren los tests viejos de este helper.
            return expiry < today
                ? new PassportAlert(PassportAlertLevel.Expired, ExpiredPassportWarning)
                : null;
        }

        var tripEndDate = tripEndOrStart.Value.Date;

        // ROJO: ya vencido hoy, O vence antes/el mismo dia en que termina el viaje (no le sirve para
        // volver al pais). Cualquiera de las dos condiciones alcanza.
        if (expiry < today || expiry <= tripEndDate)
        {
            return new PassportAlert(PassportAlertLevel.Expired, ExpiredBeforeTripEndWarning);
        }

        // AMBAR: le alcanza para el viaje, pero le quedan menos de 6 meses de vigencia DESPUES de que
        // termina — muchos destinos piden ese colchon para dejar entrar al pasajero.
        var sixMonthsAfterTripEnd = tripEndDate.AddMonths(6);
        if (expiry < sixMonthsAfterTripEnd)
        {
            return new PassportAlert(PassportAlertLevel.Tight, TightMarginAfterTripWarning);
        }

        // Holgado: le sobra vigencia de sobra, no hace falta avisar nada.
        return null;
    }
}

/// <summary>
/// Nivel del semaforo de vencimiento de pasaporte (D2). Se expone al front como STRING
/// ("Expired"/"Tight"), nunca como numero crudo de enum (P-1, T-5).
/// </summary>
public enum PassportAlertLevel
{
    /// <summary>Rojo: vencido a secas, o vencido para las fechas del viaje.</summary>
    Expired,

    /// <summary>Ambar: vigente para el viaje, pero con poco margen despues.</summary>
    Tight,
}

/// <summary>Resultado del semaforo: el nivel (para pintar el chip) + el texto exacto que lee el vendedor.</summary>
public sealed record PassportAlert(PassportAlertLevel Level, string Text);
