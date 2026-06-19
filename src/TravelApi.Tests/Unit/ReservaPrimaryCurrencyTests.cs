using System.Collections.Generic;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-035 (2026-06-19): regla pura para elegir la moneda PRINCIPAL del cobro (la de mayor saldo pendiente).
/// La comparten el detalle de la reserva y la worklist de cobranza; estos tests fijan el criterio y el desempate.
/// </summary>
public class ReservaPrimaryCurrencyTests
{
    [Fact]
    public void SinLineas_DevuelveNull()
    {
        Assert.Null(ReservaPrimaryCurrency.Resolve(new List<(string, decimal)>()));
    }

    [Fact]
    public void UnaSolaMoneda_DevuelveEsaMoneda()
    {
        var result = ReservaPrimaryCurrency.Resolve(new List<(string, decimal)> { ("USD", 500m) });
        Assert.Equal("USD", result);
    }

    [Fact]
    public void Multimoneda_DevuelveLaDeMayorDeuda()
    {
        // ARS debe 200, USD debe 700 -> USD, aunque ARS venga primero.
        var lines = new List<(string, decimal)> { ("ARS", 200m), ("USD", 700m) };
        Assert.Equal("USD", ReservaPrimaryCurrency.Resolve(lines));
    }

    [Fact]
    public void Multimoneda_SoloUnaDebe_DevuelveLaQueDebe()
    {
        // ARS saldada (0), USD debe 100 -> USD aunque ARS venga primero y tenga 0.
        var lines = new List<(string, decimal)> { ("ARS", 0m), ("USD", 100m) };
        Assert.Equal("USD", ReservaPrimaryCurrency.Resolve(lines));
    }

    [Fact]
    public void EmpateDeDeuda_DevuelveLaPrimeraSegunOrden()
    {
        // Ambas deben 300; el desempate se queda con la PRIMERA del orden (ARS, alfabetico estable).
        var lines = new List<(string, decimal)> { ("ARS", 300m), ("USD", 300m) };
        Assert.Equal("ARS", ReservaPrimaryCurrency.Resolve(lines));
    }

    [Fact]
    public void NingunaDebe_DevuelveLaDeMayorSaldoAFavorEnAbsoluto()
    {
        // ARS saldo a favor -100, USD saldo a favor -500 -> default = USD (mayor en valor absoluto).
        var lines = new List<(string, decimal)> { ("ARS", -100m), ("USD", -500m) };
        Assert.Equal("USD", ReservaPrimaryCurrency.Resolve(lines));
    }
}
