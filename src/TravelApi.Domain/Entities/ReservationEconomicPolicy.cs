namespace TravelApi.Domain.Entities;

public static class ReservationEconomicPolicy
{
    /// <summary>
    /// ADR-036 (2026-06-21, prepago puro): mensaje cuando se intenta pasar a "En viaje" (Traveling) una
    /// reserva que todavia le debe al cliente. Sin datos sensibles (ni montos): el front muestra el cartel,
    /// el monto NO se filtra a callers sin permiso de costo.
    /// </summary>
    public const string ClientNotFullyPaidForTravelingMessage =
        "No se puede pasar a En viaje: el cliente todavía tiene saldo pendiente. Debe quedar saldado antes de viajar.";

    public static bool IsEconomicallySettled(Reserva reserva)
    {
        return IsClientFullyPaid(reserva.Balance);
    }

    public static bool HasOutstandingBalance(Reserva reserva)
    {
        return !IsEconomicallySettled(reserva);
    }

    /// <summary>
    /// ADR-036 (2026-06-21, prepago puro): CANDADO DURO de pase a "En viaje" por el lado del CLIENTE, para los
    /// clientes en modo PREPAGO. La reserva solo puede entrar a Traveling si el cliente quedo SALDADO (Balance
    /// &lt;= 0, con tolerancia de redondeo). Esta regla NO depende de la llave
    /// <c>RequireFullPaymentForOperativeStatus</c>: en el modelo prepago el cliente paga el 100% antes de viajar.
    ///
    /// <para><b>ADR-040 (cuenta corriente, 2026-06-26): este candado ya NO es "incondicional para todos"</b>. Es
    /// el branch PREPAGO. Para clientes en modo <c>Account</c> (cuenta corriente) el candado de "saldado" se
    /// REEMPLAZA por <c>ClientCreditPolicy.EvaluateCanTravel</c> (puede viajar debiendo dentro de su limite por
    /// moneda). La bifurcacion por modo vive en el caller (ReservaService.EnsureCanStartTravelingAsync y el job);
    /// esta funcion queda intacta y byte-identica — sigue siendo el camino prepago, cero cambios.</para>
    ///
    /// <para>Es una funcion PURA que recibe el Balance YA MATERIALIZADO (el escalar surrogate de la reserva),
    /// para que la usen identico el pase MANUAL (ReservaService) y el JOB automatico
    /// (ReservaLifecycleAutomationService) sin divergir. El operador NO entra aca: por limitacion de datos
    /// (SupplierPayment.ReservaId nullable) la deuda con el operador es solo AVISO, no traba el viaje.</para>
    /// </summary>
    public static bool IsClientFullyPaid(decimal balance)
    {
        return RoundCurrency(balance) <= 0m;
    }

    public static decimal RoundCurrency(decimal amount)
    {
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}
