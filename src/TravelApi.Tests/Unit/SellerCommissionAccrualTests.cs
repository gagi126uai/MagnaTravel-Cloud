using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Auditoria ERP 2026-06-12 (hallazgo #1): tests del devengo de comision del vendedor. Como el persister
/// de comisiones se engancha al final de <see cref="ReservaMoneyPersister.PersistAsync"/>, probamos
/// end-to-end a traves de ese chokepoint (el mismo camino que dispara cualquier cambio de saldo en runtime).
///
/// <para>Cubre: toggle OFF = cero devengo (byte-identico); toggle ON + reserva cobrada = devenga % sobre
/// ganancia por moneda; sin vendedor = no devenga; cancelacion / saldo positivo = tope cero (revierte a 0,
/// nunca negativo); multimoneda = un devengo por moneda; idempotencia (recalcular no duplica); sin regla = 0.
/// Usa el provider InMemory (no requiere Postgres).</para>
/// </summary>
public class SellerCommissionAccrualTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>Crea la fila singleton de settings con el toggle de comision en el valor pedido.</summary>
    private static async Task SeedSettingsAsync(AppDbContext db, bool commissionsEnabled)
    {
        db.OperationalFinanceSettings.Add(new OperationalFinanceSettings { EnableSellerCommissions = commissionsEnabled });
        await db.SaveChangesAsync();
    }

    /// <summary>Crea una regla "default" (aplica a todo) con el % dado.</summary>
    private static async Task SeedDefaultRuleAsync(AppDbContext db, decimal percent)
    {
        db.CommissionRules.Add(new CommissionRule
        {
            SupplierId = null,
            ServiceType = null,
            CommissionPercent = percent,
            Priority = 1,
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<System.Collections.Generic.List<CommissionAccrual>> AccrualsForAsync(AppDbContext db, int reservaId)
        => await db.CommissionAccruals.Where(a => a.ReservaId == reservaId).ToListAsync();

    [Fact]
    public async Task ToggleOff_NoAccrual_EvenWhenFullyPaid()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: false);
        await SeedDefaultRuleAsync(db, percent: 10m);

        // Reserva cobrada (pago = venta confirmada) con ganancia. Con toggle OFF NO debe devengar nada.
        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1", ResponsibleUserName = "Vendedor Uno" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var accruals = await AccrualsForAsync(db, reserva.Id);
        Assert.Empty(accruals);
    }

    [Fact]
    public async Task ToggleOn_FullyPaid_AccruesPercentOfProfit_AttributedToResponsible()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1", ResponsibleUserName = "Vendedor Uno" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var accrual = Assert.Single(await AccrualsForAsync(db, reserva.Id));
        Assert.Equal("seller-1", accrual.SellerUserId);
        Assert.Equal("Vendedor Uno", accrual.SellerName);
        Assert.Equal("ARS", accrual.Currency);
        // 10% sobre ganancia 300 = 30.
        Assert.Equal(30m, accrual.Amount);
        Assert.Equal(10m, accrual.RatePercent);
        Assert.Equal(CommissionAccrualStatus.Devengada, accrual.Status);
    }

    [Fact]
    public async Task ToggleOn_NotFullyPaid_DoesNotAccrue()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        // Pago parcial: queda saldo positivo -> no se devenga todavia.
        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 400m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        Assert.Empty(await AccrualsForAsync(db, reserva.Id));
    }

    [Fact]
    public async Task ToggleOn_NoResponsible_DoesNotAccrue()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        // Cobrada y con ganancia, pero SIN vendedor responsable -> no inventamos dueño de la comision.
        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = null };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        Assert.Empty(await AccrualsForAsync(db, reserva.Id));
    }

    [Fact]
    public async Task ToggleOn_NoApplicableRule_AccruesZero()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        // NO sembramos ninguna regla -> sin regla aplicable el % es 0 (NO el 10% default de la calculadora suelta).

        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        // Sin regla -> comision 0 -> no se crea fila (la calculadora no emite lineas en 0).
        Assert.Empty(await AccrualsForAsync(db, reserva.Id));
    }

    [Fact]
    public async Task ToggleOn_Cancellation_RevertsAccruedToZero_NeverNegative()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1" };
        var hotel = new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m };
        reserva.HotelBookings.Add(hotel);
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        // 1) Cobrada -> devenga 30.
        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);
        Assert.Equal(30m, Assert.Single(await AccrualsForAsync(db, reserva.Id)).Amount);

        // 2) Se cancela la reserva (estado no devengable) -> tope cero. La fila NO se borra, se pone en 0.
        reserva.Status = EstadoReserva.Cancelled;
        await db.SaveChangesAsync();
        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var reverted = Assert.Single(await AccrualsForAsync(db, reserva.Id));
        Assert.Equal(0m, reverted.Amount);
        Assert.True(reverted.Amount >= 0m, "la comision nunca debe quedar negativa");
        Assert.Equal(0m, reverted.RatePercent);
    }

    [Fact]
    public async Task ToggleOn_BalanceGoesBackPositive_RevertsToZero()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        var payment = new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" };
        reserva.Payments.Add(payment);
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);
        Assert.Equal(30m, Assert.Single(await AccrualsForAsync(db, reserva.Id)).Amount);

        // El pago se anula (ej. rebote) -> el saldo vuelve a positivo -> la comision se revierte a 0.
        payment.Status = "Cancelled";
        await db.SaveChangesAsync();
        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        Assert.Equal(0m, Assert.Single(await AccrualsForAsync(db, reserva.Id)).Amount);
    }

    [Fact]
    public async Task ToggleOn_MultiCurrency_OneAccrualPerCurrency()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        reserva.FlightSegments.Add(new FlightSegment { Status = "HK", Currency = "USD", TicketIssuedAt = DateTime.UtcNow, SalePrice = 300m, NetCost = 200m, Commission = 100m });
        // Cobramos ambas monedas completas para llegar a Balance <= 0.
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 300m, Currency = "USD" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var accruals = (await AccrualsForAsync(db, reserva.Id)).ToDictionary(a => a.Currency);
        Assert.Equal(2, accruals.Count);
        Assert.Equal(30m, accruals["ARS"].Amount); // 10% de 300
        Assert.Equal(10m, accruals["USD"].Amount); // 10% de 100
    }

    [Fact]
    public async Task ToggleOn_RecalculatingTwice_IsIdempotent_NoDuplicateAccrual()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Commission = 300m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        // Recalcular tres veces (simula recobrar / re-disparar el recalculo). No debe duplicar la fila ni
        // acumular el monto.
        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);
        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);
        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var accrual = Assert.Single(await AccrualsForAsync(db, reserva.Id));
        Assert.Equal(30m, accrual.Amount);
    }

    [Fact]
    public async Task ToggleOn_ProfitWithTax_AccruesOnCommissionField_NotSaleMinusCostRaw()
    {
        await using var db = NewContext();
        await SeedSettingsAsync(db, commissionsEnabled: true);
        await SeedDefaultRuleAsync(db, percent: 10m);

        // El campo Commission del servicio (= venta - costo - impuesto) es la base. Si lo seteamos a 200
        // (porque hay impuesto incluido), la comision es 10% de 200 = 20, NO 10% de (1000-700)=30.
        var reserva = new Reserva { Name = "R", Status = EstadoReserva.InManagement, ResponsibleUserId = "seller-1" };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m, Tax = 100m, Commission = 200m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        Assert.Equal(20m, Assert.Single(await AccrualsForAsync(db, reserva.Id)).Amount);
    }
}
