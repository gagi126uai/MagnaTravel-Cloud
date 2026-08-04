using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

internal static class EconomicRulesHelper
{
    public static bool IsEconomicallySettled(Reserva reserva)
    {
        return ReservationEconomicPolicy.IsEconomicallySettled(reserva);
    }

    public static string? GetOperativeBlockReason(Reserva reserva, OperationalFinanceSettings settings)
    {
        if (!settings.RequireFullPaymentForOperativeStatus)
            return null;

        return IsEconomicallySettled(reserva)
            ? null
            : "La reserva no puede pasar a Operativo mientras tenga saldo pendiente.";
    }

    public static string? GetVoucherBlockReason(Reserva reserva, OperationalFinanceSettings settings)
    {
        if (string.Equals(reserva.Status, EstadoReserva.Budget, StringComparison.OrdinalIgnoreCase))
            return "No se puede emitir el voucher mientras la reserva siga en Presupuesto.";

        var isOperationallyConfirmed =
            string.Equals(reserva.Status, EstadoReserva.Traveling, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(reserva.Status, EstadoReserva.Closed, StringComparison.OrdinalIgnoreCase);

        if (!isOperationallyConfirmed)
            return "No se puede emitir el voucher hasta que la reserva este confirmada operativamente.";

        if (!settings.RequireFullPaymentForVoucher)
            return null;

        return IsEconomicallySettled(reserva)
            ? null
            : "No se puede emitir el voucher mientras la reserva tenga deuda.";
    }

    public static (bool CanEmit, string? BlockReason, bool RequiresOverride) EvaluateAfip(Reserva reserva, OperationalFinanceSettings settings)
    {
        if (IsEconomicallySettled(reserva))
            return (true, null, false);

        if (settings.AfipInvoiceControlMode == AfipInvoiceControlModes.AllowAgentOverrideWithReason)
        {
            return (false, "La reserva tiene deuda. AFIP queda bloqueado por defecto y requiere override con motivo.", true);
        }

        return (false, "La reserva no esta cancelada economicamente y no puede emitirse en AFIP.", false);
    }

    public static string? GetCombinedEconomicBlockReason(Reserva reserva, OperationalFinanceSettings settings)
    {
        return GetOperativeBlockReason(reserva, settings)
            ?? GetVoucherBlockReason(reserva, settings)
            ?? EvaluateAfip(reserva, settings).BlockReason;
    }

    public static string? GetEmptyReservaBlockReason(Reserva reserva)
    {
        var hasServices = (reserva.Servicios?.Any() ?? false)
            || (reserva.HotelBookings?.Any() ?? false)
            || (reserva.FlightSegments?.Any() ?? false)
            || (reserva.TransferBookings?.Any() ?? false)
            || (reserva.PackageBookings?.Any() ?? false)
            || (reserva.AssistanceBookings?.Any() ?? false);

        return hasServices ? null : "La reserva debe tener al menos un servicio cargado.";
    }

    public static string? GetArchiveBlockReason(Reserva reserva)
    {
        // Tanda 1 rediseño listado (2026-08-04, plan A5): "Operativo" era jerga interna (nombre viejo
        // del estado Traveling) que no coincide con el termino que el resto de la pantalla usa para
        // ese mismo estado ("En viaje", ver RESERVA_STATUS_LABELS en el front). "Cerradas" pasa a
        // "Finalizadas" por la misma razon: son los DOS UNICOS labels de estado que el negocio conoce.
        if (reserva.Status != EstadoReserva.Traveling && reserva.Status != EstadoReserva.Closed)
            return "Solo se pueden archivar reservas que estén En viaje o Finalizadas.";

        if (Math.Round(reserva.Balance, 2, MidpointRounding.AwayFromZero) > 0m)
            return "No se puede archivar una reserva con saldo pendiente.";

        return null;
    }

    public static decimal RoundCurrency(decimal amount)
    {
        return ReservationEconomicPolicy.RoundCurrency(amount);
    }
}
