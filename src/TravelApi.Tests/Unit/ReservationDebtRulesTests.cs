using System;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Regla ÚNICA de "deuda de una reserva" (chip "Vencida con deuda" + contexto de plata en anuladas).
/// Fija el bug de la auditoría 2026-07-04: una reserva ANULADA con deuda congelada NO debe figurar como
/// "vencida con deuda" (antes se calculaba solo por fecha+saldo, sin mirar el estado).
/// </summary>
public class ReservationDebtRulesTests
{
    // "Hoy" fijo para que los tests no dependan del reloj real.
    private static readonly DateTime TodayUtc = new(2026, 07, 04, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PastEnd = new(2026, 06, 01, 0, 0, 0, DateTimeKind.Utc);   // viaje terminado
    private static readonly DateTime FutureEnd = new(2026, 08, 01, 0, 0, 0, DateTimeKind.Utc); // viaje futuro

    // ===================== HasOverdueDebt: matriz estado × saldo × fecha =====================

    [Fact]
    public void HasOverdueDebt_Cancelled_WithFrozenOverdueDebt_IsFalse()
    {
        // El corazón del bug: anulada + con deuda + fecha pasada NO es "vencida con deuda".
        var result = ReservationDebtRules.HasOverdueDebt(
            EstadoReserva.Cancelled, PastEnd, isEconomicallySettled: false, TodayUtc);

        Assert.False(result);
    }

    [Fact]
    public void HasOverdueDebt_PendingOperatorRefund_WithOverdueDebt_IsFalse()
    {
        var result = ReservationDebtRules.HasOverdueDebt(
            EstadoReserva.PendingOperatorRefund, PastEnd, isEconomicallySettled: false, TodayUtc);

        Assert.False(result);
    }

    [Fact]
    public void HasOverdueDebt_Lost_WithOverdueDebt_IsFalse()
    {
        var result = ReservationDebtRules.HasOverdueDebt(
            EstadoReserva.Lost, PastEnd, isEconomicallySettled: false, TodayUtc);

        Assert.False(result);
    }

    [Fact]
    public void HasOverdueDebt_Closed_WithOverdueDebt_IsTrue()
    {
        // Una reserva Finalizada con deuda real SÍ es "vencida con deuda" (cuenta por cobrar legítima).
        var result = ReservationDebtRules.HasOverdueDebt(
            EstadoReserva.Closed, PastEnd, isEconomicallySettled: false, TodayUtc);

        Assert.True(result);
    }

    [Fact]
    public void HasOverdueDebt_Confirmed_WithDebtButFutureDate_IsFalse()
    {
        // Estado cobrable y con deuda, pero el viaje todavía no terminó: no está vencida.
        var result = ReservationDebtRules.HasOverdueDebt(
            EstadoReserva.Confirmed, FutureEnd, isEconomicallySettled: false, TodayUtc);

        Assert.False(result);
    }

    [Fact]
    public void HasOverdueDebt_Closed_ButSettled_IsFalse()
    {
        // Finalizada y saldada (sin deuda): no hay nada vencido.
        var result = ReservationDebtRules.HasOverdueDebt(
            EstadoReserva.Closed, PastEnd, isEconomicallySettled: true, TodayUtc);

        Assert.False(result);
    }

    [Theory]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Closed, true)]
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.Traveling, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void IsDebtCollectableStatus_MatchesSaleFirmSet(string status, bool expected)
    {
        Assert.Equal(expected, ReservationDebtRules.IsDebtCollectableStatus(status));
    }

    // ===================== DeriveForCancelled: los 4 casos =====================

    [Fact]
    public void DeriveForCancelled_NegativeBalance_IsClientCreditPending()
    {
        // Saldo a favor del cliente sin devolver: da igual si hay ND o no, gana el crédito.
        var context = ReservationDebtRules.DeriveForCancelled(balance: -500m, hasOutstandingDebitNote: false);

        Assert.Equal(ReservationDebtRules.CancelledMoneyContext.ClientCreditPending, context);
    }

    [Fact]
    public void DeriveForCancelled_PositiveBalance_WithLiveDebitNote_IsPenaltyReceivable()
    {
        var context = ReservationDebtRules.DeriveForCancelled(balance: 300m, hasOutstandingDebitNote: true);

        Assert.Equal(ReservationDebtRules.CancelledMoneyContext.PenaltyReceivable, context);
    }

    [Fact]
    public void DeriveForCancelled_PositiveBalance_WithoutDebitNote_IsInconsistent()
    {
        // Deuda sin comprobante que la justifique = dato roto (lo detectará el vigía).
        var context = ReservationDebtRules.DeriveForCancelled(balance: 300m, hasOutstandingDebitNote: false);

        Assert.Equal(ReservationDebtRules.CancelledMoneyContext.Inconsistent, context);
    }

    [Fact]
    public void DeriveForCancelled_ZeroBalance_IsNone()
    {
        var context = ReservationDebtRules.DeriveForCancelled(balance: 0m, hasOutstandingDebitNote: false);

        Assert.Equal(ReservationDebtRules.CancelledMoneyContext.None, context);
    }

    [Fact]
    public void DeriveForCancelled_CentRounding_IsNone()
    {
        // Un resto de centavo (por conversión de moneda) NO es deuda ni saldo a favor real.
        Assert.Equal(
            ReservationDebtRules.CancelledMoneyContext.None,
            ReservationDebtRules.DeriveForCancelled(balance: 0.004m, hasOutstandingDebitNote: false));
        Assert.Equal(
            ReservationDebtRules.CancelledMoneyContext.None,
            ReservationDebtRules.DeriveForCancelled(balance: -0.004m, hasOutstandingDebitNote: true));
    }

    [Theory]
    [InlineData(ReservationDebtRules.CancelledMoneyContext.None, null)]
    [InlineData(ReservationDebtRules.CancelledMoneyContext.ClientCreditPending, "SaldoAFavorPendiente")]
    [InlineData(ReservationDebtRules.CancelledMoneyContext.PenaltyReceivable, "MultaPorCobrar")]
    [InlineData(ReservationDebtRules.CancelledMoneyContext.Inconsistent, "Inconsistente")]
    public void ToDtoString_MapsContractStrings(ReservationDebtRules.CancelledMoneyContext context, string? expected)
    {
        Assert.Equal(expected, ReservationDebtRules.ToDtoString(context));
    }

    // ===================== Coherencia cruzada de sets de estado =====================

    /// <summary>
    /// El AR/cobranza (FinancePositionService.ReceivableDebtStatuses) NO debe divergir del set canónico de
    /// venta firme (EstadoReserva.SaleFirmStatuses), que es la base del predicado de deuda cobrable. Si alguien
    /// cambia uno sin el otro, este test rompe. Hoy son la MISMA referencia (alias), y además todo estado del
    /// dominio clasifica igual por ambos caminos.
    /// </summary>
    [Fact]
    public void ReceivableDebtStatuses_DoesNotDivergeFromSaleFirmSet()
    {
        Assert.Equal(EstadoReserva.SaleFirmStatuses, FinancePositionService.ReceivableDebtStatuses);

        // Y el predicado del dominio coincide con la pertenencia al array de AR para TODOS los estados.
        string[] allStatuses =
        {
            EstadoReserva.Quotation, EstadoReserva.Budget, EstadoReserva.InManagement,
            EstadoReserva.Confirmed, EstadoReserva.Traveling, EstadoReserva.Closed,
            EstadoReserva.Lost, EstadoReserva.Cancelled, EstadoReserva.PendingOperatorRefund,
        };

        foreach (var status in allStatuses)
        {
            bool inReceivableArray = Array.Exists(
                FinancePositionService.ReceivableDebtStatuses,
                s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));

            Assert.Equal(inReceivableArray, ReservationDebtRules.IsDebtCollectableStatus(status));
        }
    }
}
