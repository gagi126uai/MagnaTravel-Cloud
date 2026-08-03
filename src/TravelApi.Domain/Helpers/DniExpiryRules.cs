namespace TravelApi.Domain.Helpers;

/// <summary>
/// Semaforo de DNI vencido para cabotaje (decision firmada del dueño, 2026-08-03). Espejo de
/// <see cref="PassportExpiryRules"/>: mismo criterio de "fin del viaje" (con fallback al inicio) y misma
/// filosofia de AVISO (P-11), nunca candado — un DNI vencido NO impide guardar/confirmar/facturar nada,
/// solo se le muestra al vendedor para que gestione la renovacion o consiga el pasaporte a tiempo.
///
/// <para><b>Por que "cabotaje" y no "cualquier viaje"</b>: para volar DENTRO del pais (cabotaje) las
/// aerolineas piden un documento de identidad VIGENTE (DNI o pasaporte); para un viaje al exterior el que
/// importa es el pasaporte (ya cubierto por <see cref="PassportExpiryRules"/>). Por eso este aviso solo
/// dispara si la reserva tiene AL MENOS UN servicio marcado como Nacional
/// (<c>ServicioReserva.GeographicScope == ServiceGeographicScope.Domestic</c>) — sin eso, no hay cabotaje
/// que justifique el aviso.</para>
///
/// <para><b>Por que mira tambien el pasaporte del pasajero</b>: si el pasajero YA tiene un pasaporte
/// vigente que cubre el viaje, el DNI vencido no es un problema real (puede volar con el pasaporte). El
/// aviso solo tiene sentido cuando NINGUNO de los dos documentos le alcanza.</para>
/// </summary>
public static class DniExpiryRules
{
    /// <summary>Texto del aviso cuando NO hay fechas de viaje cargadas (mismo criterio que <see cref="PassportExpiryRules"/>: solo mira si ya vencio hoy).</summary>
    public const string ExpiredDniWarning = "El DNI de este pasajero está vencido.";

    /// <summary>Texto del aviso cuando SI hay fechas de viaje: el DNI no le alcanza para volar dentro del pais.</summary>
    public const string ExpiredBeforeTripEndWarning =
        "El DNI de este pasajero se vence antes del viaje. Para volar dentro del país piden DNI vigente (o pasaporte vigente).";

    /// <summary>
    /// Devuelve el aviso (o null si no corresponde). UN SOLO nivel ("Expired"): a diferencia del pasaporte,
    /// el DNI no tiene una franja "ambar" — o alcanza para el viaje, o no alcanza.
    /// </summary>
    /// <param name="documentType">Tipo de documento cargado del pasajero. Solo dispara si es DNI.</param>
    /// <param name="documentExpiry">Vencimiento cargado del DNI. Null = nada que avisar.</param>
    /// <param name="reservaHasDomesticService">
    /// True si la reserva tiene AL MENOS UN servicio con <c>GeographicScope == Domestic</c> (cabotaje).
    /// Sin esto, el aviso no aplica: no hay tramo dentro del pais que exija el DNI vigente.
    /// </param>
    /// <param name="passportExpiry">
    /// Vencimiento del PASAPORTE del mismo pasajero (puede ser null). Si el pasaporte vence DESPUES del
    /// fin del viaje, ya cubre el viaje y el DNI vencido deja de ser un problema (el pasajero vuela con
    /// el pasaporte).
    /// </param>
    /// <param name="tripStart">Fecha de inicio del viaje (Reserva.StartDate).</param>
    /// <param name="tripEnd">Fecha de fin del viaje (Reserva.EndDate). Si falta, se usa <paramref name="tripStart"/>.</param>
    /// <param name="todayInArgentina">Solo para fijar el "hoy" en los tests (T-14: hora argentina siempre).</param>
    public static DniAlert? GetAlertOrNull(
        string? documentType,
        DateTime? documentExpiry,
        bool reservaHasDomesticService,
        DateTime? passportExpiry,
        DateTime? tripStart,
        DateTime? tripEnd,
        DateTime? todayInArgentina = null)
    {
        if (!DocumentNumberValidator.IsDniType(documentType))
        {
            return null;
        }

        if (!documentExpiry.HasValue)
        {
            return null;
        }

        if (!reservaHasDomesticService)
        {
            return null;
        }

        var today = (todayInArgentina ?? ArgentinaTime.GetArgentinaToday()).Date;
        var expiry = documentExpiry.Value.Date;

        // "Fin del viaje" para esta cuenta: si no hay fecha de fin cargada, usamos el inicio (mismo
        // criterio que PassportExpiryRules, para que las dos reglas nunca se desincronicen — F-1).
        var tripEndOrStart = tripEnd ?? tripStart;

        if (tripEndOrStart is null)
        {
            // Sin NINGUNA fecha de viaje cargada: nos quedamos con la regla mas simple (vencido hoy =
            // aviso, nada mas). No se evalua el pasaporte aca: sin fechas de viaje no hay "fin del viaje"
            // contra el cual comparar si el pasaporte lo cubre.
            return expiry < today
                ? new DniAlert(DniAlertLevel.Expired, ExpiredDniWarning)
                : null;
        }

        var tripEndDate = tripEndOrStart.Value.Date;

        // Un pasaporte VIGENTE que cubre el viaje (vence DESPUES del fin del viaje) hace que el DNI
        // vencido deje de importar: el pasajero vuela con el pasaporte. No hay aviso.
        var passportCoversTrip = passportExpiry.HasValue && passportExpiry.Value.Date > tripEndDate;
        if (passportCoversTrip)
        {
            return null;
        }

        // Mismo criterio que el ROJO de PassportExpiryRules: ya vencido hoy, O vence antes/el mismo dia
        // en que termina el viaje (no le alcanza para volver al pais en el tramo de cabotaje).
        var dniExpiredForTrip = expiry < today || expiry <= tripEndDate;
        return dniExpiredForTrip
            ? new DniAlert(DniAlertLevel.Expired, ExpiredBeforeTripEndWarning)
            : null;
    }
}

/// <summary>
/// Nivel del semaforo de DNI vencido. Un solo valor a proposito (ver <see cref="DniExpiryRules"/>): se
/// expone al front como STRING ("Expired"), nunca como numero crudo de enum (P-1, T-5).
/// </summary>
public enum DniAlertLevel
{
    /// <summary>Unico nivel: el DNI no le alcanza para el tramo de cabotaje (o esta vencido a secas).</summary>
    Expired,
}

/// <summary>Resultado del semaforo: el nivel (para pintar el chip) + el texto exacto que lee el vendedor.</summary>
public sealed record DniAlert(DniAlertLevel Level, string Text);
