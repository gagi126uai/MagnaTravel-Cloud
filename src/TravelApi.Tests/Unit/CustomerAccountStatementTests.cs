using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// EXTRACTO (libro mayor) de la cuenta por cobrar del cliente calculado EN EL SERVIDOR
/// (GET /api/customers/{id}/account/statement). Espejo del extracto del proveedor pero del lado VENTA y
/// cruzando TODAS las reservas en firme del cliente.
///
/// <para>El contrato clave que se verifica: el saldo de cierre de cada moneda RECONCILIA con el "Debe" por
/// moneda del header (FinancePositionService.GetCustomerReceivableByCurrencyAsync). Para probarlo de punta a
/// punta, los escenarios siembran servicios y cobros REALES y corren el persister oficial
/// (<see cref="ReservaMoneyPersister"/>) para materializar la proyeccion — asi el extracto y el header parten
/// de la MISMA cuenta, sin numeros sembrados a mano que oculten una divergencia.</para>
///
/// <para>Se cubre: venta sin cobrar, cobro parcial, cobro total, multimoneda separada, "facturar tarde"
/// (venta confirmada sin factura), saldo a favor por sobrepago (trasladado al bolsillo, fuera del extracto),
/// exclusion de reservas no-firme / de otro pagador / cobros cancelados, y el extracto vacio.</para>
/// </summary>
public class CustomerAccountStatementTests
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
        AppDbContext context, int payerId, string numero, string status, DateTime createdAt)
    {
        var reserva = new Reserva
        {
            NumeroReserva = numero,
            Name = "Expediente " + numero,
            Status = status,
            PayerId = payerId,
            CreatedAt = createdAt
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    /// <summary>Agrega un servicio generico RESUELTO (Status "Confirmado"): aporta SalePrice a ConfirmedSale.</summary>
    private static void AddResolvedService(AppDbContext context, int reservaId, decimal salePrice, string currency)
    {
        context.Set<ServicioReserva>().Add(new ServicioReserva
        {
            ReservaId = reservaId,
            Status = "Confirmado",
            SalePrice = salePrice,
            NetCost = 0m,
            Currency = currency
        });
    }

    private static Payment AddPayment(
        AppDbContext context, int reservaId, decimal amount, string currency, DateTime paidAt,
        string method = "Efectivo", string? imputedCurrency = null, decimal? imputedAmount = null)
    {
        var payment = new Payment
        {
            ReservaId = reservaId,
            Amount = amount,
            Currency = currency,
            ImputedCurrency = imputedCurrency,
            ImputedAmount = imputedAmount,
            PaidAt = paidAt,
            Method = method,
            Status = "Paid",
            AffectsCash = true
        };
        context.Payments.Add(payment);
        return payment;
    }

    /// <summary>Materializa la proyeccion (ReservaMoneyByCurrency) con el persister oficial, como en produccion.</summary>
    private static Task PersistMoneyAsync(AppDbContext context, int reservaId)
        => ReservaMoneyPersister.PersistAsync(context, reservaId, CancellationToken.None);

    // ============================================================================================
    // INVARIANTE: el saldo de cierre por moneda == el receivable del header por moneda.
    // ============================================================================================

    [Fact]
    public async Task Closing_balance_reconciles_with_header_receivable_per_currency()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Reconciliacion");

        // Reserva A (ARS): venta 1000, cobrado 600 -> debe 400.
        var a = await AddReservaAsync(context, customer.Id, "R-001", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, a.Id, 1000m, "ARS");
        AddPayment(context, a.Id, 600m, "ARS", new DateTime(2026, 1, 10));
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, a.Id);

        // Reserva B (USD): venta 500, cobrado 500 -> saldada (Balance 0, no aparece en el receivable).
        var b = await AddReservaAsync(context, customer.Id, "R-002", EstadoReserva.Confirmed, new DateTime(2026, 2, 1));
        AddResolvedService(context, b.Id, 500m, "USD");
        AddPayment(context, b.Id, 500m, "USD", new DateTime(2026, 2, 5));
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, b.Id);

        // Reserva C (ARS, InManagement): venta 200 sin cobrar y SIN facturar ("facturar tarde") -> debe 200.
        var c = await AddReservaAsync(context, customer.Id, "R-003", EstadoReserva.InManagement, new DateTime(2026, 3, 1));
        AddResolvedService(context, c.Id, 200m, "ARS");
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, c.Id);

        var service = CreateService(context);
        var statement = await service.GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);
        var receivable = await new FinancePositionService(context)
            .GetCustomerReceivableByCurrencyAsync(customer.Id, CancellationToken.None);

        // Header: ARS = 400 + 200 = 600; USD ausente (Balance 0).
        Assert.Equal(600m, receivable.Single(x => x.Currency == "ARS").Amount);
        Assert.DoesNotContain(receivable, x => x.Currency == "USD");

        // Extracto: bloque ARS cierra en 600 (== receivable); bloque USD existe pero cierra en 0.
        var ars = statement.Currencies.Single(x => x.Currency == "ARS");
        Assert.Equal(600m, ars.ClosingBalance);

        var usd = statement.Currencies.Single(x => x.Currency == "USD");
        Assert.Equal(0m, usd.ClosingBalance);

        // Invariante general: cada bloque cierra en el receivable de su moneda (0 si esa moneda no esta en el header).
        foreach (var block in statement.Currencies)
        {
            var expected = receivable.SingleOrDefault(x => x.Currency == block.Currency)?.Amount ?? 0m;
            Assert.Equal(expected, block.ClosingBalance);
        }
    }

    [Fact]
    public async Task Unpaid_sale_charges_full_confirmed_sale()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Sin Cobrar");

        var r = await AddReservaAsync(context, customer.Id, "R-010", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, r.Id, 1500m, "ARS");
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, r.Id);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        var line = Assert.Single(ars.Lines);
        Assert.Equal(CustomerAccountStatementLineKinds.Sale, line.Kind);
        Assert.Equal(1500m, line.Charge);
        Assert.Equal(0m, line.Credit);
        Assert.Equal(1500m, line.RunningBalance);
        Assert.Equal(1500m, ars.ClosingBalance);
        Assert.Equal(r.PublicId, line.ReservaPublicId);
        Assert.Equal("R-010", line.NumeroReserva);
    }

    [Fact]
    public async Task Partial_payment_running_balance_and_order()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Parcial");

        var r = await AddReservaAsync(context, customer.Id, "R-020", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, r.Id, 1000m, "ARS");
        AddPayment(context, r.Id, 400m, "ARS", new DateTime(2026, 1, 15));
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, r.Id);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        Assert.Equal(2, ars.Lines.Count);

        // Venta primero (cargo), cobro despues (abono): orden cronologico y saldo corriente.
        Assert.Equal(CustomerAccountStatementLineKinds.Sale, ars.Lines[0].Kind);
        Assert.Equal(1000m, ars.Lines[0].RunningBalance);
        Assert.Equal(CustomerAccountStatementLineKinds.Payment, ars.Lines[1].Kind);
        Assert.Equal(400m, ars.Lines[1].Credit);
        Assert.Equal(600m, ars.Lines[1].RunningBalance);
        Assert.Equal(600m, ars.ClosingBalance);
    }

    [Fact]
    public async Task Fully_paid_reserva_closes_at_zero()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Saldado");

        var r = await AddReservaAsync(context, customer.Id, "R-030", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, r.Id, 800m, "ARS");
        AddPayment(context, r.Id, 800m, "ARS", new DateTime(2026, 1, 20));
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, r.Id);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        Assert.Equal(0m, ars.ClosingBalance);
    }

    [Fact]
    public async Task Multicurrency_blocks_are_separated_never_summed()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Multimoneda");

        var r = await AddReservaAsync(context, customer.Id, "R-040", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, r.Id, 1000m, "ARS");
        AddResolvedService(context, r.Id, 300m, "USD");
        AddPayment(context, r.Id, 250m, "ARS", new DateTime(2026, 1, 10));
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, r.Id);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        Assert.Equal(2, statement.Currencies.Count);
        // Orden estable: ARS antes que USD.
        Assert.Equal("ARS", statement.Currencies[0].Currency);
        Assert.Equal(750m, statement.Currencies[0].ClosingBalance); // 1000 - 250
        Assert.Equal("USD", statement.Currencies[1].Currency);
        Assert.Equal(300m, statement.Currencies[1].ClosingBalance); // sin cobro USD
    }

    [Fact]
    public async Task Overpayment_is_swept_to_credit_and_excluded_from_statement()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Sobrepago");

        var r = await AddReservaAsync(context, customer.Id, "R-050", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, r.Id, 1000m, "ARS");
        var payment = AddPayment(context, r.Id, 1300m, "ARS", new DateTime(2026, 1, 10));
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, r.Id);

        // El sobrepago (-300) se traslada al bolsillo del cliente y deja la reserva en 0 (flujo real de produccion).
        await OverpaymentCreditConverter.ConvertAsync(
            context, payment, actorUserId: "tester", actorUserName: "Tester", NullLogger.Instance, CancellationToken.None);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);
        var receivable = await new FinancePositionService(context)
            .GetCustomerReceivableByCurrencyAsync(customer.Id, CancellationToken.None);

        // La reserva cierra en 0: el excedente salio a saldo a favor (ledger aparte), no es deuda.
        var ars = Assert.Single(statement.Currencies);
        Assert.Equal(0m, ars.ClosingBalance);
        Assert.DoesNotContain(receivable, x => x.Currency == "ARS" && x.Amount != 0m);

        // El saldo a favor vive en ClientCreditEntry, NO como una linea de deuda del extracto.
        var credit = Assert.Single(context.ClientCreditEntries.Where(e => e.CustomerId == customer.Id));
        Assert.Equal(300m, credit.RemainingBalance);

        // El cobro puente aparece marcado claramente (sin filtrar sus Notes internas).
        Assert.Contains(ars.Lines, l => l.Description == "Excedente trasladado a saldo a favor");
    }

    [Fact]
    public async Task Cancelled_payment_is_excluded()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Cobro Anulado");

        var r = await AddReservaAsync(context, customer.Id, "R-060", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, r.Id, 1000m, "ARS");
        var cancelled = AddPayment(context, r.Id, 400m, "ARS", new DateTime(2026, 1, 10));
        cancelled.Status = "Cancelled";
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, r.Id);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        // El cobro cancelado no baja la deuda ni aparece como abono.
        Assert.Equal(1000m, ars.ClosingBalance);
        Assert.DoesNotContain(ars.Lines, l => l.Kind == CustomerAccountStatementLineKinds.Payment);
    }

    [Fact]
    public async Task Non_firm_and_other_payer_reservas_are_excluded()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Estados");
        var other = await AddCustomerAsync(context, "Otro Pagador");

        var firme = await AddReservaAsync(context, customer.Id, "R-070", EstadoReserva.Confirmed, new DateTime(2026, 1, 1));
        AddResolvedService(context, firme.Id, 500m, "ARS");
        var cotizacion = await AddReservaAsync(context, customer.Id, "R-071", EstadoReserva.Quotation, new DateTime(2026, 1, 2));
        AddResolvedService(context, cotizacion.Id, 999m, "ARS");
        var cancelada = await AddReservaAsync(context, customer.Id, "R-072", EstadoReserva.Cancelled, new DateTime(2026, 1, 3));
        AddResolvedService(context, cancelada.Id, 888m, "ARS");
        var ajena = await AddReservaAsync(context, other.Id, "R-073", EstadoReserva.Confirmed, new DateTime(2026, 1, 4));
        AddResolvedService(context, ajena.Id, 777m, "ARS");
        await context.SaveChangesAsync();
        await PersistMoneyAsync(context, firme.Id);
        await PersistMoneyAsync(context, cotizacion.Id);
        await PersistMoneyAsync(context, cancelada.Id);
        await PersistMoneyAsync(context, ajena.Id);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        // Solo la reserva firme del cliente (500). Ni la cotizacion, ni la cancelada, ni la de otro pagador.
        var line = Assert.Single(ars.Lines);
        Assert.Equal("R-070", line.NumeroReserva);
        Assert.Equal(500m, ars.ClosingBalance);
    }

    [Fact]
    public async Task Empty_statement_when_customer_has_no_firm_reservas()
    {
        using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Sin Reservas");

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        Assert.Equal(customer.PublicId, statement.CustomerPublicId);
        Assert.True(statement.AmountsVisible);
        Assert.Empty(statement.Currencies);
    }

    [Fact]
    public async Task Unknown_customer_throws_key_not_found()
    {
        using var context = CreateContext();
        var service = CreateService(context);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetCustomerAccountStatementAsync(999999, CancellationToken.None));
    }
}
