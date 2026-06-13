using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-020 (2026-06-07): fuente UNICA de "en que punto esta un servicio". Distingue DOS hechos
/// de negocio que NO son lo mismo (sobre todo en el aereo):
///
/// <list type="bullet">
/// <item><b>IsOperatorConfirmed</b>: el operador se comprometio. Gobierna borrar-vs-cancelar
///   (un servicio confirmado NO se borra, solo se cancela), el estampado de ConfirmedAt,
///   las penalidades y la deuda al proveedor.</item>
/// <item><b>IsResolved</b>: el servicio esta ASEGURADO para viajar. Gobierna el paso automatico
///   a Confirmada (motor de estados) y el saldo del cliente (ConfirmedSale).</item>
/// </list>
///
/// <para>Para todos los tipos salvo el aereo ambos predicados COINCIDEN. En el aereo divergen:
/// un PNR confirmado (HK) confirma pero NO resuelve; resuelve recien al emitir el ticket
/// (<c>TicketIssuedAt != null</c>). El traslado puede resolver sin confirmacion del operador si
/// lleva la marca "no requiere confirmacion".</para>
///
/// <para>Clase PURA (sin EF, sin DB): se testea sin Postgres, igual que <see cref="ReservaMoneyCalculator"/>.</para>
/// </summary>
public static class ServiceResolutionRules
{
    // ===================== FlightSegment (el unico donde confirmacion != resolucion) =====================

    public static bool IsOperatorConfirmed(FlightSegment flight)
        => WorkflowStatusHelper.MapFlightStatus(flight.Status) == WorkflowStatuses.Confirmado;

    // El ticket emitido es lo que RESUELVE el aereo. El PNR confirmado NO alcanza (decision B4).
    // OJO (ADR-020, regla de plata): un vuelo emitido y DESPUES cancelado NO resuelve. Si no
    // excluyeramos el cancelado, su precio de venta seguiria sumando a ConfirmedSale (deuda
    // fantasma del cliente). El precio de un servicio cancelado sale del saldo; la penalidad,
    // si la hay, se maneja por el flujo de cancelacion, no dejando la venta como deuda.
    // Los otros tipos ya quedan excluidos porque su IsResolved exige status == Confirmado
    // (un cancelado mapea a Cancelado); el aereo necesita el chequeo explicito porque resuelve
    // por TicketIssuedAt, no por el Status.
    public static bool IsResolved(FlightSegment flight)
        => flight.TicketIssuedAt != null && !IsCancelled(flight);

    public static bool IsCancelled(FlightSegment flight)
        => WorkflowStatusHelper.MapFlightStatus(flight.Status) == WorkflowStatuses.Cancelado;

    // ===================== HotelBooking =====================

    public static bool IsOperatorConfirmed(HotelBooking hotel) => ConfirmedByGenericStatus(hotel.Status);
    public static bool IsResolved(HotelBooking hotel) => ConfirmedByGenericStatus(hotel.Status);
    public static bool IsCancelled(HotelBooking hotel) => CancelledByGenericStatus(hotel.Status);

    // ===================== PackageBooking =====================

    public static bool IsOperatorConfirmed(PackageBooking package) => ConfirmedByGenericStatus(package.Status);
    public static bool IsResolved(PackageBooking package) => ConfirmedByGenericStatus(package.Status);
    public static bool IsCancelled(PackageBooking package) => CancelledByGenericStatus(package.Status);

    // ===================== AssistanceBooking (el mapeo generico ya trata "emit*" como confirmado) =====================

    public static bool IsOperatorConfirmed(AssistanceBooking assistance) => ConfirmedByGenericStatus(assistance.Status);
    public static bool IsResolved(AssistanceBooking assistance) => ConfirmedByGenericStatus(assistance.Status);
    public static bool IsCancelled(AssistanceBooking assistance) => CancelledByGenericStatus(assistance.Status);

    // ===================== TransferBooking (puede resolver con la marca "no requiere confirmacion") =====================

