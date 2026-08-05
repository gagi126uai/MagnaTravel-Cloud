namespace TravelApi.Domain.Helpers;

/// <summary>
/// Gate del aviso de pasaporte por AMBITO del servicio (decision firmada del dueño, 2026-08-05, PARTE 1
/// de la obra "gate ámbito"): en cabotaje y en el Mercosur se viaja con DNI, así que el aviso de
/// pasaporte no tiene sentido en una reserva 100% Nacional (hoy sonaba en CUALQUIER reserva, incluidas
/// las que nunca salen del país).
///
/// <para>Esta clase resuelve una pregunta DISTINTA de la que resuelve <see cref="PassportExpiryRules"/>:
/// esta decide SI corresponde avisar (según el ámbito de los servicios cargados), la otra decide QUÉ
/// avisar (según si el pasaporte cargado alcanza para las fechas del viaje). T-3 obliga a combinar las
/// dos reglas en el motor — nunca en la pantalla, para que no se desincronicen entre el chip y el toast.</para>
///
/// <para><b>Regla CONSERVADORA para el sin-dato</b>: si algún servicio de la reserva no tiene el ámbito
/// cargado (<c>SinDefinir</c>) el aviso SIGUE mostrándose — la falta de dato nunca apaga un aviso que
/// hoy existe. Lo mismo si la reserva todavía no tiene NINGÚN servicio cargado (comportamiento histórico,
/// sin cambios: el aviso depende solo del vencimiento). El aviso se apaga ÚNICAMENTE cuando TODOS los
/// servicios cargados tienen el ámbito definido y son, sin excepción, Nacional.</para>
/// </summary>
public static class PassportAlertScopeGate
{
    /// <summary>
    /// True si corresponde EVALUAR el aviso de pasaporte (no significa que vaya a avisar: eso lo decide
    /// <see cref="PassportExpiryRules"/> con el vencimiento cargado). False solo cuando la reserva es
    /// 100% Nacional con el ámbito definido en todos sus servicios.
    /// </summary>
    /// <param name="reservaHasAnyServiceWithScope">True si la reserva tiene al menos un servicio (genérico o vuelo) cargado.</param>
    /// <param name="reservaHasInternationalService">True si al menos uno de esos servicios es Internacional.</param>
    /// <param name="reservaHasUndefinedScopeService">True si al menos uno de esos servicios no tiene el ámbito cargado.</param>
    public static bool IsOpen(
        bool reservaHasAnyServiceWithScope,
        bool reservaHasInternationalService,
        bool reservaHasUndefinedScopeService)
    {
        if (!reservaHasAnyServiceWithScope)
        {
            // Sin ningún servicio cargado todavía: comportamiento histórico, no se apaga nada.
            return true;
        }

        if (reservaHasInternationalService || reservaHasUndefinedScopeService)
        {
            return true;
        }

        // Hay servicios, ninguno es Internacional y ninguno está SinDefinir: son TODOS Nacional con
        // dato completo. Ahí sí se apaga el aviso.
        return false;
    }
}
