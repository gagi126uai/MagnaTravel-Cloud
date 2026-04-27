namespace TravelApi.Domain.Entities;

public static class ReservationEconomicPolicy
{
    public static bool IsEconomicallySettled(Reserva reserva)
    {
        return RoundCurrency(reserva.Balance) <= 0m;
    }

    public static bool HasOutstandingBalance(Reserva reserva)
    {
        return !IsEconomicallySettled(reserva);
    }

    public static decimal RoundCurrency(decimal amount)
    {
        return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
    }
}
