using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-040 (cuenta corriente del cliente, 2026-06-26): cobertura de INTEGRACION (InMemory) del MOTOR de cuenta
/// corriente. Cubre los 4 arreglos del review:
///   - B1: la exposicion de credito INCLUYE las reservas ya "En viaje" (un hermano Traveling cuenta).
///   - B2: el job re-evalua el credito al aplicar (rama Account del confirmed->traveling).
///   - B3: el limite vive en la tabla por moneda y la ausencia de moneda = prepago.
///   - B4: un cliente a cuenta CIERRA con deuda; la comision NO devenga; un prepago con deuda NO cierra.
///   - Regresion: un cliente PREPAGO se comporta byte-identico (no viaja/cierra debiendo).
/// </summary>
public class Adr040AccountCreditEngineTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReservaLifecycleAutomationService NewJob(AppDbContext context, OperationalFinanceSettings settings)
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaLifecycleAutomationService(
            context, NullLogger<ReservaLifecycleAutomationService>.Instance, mock.Object, engine);
    }

    private static OperationalFinanceSettings Settings(
        CustomerBillingMode agencyDefault = CustomerBillingMode.Prepaid,
        bool blockWhenOverLimit = true)
        => new()
        {
            DefaultCustomerBillingMode = agencyDefault,
            BlockTravelWhenCreditExceeded = blockWhenOverLimit
        };

    private static Customer SeedCustomer(AppDbContext ctx, int id, CustomerBillingMode? mode,
        params (string Currency, decimal Limit)[] limits)
    {
        var customer = new Customer { Id = id, FullName = $"Cliente {id}", BillingMode = mode };
        ctx.Customers.Add(customer);
        foreach (var (currency, limit) in limits)
            ctx.CustomerCreditLimitByCurrency.Add(new CustomerCreditLimitByCurrency
            {
                CustomerId = id,
                Currency = currency,
                Limit = limit
            });
        return customer;
    }

    /// <summary>Reserva Confirmed lista para que el job intente promoverla (StartDate hoy, 1 servicio, sin pax).</summary>
    private static void SeedConfirmedReadyToTravel(AppDbContext ctx, int id, int payerId, decimal scalarBalance)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = id,
            NumeroReserva = $"R-40-{id}",
            Name = $"Reserva {id}",
            Status = EstadoReserva.Confirmed,
            PayerId = payerId,
            ResponsibleUserId = "seller-1",
            ResponsibleUserName = "Seller",
            StartDate = DateTime.UtcNow.Date,
            Balance = scalarBalance
        });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 1000 + id, ReservaId = id, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow
        });
    }

    private static void SeedMoney(AppDbContext ctx, int reservaId, string currency, decimal balance)
        => ctx.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reservaId,
            Currency = currency,
            ConfirmedSale = balance,
            Balance = balance
        });

    // ===================== Confirmed -> Traveling (rama Account) =====================

    [Fact]
    public async Task Job_Account_WithinLimit_Promotes()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 300_000m);
        SeedMoney(ctx, 1, "ARS", 300_000m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Traveling, reserva!.Status);
    }

    [Fact]
    public async Task Job_Account_OverLimit_Block_DoesNotPromote()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 600_000m);
        SeedMoney(ctx, 1, "ARS", 600_000m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings(blockWhenOverLimit: true)).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status); // queda esperando que regularice
    }

    [Fact]
    public async Task Job_Account_OverLimit_WarnOnly_PromotesAnyway()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 600_000m);
        SeedMoney(ctx, 1, "ARS", 600_000m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings(blockWhenOverLimit: false)).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Traveling, reserva!.Status);
    }

    [Fact]
    public async Task Job_Account_DebtInCurrencyWithoutLimit_DoesNotPromote()
    {
        await using var ctx = NewContext();
        // Limite solo en ARS; debe en USD -> USD es prepago -> bloquea.
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 0m);
        SeedMoney(ctx, 1, "USD", 200m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
    }

    [Fact]
    public async Task Job_Account_SiblingTravelingDebt_CountsAgainstLimit_B1()
    {
        await using var ctx = NewContext();
        // Limite 500k. La candidata sola debe 100k (dentro). Pero un HERMANO ya "En viaje" debe 450k.
        // Total 550k > 500k -> la candidata NO promueve. Prueba que la exposicion INCLUYE Traveling (B1).
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 100_000m);
        SeedMoney(ctx, 1, "ARS", 100_000m);

        ctx.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "R-40-2", Name = "Hermano en viaje",
            Status = EstadoReserva.Traveling, PayerId = 1, Balance = 450_000m
        });
        SeedMoney(ctx, 2, "ARS", 450_000m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
    }

    [Fact]
    public async Task Job_Account_WithoutSibling_SameDebt_Promotes_ProvesSiblingWasTheCause()
    {
        await using var ctx = NewContext();
        // Igual que el test B1 pero SIN el hermano: 100k dentro de 500k -> promueve. Confirma que lo que
        // bloqueaba en el otro test era la deuda del hermano en viaje, no la propia.
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 100_000m);
        SeedMoney(ctx, 1, "ARS", 100_000m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Traveling, reserva!.Status);
    }

    // ===================== Regresion prepago (debe seguir byte-identico) =====================

    [Theory]
    [InlineData(true)]   // BillingMode explicito Prepaid
    [InlineData(false)]  // BillingMode null -> hereda default de agencia (Prepaid)
    public async Task Job_Prepaid_WithDebt_DoesNotPromote(bool explicitPrepaid)
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, explicitPrepaid ? CustomerBillingMode.Prepaid : (CustomerBillingMode?)null);
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 100m); // debe
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings(agencyDefault: CustomerBillingMode.Prepaid)).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
    }

    [Fact]
    public async Task Job_Prepaid_FullyPaid_Promotes()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Prepaid);
        SeedConfirmedReadyToTravel(ctx, 1, payerId: 1, scalarBalance: 0m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionConfirmedToTravelingAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Traveling, reserva!.Status);
    }

    // ===================== I1 (review): reserva sin pagador + default Account = NO NRE =====================

    [Fact]
    public async Task Job_DefaultAccount_ReservaWithoutPayer_DoesNotPromote_AndDoesNotThrow()
    {
        await using var ctx = NewContext();
        // Reserva Confirmed lista para viajar pero SIN pagador (PayerId null), con el default de agencia en
        // Account. Antes del fix, la rama Account dereferenciaba PayerId!.Value -> NRE que tumbaba TODA la fase.
        // Ahora PayerId null se trata como Prepaid: debe 100 -> NO viaja, y NO tira excepcion.
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "R-NOPAYER", Name = "Sin pagador",
            Status = EstadoReserva.Confirmed, PayerId = null,
            StartDate = DateTime.UtcNow.Date, Balance = 100m
        });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 1001, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        // Llamada DIRECTA a la fase (no via RunPhaseSafelyAsync): si hubiera NRE, propagaria y romperia el test.
        var promoted = await NewJob(ctx, Settings(agencyDefault: CustomerBillingMode.Account))
            .AutoTransitionConfirmedToTravelingAsync();

        Assert.Equal(0, promoted);
        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Confirmed, reserva!.Status);
    }

    // ===================== B2: el recheck re-lee la exposicion FRESCA y decide con el dato nuevo =========

    [Fact]
    public async Task EvaluateCanTravelAsync_ReReadsFreshExposure_BlocksAfterNewDebtAppears()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "A", Name = "A", Status = EstadoReserva.Confirmed, PayerId = 1, Balance = 100_000m });
        SeedMoney(ctx, 1, "ARS", 100_000m);
        await ctx.SaveChangesAsync();

        var settings = Settings();

        // Plan: dentro del limite (100k <= 500k) -> permite.
        var first = await ClientCreditGate.EvaluateCanTravelAsync(ctx, customerId: 1, thisReservaBalance: 100_000m, settings, CancellationToken.None);
        Assert.True(first.Allowed);

        // Entre el plan y el "commit" aparece deuda nueva en OTRA reserva del mismo cliente (un cajero la cargo).
        ctx.Reservas.Add(new Reserva { Id = 2, NumeroReserva = "B", Name = "B", Status = EstadoReserva.Confirmed, PayerId = 1, Balance = 450_000m });
        SeedMoney(ctx, 2, "ARS", 450_000m);
        await ctx.SaveChangesAsync();

        // La MISMA evaluacion ahora RE-LEE la exposicion fresca (100k + 450k = 550k > 500k) y BLOQUEA. Este es el
        // mecanismo exacto del recheck B2 del job: decide con el dato nuevo, no con uno cacheado del plan.
        var second = await ClientCreditGate.EvaluateCanTravelAsync(ctx, 1, 100_000m, settings, CancellationToken.None);
        Assert.False(second.Allowed);
    }

    // ===================== B4 coherencia: Account cerrado con deuda sigue en el AR canonico =============

    [Fact]
    public async Task ClosedAccountWithDebt_StillCountedInCustomerReceivable()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "C", Name = "C", Status = EstadoReserva.Closed, PayerId = 1, Balance = 200_000m });
        SeedMoney(ctx, 1, "ARS", 200_000m);
        await ctx.SaveChangesAsync();

        // ReceivableDebtStatuses incluye Closed: la deuda de un Account cerrado NO desaparece de la cartera, se
        // ve en el AR canonico (FinancePositionService) / cuenta corriente del cliente.
        var ar = await new FinancePositionService(ctx).GetCustomerReceivableByCurrencyAsync(1, CancellationToken.None);
        Assert.Equal(200_000m, ar.Single(x => x.Currency == "ARS").Amount);
    }

    // ===================== Traveling -> Closed (B4: cerrar con deuda) =====================

    private static void SeedTravelingEnded(AppDbContext ctx, int id, int payerId, decimal balance)
        => ctx.Reservas.Add(new Reserva
        {
            Id = id, NumeroReserva = $"R-40C-{id}", Name = $"Fin {id}",
            Status = EstadoReserva.Traveling, PayerId = payerId,
            EndDate = DateTime.UtcNow.Date.AddDays(-1), Balance = balance
        });

    [Fact]
    public async Task Job_Account_ClosesWithDebt_B4()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Account, ("ARS", 500_000m));
        SeedTravelingEnded(ctx, 1, payerId: 1, balance: 200_000m); // debe, pero es a cuenta
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionTravelingToClosedAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Closed, reserva!.Status);
        Assert.NotNull(reserva.ClosedAt);
    }

    [Fact]
    public async Task Job_Prepaid_WithDebt_DoesNotClose()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Prepaid);
        SeedTravelingEnded(ctx, 1, payerId: 1, balance: 200_000m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionTravelingToClosedAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Traveling, reserva!.Status); // queda "Vencida con deuda"
    }

    [Fact]
    public async Task Job_Prepaid_Settled_Closes()
    {
        await using var ctx = NewContext();
        SeedCustomer(ctx, 1, CustomerBillingMode.Prepaid);
        SeedTravelingEnded(ctx, 1, payerId: 1, balance: 0m);
        await ctx.SaveChangesAsync();

        await NewJob(ctx, Settings()).AutoTransitionTravelingToClosedAsync();

        var reserva = await ctx.Reservas.FindAsync(1);
        Assert.Equal(EstadoReserva.Closed, reserva!.Status);
    }

    // ===================== B4: cerrar a cuenta con deuda NO devenga comision =====================

    [Fact]
    public void Commission_ClosedWithDebt_DoesNotAccrue()
    {
        // El calculador de comision (fuente unica del devengo) corta si Balance > 0, sin importar el estado.
        // Asi, una reserva a cuenta CERRADA con deuda no genera comision (la comision es al cobrar).
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "R", Name = "X",
            Status = EstadoReserva.Closed, ResponsibleUserId = "seller-1", Balance = 200_000m
        };

        var lines = SellerCommissionCalculator.Calculate(reserva, (_, _) => 10m);
        Assert.Empty(lines);
    }

    // ===================== B1 directo: el lector de exposicion incluye Traveling =====================

    [Fact]
    public async Task ExposureReader_IncludesTravelingReservas()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "A", Name = "A", Status = EstadoReserva.Confirmed, PayerId = 7, Balance = 100m });
        ctx.Reservas.Add(new Reserva { Id = 2, NumeroReserva = "B", Name = "B", Status = EstadoReserva.Traveling, PayerId = 7, Balance = 100m });
        ctx.Reservas.Add(new Reserva { Id = 3, NumeroReserva = "C", Name = "C", Status = EstadoReserva.Cancelled, PayerId = 7, Balance = 100m });
        SeedMoney(ctx, 1, "ARS", 100_000m);
        SeedMoney(ctx, 2, "ARS", 250_000m);
        SeedMoney(ctx, 3, "ARS", 999_000m); // Cancelled: NO debe contar
        await ctx.SaveChangesAsync();

        var exposure = await CustomerCreditExposureReader.GetExposureByCurrencyAsync(ctx, 7, CancellationToken.None);

        // Confirmed (100k) + Traveling (250k) = 350k. La Cancelled queda afuera.
        Assert.Equal(350_000m, exposure["ARS"]);
    }

    // ===================== Write-side: audita la config de cuenta corriente (sensible) =====================

    [Fact]
    public async Task UpdateCreditConfig_PersistsAndAudits()
    {
        await using var ctx = NewContext();
        ctx.Customers.Add(new Customer { Id = 1, FullName = "ACME" });
        await ctx.SaveChangesAsync();

        var audit = new Mock<IAuditService>();
        var service = new CustomerService(ctx, new FinancePositionService(ctx), audit.Object);

        await service.UpdateCustomerCreditConfigAsync(
            id: 1,
            billingMode: CustomerBillingMode.Account,
            paymentTermsDays: 30,
            creditLimitsByCurrency: new Dictionary<string, decimal> { ["ARS"] = 500_000m, ["USD"] = 1_000m },
            actorUserId: "admin-1",
            actorUserName: "Admin",
            CancellationToken.None);

        var customer = await ctx.Customers.FindAsync(1);
        Assert.Equal(CustomerBillingMode.Account, customer!.BillingMode);
        Assert.Equal(30, customer.PaymentTermsDays);

        var limits = await ctx.CustomerCreditLimitByCurrency.Where(l => l.CustomerId == 1).ToListAsync();
        Assert.Equal(2, limits.Count);
        Assert.Equal(500_000m, limits.Single(l => l.Currency == "ARS").Limit);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.CustomerCreditConfigUpdated,
            "Customer",
            "1",
            It.Is<string>(d => d.Contains("Account") && d.Contains("PaymentTermsDays: 0 -> 30") && d.Contains("ARS")),
            "admin-1",
            "Admin"), Times.Once);
    }

    [Fact]
    public async Task UpdateCreditConfig_RemovesCurrencyNotInDesiredState()
    {
        await using var ctx = NewContext();
        ctx.Customers.Add(new Customer { Id = 1, FullName = "ACME" });
        ctx.CustomerCreditLimitByCurrency.Add(new CustomerCreditLimitByCurrency { CustomerId = 1, Currency = "ARS", Limit = 100m });
        ctx.CustomerCreditLimitByCurrency.Add(new CustomerCreditLimitByCurrency { CustomerId = 1, Currency = "USD", Limit = 50m });
        await ctx.SaveChangesAsync();

        var service = new CustomerService(ctx, new FinancePositionService(ctx));

        // Estado deseado: solo ARS -> USD se borra.
        await service.UpdateCustomerCreditConfigAsync(
            1, CustomerBillingMode.Account, 0,
            new Dictionary<string, decimal> { ["ARS"] = 200_000m },
            "admin-1", "Admin", CancellationToken.None);

        var limits = await ctx.CustomerCreditLimitByCurrency.Where(l => l.CustomerId == 1).ToListAsync();
        Assert.Single(limits);
        Assert.Equal("ARS", limits[0].Currency);
        Assert.Equal(200_000m, limits[0].Limit);
    }

    [Fact]
    public async Task UpdateCreditConfig_NegativeLimit_Throws()
    {
        await using var ctx = NewContext();
        ctx.Customers.Add(new Customer { Id = 1, FullName = "ACME" });
        await ctx.SaveChangesAsync();

        var service = new CustomerService(ctx, new FinancePositionService(ctx));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateCustomerCreditConfigAsync(
            1, CustomerBillingMode.Account, 0,
            new Dictionary<string, decimal> { ["ARS"] = -1m },
            "admin-1", "Admin", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCreditConfig_ZeroLimit_DoesNotPersistRow_N1()
    {
        await using var ctx = NewContext();
        ctx.Customers.Add(new Customer { Id = 1, FullName = "ACME" });
        // El cliente tenia una fila USD; al re-configurar con USD:0 esa fila debe BORRARSE (0 = sin credito).
        ctx.CustomerCreditLimitByCurrency.Add(new CustomerCreditLimitByCurrency { CustomerId = 1, Currency = "USD", Limit = 50m });
        await ctx.SaveChangesAsync();

        var service = new CustomerService(ctx, new FinancePositionService(ctx));

        await service.UpdateCustomerCreditConfigAsync(
            1, CustomerBillingMode.Account, 0,
            new Dictionary<string, decimal> { ["ARS"] = 500_000m, ["USD"] = 0m },
            "admin-1", "Admin", CancellationToken.None);

        var limits = await ctx.CustomerCreditLimitByCurrency.Where(l => l.CustomerId == 1).ToListAsync();
        // Solo ARS se persiste; USD:0 no deja fila (ni la nueva ni la vieja).
        Assert.Single(limits);
        Assert.Equal("ARS", limits[0].Currency);
        Assert.Equal(500_000m, limits[0].Limit);
    }
}