    public static bool IsOperatorConfirmed(TransferBooking transfer) => ConfirmedByGenericStatus(transfer.Status);

    // Un traslado resuelve si el operador lo confirmo O si lleva la marca "no requiere
    // confirmacion". Pero un traslado CANCELADO nunca resuelve, ni siquiera con la marca puesta:
    // mismo criterio que el aereo (un servicio cancelado sale del saldo, ADR-020).
    public static bool IsResolved(TransferBooking transfer)
        => !IsCancelled(transfer)
           && (ConfirmedByGenericStatus(transfer.Status) || transfer.NoConfirmationRequired);

    public static bool IsCancelled(TransferBooking transfer) => CancelledByGenericStatus(transfer.Status);

    // ===================== ServicioReserva (generico) =====================

    public static bool IsOperatorConfirmed(ServicioReserva service) => ConfirmedByGenericStatus(service.Status);
    public static bool IsResolved(ServicioReserva service) => ConfirmedByGenericStatus(service.Status);
    public static bool IsCancelled(ServicioReserva service) => CancelledByGenericStatus(service.Status);

    // ===================== Agregados a nivel reserva (reusados por motor de estados y vouchers) =====================

    /// <summary>
    /// Nombra los TIPOS de servicio vivos (no cancelados) que todavia NO estan resueltos por el
    /// operador, recorriendo las 6 colecciones de la reserva. Devuelve una etiqueta legible por cada
    /// tipo con al menos un servicio sin resolver (sin repetir el tipo). Lista vacia = no hay nada
    /// pendiente de resolver (todos los servicios vivos estan asegurados, o la reserva no tiene
    /// servicios vivos).
    ///
    /// <para>Por que existe: tanto la confirmacion automatica de la reserva (motor de estados ADR-020)
    /// como el bloqueo de emision de voucher (auditoria de negocio 2026-06-12) necesitan la MISMA
    /// definicion de "que falta resolver". Centralizarla aca evita que dos lugares la dupliquen y se
    /// desincronicen. El llamador es responsable de cargar las 6 colecciones (Includes en EF); una
    /// coleccion null se trata como vacia.</para>
    /// </summary>
    public static IReadOnlyList<string> GetUnresolvedLiveServiceLabels(Reserva reserva)
    {
        ArgumentNullException.ThrowIfNull(reserva);

        var unresolvedTypes = new List<string>();

        // Agrega la etiqueta del tipo si encuentra AL MENOS un servicio vivo sin resolver. Corta en el
        // primero (break): basta con saber que ese tipo tiene un pendiente para nombrarlo una vez.
        void CheckType<T>(IEnumerable<T>? items, Func<T, bool> isCancelled, Func<T, bool> isResolved, string typeLabel)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (isCancelled(item)) continue; // los cancelados no cuentan ni bloquean
                if (!isResolved(item))
                {
                    unresolvedTypes.Add(typeLabel);
                    break;
                }
            }
        }

        CheckType(reserva.FlightSegments, IsCancelled, IsResolved, "un aereo (sin emitir)");
        CheckType(reserva.HotelBookings, IsCancelled, IsResolved, "un hotel");
        CheckType(reserva.TransferBookings, IsCancelled, IsResolved, "un traslado");
        CheckType(reserva.PackageBookings, IsCancelled, IsResolved, "un paquete");
        CheckType(reserva.AssistanceBookings, IsCancelled, IsResolved, "una asistencia");
        CheckType(reserva.Servicios, IsCancelled, IsResolved, "un servicio");

        return unresolvedTypes;
    }

    // --- helpers de mapeo de estado generico (texto libre que contiene "confirm"/"emit"/"cancel") ---

    private static bool ConfirmedByGenericStatus(string? status)
        => WorkflowStatusHelper.MapGenericStatus(status ?? string.Empty) == WorkflowStatuses.Confirmado;

    private static bool CancelledByGenericStatus(string? status)
        => WorkflowStatusHelper.MapGenericStatus(status ?? string.Empty) == WorkflowStatuses.Cancelado;
}
