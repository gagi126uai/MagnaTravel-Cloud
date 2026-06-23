using TravelApi.Application.DTOs;

namespace TravelApi.Application.Contracts.Reservations;

public class ReservationServiceMutationResult
{
    public required ServicioReservaDto Servicio { get; set; }
    public string? Warning { get; set; }
}

/// <summary>
/// REPROGRAMAR VIAJE (2026-06-23): resultado de mover todas las fechas de una reserva. Devuelve el shift
/// efectivamente aplicado, cuantos servicios se movieron y las nuevas fechas de cabecera de la reserva
/// (StartDate/EndDate ya recalculadas). Sin montos: reprogramar no toca la plata.
/// </summary>
public class RescheduleReservaResult
{
    /// <summary>Desplazamiento aplicado en dias (+ adelanta, - atrasa). 0 = no-op (no se movio nada).</summary>
    public int DaysShift { get; set; }

    /// <summary>Cantidad de servicios (de todos los tipos) cuyas fechas se desplazaron.</summary>
    public int ServicesMoved { get; set; }

    /// <summary>Nueva fecha de salida de la reserva, recalculada despues del shift. Null si la reserva no tiene fechas.</summary>
    public DateTime? NewStartDate { get; set; }

    /// <summary>Nueva fecha de regreso de la reserva, recalculada despues del shift. Null si la reserva no tiene fechas.</summary>
    public DateTime? NewEndDate { get; set; }
}
