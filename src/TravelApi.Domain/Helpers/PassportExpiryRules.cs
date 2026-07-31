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
    /// <summary>Texto unico del aviso, tal como lo lee el vendedor.</summary>
    public const string ExpiredPassportWarning = "El pasaporte de este pasajero está vencido.";

    /// <summary>
    /// Devuelve el aviso si el pasaporte ya vencio; null si esta vigente, si vence hoy, o si no se cargo
    /// la fecha.
    ///
    /// <para>Se compara contra el DIA CALENDARIO ARGENTINO (ver <see cref="ArgentinaTime"/>): el servidor
    /// corre en UTC y, cargando un pasaporte a la noche, un pasaporte que vence hoy en Argentina ya seria
    /// "ayer" en UTC y el sistema avisaria de un vencimiento que todavia no ocurrio.</para>
    ///
    /// <para><paramref name="todayInArgentina"/> existe solo para fijar el "hoy" en los tests.</para>
    /// </summary>
    public static string? GetExpiredWarningOrNull(DateTime? passportExpiry, DateTime? todayInArgentina = null)
    {
        if (!passportExpiry.HasValue)
        {
            return null;
        }

        var today = (todayInArgentina ?? ArgentinaTime.GetArgentinaToday()).Date;

        // Vence HOY todavia no es vencido: el pasaporte sirve hasta el final de su ultimo dia.
        return passportExpiry.Value.Date < today
            ? ExpiredPassportWarning
            : null;
    }
}
