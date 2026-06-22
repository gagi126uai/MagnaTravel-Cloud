using System;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-037 (2026-06-21): regla PURA del aviso "Debe — no viaja" (ADR-036): ¿esta esta reserva dentro de la
/// ventana de "sale pronto y todavia debe"? Se usa para exponer el flag <c>IsWithinUnpaidAlertWindow</c> en
/// el DTO de la reserva, de modo que el front pinte el aviso sin recalcular la ventana por su cuenta.
///
/// <para><b>FUENTE UNICA de la ventana</b>: replica EXACTAMENTE el criterio que ya usa el job nocturno de
/// notificaciones (<c>OperationalFinanceMonitorService.GenerateUpcomingUnpaidReservationNotificationsAsync</c>)
/// y la alerta de "viajes urgentes" (<c>AlertService</c>): la reserva tiene DEUDA del cliente
/// (<c>balance &gt; 0</c>) y su fecha de SALIDA (<c>StartDate</c>) cae en <c>[hoy ... hoy + ventana]</c>, con
/// la ventana minima de 1 dia (decision H5: se mide contra <c>StartDate</c>, la fecha de salida del viaje).</para>
///
/// <para>Clase PURA (sin EF, sin DB): recibe los datos ya leidos y un "hoy" inyectado para testear sin reloj.
/// El llamador (ReservaService) lee la config <c>OperationalFinanceSettings</c> UNA vez por request y pasa
/// <paramref name="notificationsEnabled"/> y <paramref name="alertDays"/>.</para>
/// </summary>
public static class ReservaUnpaidAlertWindow
{
    /// <summary>
    /// ¿La reserva esta dentro de la ventana de aviso "Debe — no viaja"? Devuelve true SOLO si las tres
    /// condiciones se cumplen a la vez:
    ///   - las notificaciones de este tipo estan habilitadas (<paramref name="notificationsEnabled"/>);
    ///   - hay deuda del cliente (<paramref name="balance"/> &gt; 0);
    ///   - hay fecha de salida (<paramref name="startDate"/>) y cae en <c>[hoy ... hoy + max(alertDays, 1)]</c>.
    ///
    /// <para>Las fechas se comparan por DIA (parte Date), igual que el job nocturno, para no perder el aviso
    /// por la hora. Si la reserva no tiene <paramref name="startDate"/>, no hay ventana posible -> false.</para>
    /// </summary>
    /// <param name="notificationsEnabled">Config <c>EnableUpcomingUnpaidReservationNotifications</c>.</param>
    /// <param name="alertDays">Config <c>UpcomingUnpaidReservationAlertDays</c> (la ventana se acota a min 1 dia).</param>
    /// <param name="balance">Saldo escalar del cliente. &gt; 0 = debe.</param>
    /// <param name="startDate">Fecha de salida del viaje (puede ser null si no esta cargada).</param>
    /// <param name="today">"Hoy" (parte Date). Inyectado para poder testear sin depender del reloj.</param>
    public static bool IsWithin(
        bool notificationsEnabled,
        int alertDays,
        decimal balance,
        DateTime? startDate,
        DateTime today)
    {
        if (!notificationsEnabled)
            return false;

        // Sin deuda no hay nada que avisar.
        if (balance <= 0)
            return false;

        // Sin fecha de salida no se puede ubicar en la ventana.
        if (!startDate.HasValue)
            return false;

        // Ventana minima de 1 dia: mismo Math.Max que el job nocturno (evita una ventana de 0 dias si la
        // config quedara en 0 — el clamp de UpdateAsync ya la mantiene en 1..60, esto es defensa en profundidad).
        var todayDate = today.Date;
        var windowEnd = todayDate.AddDays(Math.Max(alertDays, 1));

        var start = startDate.Value.Date;
        return start >= todayDate && start <= windowEnd;
    }
}
