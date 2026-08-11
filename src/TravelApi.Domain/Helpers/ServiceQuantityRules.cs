using TravelApi.Domain.Exceptions;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Minimos de las CANTIDADES que se cuentan en un servicio: habitaciones y pasajeros.
///
/// <para><b>Por que existe</b> (hallazgo de la prueba con navegador en PROD, 2026-08-11): se guardo
/// un hotel con Habitaciones = -1 y el backend lo acepto sin chistar. El daño concreto es en el
/// TARIFARIO: para sacar el precio unitario, <see cref="CatalogUnitization"/> divide el total por
/// las cantidades, pero antes las lleva a 1 con <c>Math.Max</c> para no dividir por cero. O sea que
/// la plata del servicio no cambia de signo, pero la cantidad rota deja de dividir: un hotel de 3
/// habitaciones guardado con -1 se aprende como si el total de las TRES fuera el precio de UNA sola
/// (precio unitario inflado al triple), y eso es lo que el tarifario le va a sugerir al vendedor en
/// la proxima venta. Aparte, claro, queda una reserva con un dato imposible en la ficha y en los
/// vouchers.</para>
///
/// <para><b>T-3 — el guard vive en el motor</b>: la ficha de carga tambien frena estas cantidades en
/// pantalla (para avisarle al vendedor antes de guardar), pero la verdad la impone este archivo. Los
/// textos son EXACTAMENTE los mismos de la pantalla, asi que el vendedor lee siempre la misma frase
/// venga el freno de donde venga.</para>
/// </summary>
public static class ServiceQuantityRules
{
    /// <summary>Texto unico del rechazo de habitaciones (igual al de la ficha de carga).</summary>
    public const string RoomsBelowMinimumMessage = "Las habitaciones tienen que ser al menos 1.";

    /// <summary>Texto unico del rechazo de pasajeros (igual al de la ficha de carga).</summary>
    public const string PassengersBelowMinimumMessage = "Los pasajeros tienen que ser al menos 1.";

    /// <summary>
    /// Un hotel se vende por habitacion: cero (o menos) habitaciones no es una venta, es un dato roto.
    /// </summary>
    public static void EnsureRoomsAtLeastOne(int rooms)
    {
        if (rooms < 1)
        {
            throw new ServiceQuantityValidationException(RoomsBelowMinimumMessage);
        }
    }

    /// <summary>
    /// Cantidad de pasajeros que viaja partida en adultos y menores (hotel, paquete, asistencia).
    ///
    /// <para>La regla es sobre el TOTAL, no sobre cada casillero: cero menores es normalisimo, lo que
    /// no puede pasar es que no viaje NADIE. Ademas se rechaza cualquiera de los dos en negativo,
    /// aunque el total diera positivo (3 adultos y -1 menor suman 2, pero el -1 igual descuadra las
    /// cuentas por persona).</para>
    /// </summary>
    public static void EnsurePassengersAtLeastOne(int adults, int children)
    {
        var hayAlguienEnNegativo = adults < 0 || children < 0;
        var totalDePasajeros = adults + children;

        if (hayAlguienEnNegativo || totalDePasajeros < 1)
        {
            throw new ServiceQuantityValidationException(PassengersBelowMinimumMessage);
        }
    }

    /// <summary>
    /// Cantidad de pasajeros que viaja en un solo numero (traslado).
    /// </summary>
    public static void EnsurePassengersAtLeastOne(int passengers)
    {
        if (passengers < 1)
        {
            throw new ServiceQuantityValidationException(PassengersBelowMinimumMessage);
        }
    }

    /// <summary>
    /// Cantidad de pasajeros OPCIONAL (el aereo la guarda como "sin informar" cuando el vendedor no
    /// la carga). Si no vino, no hay nada que validar; si vino, tiene que ser al menos 1.
    /// </summary>
    public static void EnsurePassengersAtLeastOneWhenInformed(int? passengerCount)
    {
        if (!passengerCount.HasValue) return;

        EnsurePassengersAtLeastOne(passengerCount.Value);
    }
}
