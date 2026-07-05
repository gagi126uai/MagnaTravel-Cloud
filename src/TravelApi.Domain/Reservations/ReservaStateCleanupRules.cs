using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// Tabla declarativa ÚNICA de "qué marcas de revisión hay que limpiar cuando una reserva ENTRA a cierto estado".
///
/// <para><b>Por qué existe</b>: la marca "confirmada con cambios" (ADR-027:
/// <see cref="Reserva.HasUnacknowledgedChanges"/> + <see cref="Reserva.ChangesPendingSince"/> + las filas de detalle
/// <c>ReservaPendingChanges</c>) y el motivo de revisión (<see cref="Reserva.LastRegressionReason"/> +
/// <see cref="Reserva.LastRegressionAt"/>) solo deben verse mientras la reserva está VIVA y el dueño todavía no dio el
/// OK. Antes cada uno de los ~15 lugares que cambiaban <c>Reserva.Status</c> a mano decidía por su cuenta si limpiaba
/// o no — y casi ninguno lo hacía. Consecuencia (bugs auditoría 2026-07-04): una reserva anulada o revertida a
/// Presupuesto seguía mostrando el cartel "Se editaron precios..." y quedaba trabada para pasar a viaje si se
/// reabría. Esta tabla centraliza la decisión en UN solo lugar puro, y el
/// <c>ReservaStatusTransitioner</c> (Infrastructure) la aplica en cada transición.</para>
///
/// <para><b>Es PURA a propósito</b>: no toca EF ni la base. Solo describe QUÉ limpiar (con flags); el borrado real de
/// las filas <c>ReservaPendingChanges</c> lo hace el transicionador, que sí tiene el <c>DbContext</c>. Así esta regla
/// se puede testear sin base y no divergen dos criterios.</para>
/// </summary>
public static class ReservaStateCleanupRules
{
    /// <summary>
    /// Qué marcas hay que limpiar al entrar a un estado. Cada flag es independiente.
    /// </summary>
    /// <param name="ClearUnacknowledgedChanges">
    /// Apagar la marca "confirmada con cambios" (<c>HasUnacknowledgedChanges = false</c> +
    /// <c>ChangesPendingSince = null</c>). Va de la mano con <see cref="ClearPendingChangeRows"/>: no tiene sentido
    /// apagar la marca y dejar vivas las filas de detalle.
    /// </param>
    /// <param name="ClearPendingChangeRows">
    /// Borrar las filas de detalle <c>ReservaPendingChanges</c> ("qué precio cambió, de cuánto a cuánto"). Requiere
    /// acceso a la base, por eso lo ejecuta el transicionador y no esta regla pura.
    /// </param>
    /// <param name="ClearLastRegression">
    /// Limpiar el motivo de revisión (<c>LastRegressionReason = null</c> + <c>LastRegressionAt = null</c>).
    /// </param>
    public readonly record struct Cleanup(
        bool ClearUnacknowledgedChanges,
        bool ClearPendingChangeRows,
        bool ClearLastRegression);

    /// <summary>
    /// Devuelve qué limpiar cuando la reserva ENTRA al estado <paramref name="toStatus"/>.
    ///
    /// <para>Reglas (decisión del dueño, ADR-027 + auditoría 2026-07-04):
    ///   - Terminales de cancelación/cierre/descarte (<b>Cancelled, PendingOperatorRefund, Closed, Lost</b>): la
    ///     revisión ya no tiene sentido → apagar la marca + borrar el detalle. NO se toca el motivo de revisión
    ///     (<c>LastRegression*</c>): en estos estados es historial informativo inocuo y su limpieza no depende de
    ///     la transición.
    ///   - Reverts a pre-venta (<b>Budget, Quotation</b>): la reserva vuelve atrás en el ciclo → apagar la marca +
    ///     borrar el detalle + limpiar también el motivo de revisión (arrancan de cero como pre-venta).
    ///   - <b>Confirmed</b>: SOLO se limpia el motivo de revisión. CRÍTICO: entrar a Confirmed <b>NUNCA</b> apaga la
    ///     marca "confirmada con cambios" — esa marca VIVE justamente en Confirmed y solo la baja una persona con el
    ///     OK (endpoint acknowledge-changes). Que los servicios se resuelvan solos no significa que alguien revisó.
    ///   - Cualquier otro estado (InManagement, Traveling, Archived, desconocidos): no se limpia nada.</para>
    /// </summary>
    public static Cleanup For(string? toStatus)
    {
        // Terminales: la reserva se anula, cierra o descarta. La marca de revisión pierde sentido, pero el motivo
        // de revisión queda como historial (no molesta y no depende de la transición).
        if (Is(toStatus, EstadoReserva.Cancelled)
            || Is(toStatus, EstadoReserva.PendingOperatorRefund)
            || Is(toStatus, EstadoReserva.Closed)
            || Is(toStatus, EstadoReserva.Lost))
        {
            return new Cleanup(
                ClearUnacknowledgedChanges: true,
                ClearPendingChangeRows: true,
                ClearLastRegression: false);
        }

        // Revert a pre-venta: la reserva vuelve atrás. Se limpia TODO lo de revisión, incluido el motivo.
        if (Is(toStatus, EstadoReserva.Budget) || Is(toStatus, EstadoReserva.Quotation))
        {
            return new Cleanup(
                ClearUnacknowledgedChanges: true,
                ClearPendingChangeRows: true,
                ClearLastRegression: true);
        }

        // Confirmed: solo el motivo de revisión. La marca "confirmada con cambios" se conserva SIEMPRE.
        if (Is(toStatus, EstadoReserva.Confirmed))
        {
            return new Cleanup(
                ClearUnacknowledgedChanges: false,
                ClearPendingChangeRows: false,
                ClearLastRegression: true);
        }

        // Resto (InManagement, Traveling, Archived, valores no contemplados): no se limpia nada.
        return new Cleanup(
            ClearUnacknowledgedChanges: false,
            ClearPendingChangeRows: false,
            ClearLastRegression: false);
    }

    // Comparación de estado tolerante a mayúsculas/minúsculas, igual que el resto del ciclo de vida.
    private static bool Is(string? status, string expected)
        => string.Equals(status, expected, System.StringComparison.OrdinalIgnoreCase);
}
