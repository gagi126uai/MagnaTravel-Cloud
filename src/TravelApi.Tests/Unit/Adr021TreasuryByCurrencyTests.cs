using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-021 Capa 6 (multimoneda, 2026-06-10): tests del desglose POR MONEDA de tesoreria.
///
/// <para>Cubre: (a) cuentas por cobrar por moneda del SALDO contra la tabla hija; (b) caja real por
/// moneda REAL del cobro/egreso (un cobro cruzado entra a caja en su moneda real, no en la imputada);
/// (c) REGRESION: una instalacion 100% ARS produce un solo item ARS que coincide con el escalar.</para>
/// </summary>
public class Adr021TreasuryByCurrencyTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public Adr021TreasuryByCurrencyTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private static TreasuryService BuildService(AppDbContext context) => new(context, null!);

    [Fact]
    public async Task GetSummary_AccountsReceivableByCurrency_AggregatesAgainstChildTable()
    {
        await using var context = new AppDbContext(_dbOptions);

        // Dos reservas activas con saldo: una ARS, una con saldo en ambas monedas.
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed, Balance = 500m },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", Status = EstadoReserva.Confirmed, Balance = 300m });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m, ConfirmedSale = 500m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", Balance = 300m, ConfirmedSale = 300m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "USD", Balance = 100m, ConfirmedSale = 100m });
        await context.SaveChangesAsync();

        var summary = await BuildService(context).GetSummaryAsync(CancellationToken.None);

        var ars = summary.AccountsReceivableByCurrency.Single(x => x.Currency == "ARS");
        var usd = summary.AccountsReceivableByCurrency.Single(x => x.Currency == "USD");
        Assert.Equal(800m, ars.Amount); // 500 + 300
        Assert.Equal(100m, usd.Amount);
    }

    [Fact]
    public async Task GetSummary_CashByCurrency_UsesRealPaymentCurrency()
    {
        await using var context = new AppDbContext(_dbOptions);
        var now = DateTime.UtcNow;

        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed });

        // Cobro cruzado: entra a caja en ARS (moneda real), aunque impute a un saldo USD.
        context.Payments.Add(new Payment
        {
            ReservaId = 1, Amount = 100000m, Currency = "ARS", ImputedCurrency = "USD",
            ImputedAmount = 100m, ExchangeRate = 1000m, ExchangeRateSource = ExchangeRateSource.Manual,
            ExchangeRateAt = now, PaidAt = now, Status = "Paid", AffectsCash = true
        });
        // Cobro USD directo.
        context.Payments.Add(new Payment
        {
            ReservaId = 1, Amount = 50m, Currency = "USD", PaidAt = now, Status = "Paid", AffectsCash = true
        });
        await context.SaveChangesAsync();

        var summary = await BuildService(context).GetSummaryAsync(CancellationToken.None);

        var arsIn = summary.CashInByCurrency.Single(x => x.Currency == "ARS");
        var usdIn = summary.CashInByCurrency.Single(x => x.Currency == "USD");
        Assert.Equal(100000m, arsIn.Amount); // caja real ARS (NO el imputado USD 100)
        Assert.Equal(50m, usdIn.Amount);
    }

    [Fact]
    public async Task GetSummary_MonoArs_ByCurrencyMatchesScalar_Regression()
    {
        await using var context = new AppDbContext(_dbOptions);
        var now = DateTime.UtcNow;

        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", Status = EstadoReserva.Confirmed, Balance = 400m });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 400m, ConfirmedSale = 400m });
        context.Payments.Add(new Payment { ReservaId = 1, Amount = 200m, Currency = "ARS", PaidAt = now, Status = "Paid", AffectsCash = true });
        await context.SaveChangesAsync();

        var summary = await BuildService(context).GetSummaryAsync(CancellationToken.None);

        // Mono-ARS: una sola linea por moneda que coincide con el escalar de compat.
        var arReceivable = Assert.Single(summary.AccountsReceivableByCurrency);
        Assert.Equal("ARS", arReceivable.Currency);
        Assert.Equal(summary.AccountsReceivable, arReceivable.Amount);

        var cashIn = Assert.Single(summary.CashInByCurrency);
        Assert.Equal("ARS", cashIn.Currency);
        Assert.Equal(summary.CashInThisMonth, cashIn.Amount);
    }
}
