using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Services;

internal static class EconomicRulesHelper
{
    public static bool IsEconomicallySettled(Reserva reserva)
    {
        return Math.Round(reserva.Balance, 2, MidpointRounding.AwayFromZero) <= 0m;
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

    public static decimal RoundCurrency(decimal amount)
    {
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}
