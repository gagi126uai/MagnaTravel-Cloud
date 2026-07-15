using System;
using System.Collections.Generic;
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
/// Deuda del cliente DESGLOSADA POR RESERVA y por moneda (GET /api/customers/{id}/account/debt-by-reserva).
/// Espejo conceptual del lado proveedor. Alimenta el buscador de "usar saldo a favor -> aplicar a otra
/// reserva": el front necesita saber EN QUE reservas y EN QUE moneda debe el cliente. Contrato verificado:
///   - una reserva con deuda en ARS y otra con deuda en USD aparecen separadas por moneda (nunca se mezclan);
///   - una reserva saldada (Balance 0) NO aparece;
///   - una reserva cuyo pagador es OTRO cliente NO aparece;
///   - una reserva en estado no-firme (cotizacion / cancelada) NO aparece;
///   - reconciliacion: la suma por moneda del desglose iguala el saldo a cobrar global del cliente
///     (misma fuente: ReservaMoneyByCurrency en firme).
/// </summary>
public class CustomerDebtByReservaTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static CustomerService CreateService(AppDbContext context)
        => new CustomerService(context, new FinancePositionService(context));

    private static async Task<Customer> AddCustomerAsync(AppDbContext context, string fullName)
    {
        var customer = new Customer { FullName = fullName, IsActive = true };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task<Reserva> AddReservaAsync(
        AppDbContext context, int payerId, string numero, string status)
    {
        var reserva = new Reserva
        {
            NumeroReserva = numero,
            Name = "Reserva " + numero,
            Status = status,
            PayerId = payerId
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    private static void AddBalance(AppDbContext context, int reservaId, string currency, decimal balance)
    {
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reservaId,
            Currency = currency,
            Balance = balance,
            ConfirmedSale = balance
        });
        if (balance > 0m)
        {
            context.Invoices.Add(new Invoice
            {
                ReservaId = reservaId,
                TipoComprobante = 11,
                PuntoDeVenta = 1,
                NumeroComprobante = reservaId,
                Resultado = "A",
                ImporteTotal = balance,
                MonId = currency == "USD" ? "DOL" : "PES"
            });
        }
    }

    [Fact]
    public async Task Lists_debt_per_reserva_separated_by_currency()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Multimoneda");

        // Reserva A debe en ARS; Reserva B debe en USD. Distintas reservas, distintas monedas.
        var reservaArs = await AddReservaAsync(context, customer.Id, "R-001", EstadoReserva.Confirmed);
        var reservaUsd = await AddReservaAsync(context, customer.Id, "R-002", EstadoReserva.Confirmed);
        AddBalance(context, reservaArs.Id, "ARS", 1500m);
        AddBalance(context, reservaUsd.Id, "USD", 300m);
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtByReservaAsync(customer.Id, CancellationToken.None);

        Assert.Equal(customer.PublicId, result.CustomerPublicId);
        Assert.Equal(2, result.Reservas.Count);

        var lineArs = result.Reservas.Single(r => r.NumeroReserva == "R-001");
        var soloArs = Assert.Single(lineArs.DebtByCurrency);
        Assert.Equal("ARS", soloArs.Currency);
        Assert.Equal(1500m, soloArs.Amount);

        var lineUsd = result.Reservas.Single(r => r.NumeroReserva == "R-002");
        var soloUsd = Assert.Single(lineUsd.DebtByCurrency);
        Assert.Equal("USD", soloUsd.Currency);
        Assert.Equal(300m, soloUsd.Amount);
    }

    [Fact]
    public async Task Single_reserva_with_two_currencies_keeps_them_separate()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Una Reserva Dos Monedas");

        var reserva = await AddReservaAsync(context, customer.Id, "R-010", EstadoReserva.InManagement);
        AddBalance(context, reserva.Id, "ARS", 800m);
        AddBalance(context, reserva.Id, "USD", 120m);
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtByReservaAsync(customer.Id, CancellationToken.None);

        var line = Assert.Single(result.Reservas);
        Assert.Equal(2, line.DebtByCurrency.Count);
        // Orden estable por codigo de moneda: ARS antes que USD.
        Assert.Equal("ARS", line.DebtByCurrency[0].Currency);
        Assert.Equal(800m, line.DebtByCurrency[0].Amount);
        Assert.Equal("USD", line.DebtByCurrency[1].Currency);
        Assert.Equal(120m, line.DebtByCurrency[1].Amount);
    }

    [Fact]
    public async Task Settled_reserva_does_not_appear()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Saldado");

        var reservaConDeuda = await AddReservaAsync(context, customer.Id, "R-100", EstadoReserva.Confirmed);
        var reservaSaldada = await AddReservaAsync(context, customer.Id, "R-101", EstadoReserva.Confirmed);
        AddBalance(context, reservaConDeuda.Id, "ARS", 500m);
        AddBalance(context, reservaSaldada.Id, "ARS", 0m); // sin deuda viva -> no debe listarse
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtByReservaAsync(customer.Id, CancellationToken.None);

        var line = Assert.Single(result.Reservas);
        Assert.Equal("R-100", line.NumeroReserva);
    }

    [Fact]
    public async Task Reserva_of_another_payer_does_not_appear()
    {
        using var context = CreateContext();
        var owner = await AddCustomerAsync(context, "Pagador");
        var other = await AddCustomerAsync(context, "Otro Pagador");

        var reservaPropia = await AddReservaAsync(context, owner.Id, "R-200", EstadoReserva.Confirmed);
        var reservaAjena = await AddReservaAsync(context, other.Id, "R-201", EstadoReserva.Confirmed);
        AddBalance(context, reservaPropia.Id, "ARS", 700m);
        AddBalance(context, reservaAjena.Id, "ARS", 900m);
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtByReservaAsync(owner.Id, CancellationToken.None);

        var line = Assert.Single(result.Reservas);
        Assert.Equal("R-200", line.NumeroReserva);
    }

    [Fact]
    public async Task Non_firm_reserva_does_not_appear()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Estados");

        var firme = await AddReservaAsync(context, customer.Id, "R-300", EstadoReserva.Confirmed);
        var cotizacion = await AddReservaAsync(context, customer.Id, "R-301", EstadoReserva.Quotation);
        var cancelada = await AddReservaAsync(context, customer.Id, "R-302", EstadoReserva.Cancelled);
        AddBalance(context, firme.Id, "ARS", 400m);
        // Sin comprobante aprobado no son open items, independientemente del estado operativo.
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerDebtByReservaAsync(customer.Id, CancellationToken.None);

        var line = Assert.Single(result.Reservas);
        Assert.Equal("R-300", line.NumeroReserva);
    }

    [Fact]
    public async Task Per_currency_totals_reconcile_with_global_receivable()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Reconciliacion");

        var r1 = await AddReservaAsync(context, customer.Id, "R-400", EstadoReserva.Confirmed);
        var r2 = await AddReservaAsync(context, customer.Id, "R-401", EstadoReserva.InManagement);
        AddBalance(context, r1.Id, "ARS", 1000m);
        AddBalance(context, r1.Id, "USD", 50m);
        AddBalance(context, r2.Id, "ARS", 250m);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var byReserva = await service.GetCustomerDebtByReservaAsync(customer.Id, CancellationToken.None);
        var globalByCurrency = await new FinancePositionService(context)
            .GetCustomerReceivableByCurrencyAsync(customer.Id, CancellationToken.None);

        decimal totalArsDesglose = byReserva.Reservas
            .SelectMany(r => r.DebtByCurrency)
            .Where(c => c.Currency == "ARS")
            .Sum(c => c.Amount);
        decimal totalUsdDesglose = byReserva.Reservas
            .SelectMany(r => r.DebtByCurrency)
            .Where(c => c.Currency == "USD")
            .Sum(c => c.Amount);

        Assert.Equal(globalByCurrency.Single(c => c.Currency == "ARS").Amount, totalArsDesglose);
        Assert.Equal(globalByCurrency.Single(c => c.Currency == "USD").Amount, totalUsdDesglose);
        Assert.Equal(1250m, totalArsDesglose);
        Assert.Equal(50m, totalUsdDesglose);
    }

    [Fact]
    public async Task Unknown_customer_throws_key_not_found()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetCustomerDebtByReservaAsync(99999, CancellationToken.None));
    }
}
