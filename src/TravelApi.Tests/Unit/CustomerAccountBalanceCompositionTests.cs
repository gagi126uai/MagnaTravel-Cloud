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
/// Tanda D2 (extracto profesional de la cuenta corriente del cliente, 2026-07-16). Cubre lo que pide la spec
/// UX (docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md §7):
///   1. una reserva ANULADA aparece en el extracto con su factura + su nota de credito (contra-asiento) y,
///      si hubo, su multa como nota de debito;
///   2. el "Saldo a favor aplicado" a una multa aparece como renglon Haber (CreditApplication) del extracto;
///   3. la composicion del saldo por moneda (facturadoSinCobrar / multasAbiertas / multasEnTramite /
///      creditoAFavor / saldo) coincide, por construccion, con el cierre del extracto;
///   4. una reserva VIVA sin multas no cambia de comportamiento (regresion).
/// </summary>
public class CustomerAccountBalanceCompositionTests
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
            Name = "Expediente " + numero,
            Status = status,
            PayerId = payerId,
            CreatedAt = new DateTime(2026, 1, 1),
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    private static async Task<int> AddInvoiceAsync(
        AppDbContext context, int reservaId, int tipoComprobante, decimal importe, DateTime issuedAt,
        string monId = "PES", string cae = "77777777")
    {
        var invoice = new Invoice
        {
            ReservaId = reservaId,
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = new Random().Next(1, 999999),
            Resultado = "A",
            ImporteTotal = importe,
            MonId = monId,
            CAE = cae,
            AnnulmentStatus = AnnulmentStatus.None,
            IssuedAt = issuedAt,
        };
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();
        return invoice.Id;
    }

    // Misma variante "cruda" que CustomerPendingPenaltiesTests: fija el estado exacto de la penalidad sin
    // pasar por BookingCancellationService (test de READ MODEL, no de la escritura).
    private static async Task AddCancellationRawAsync(
        AppDbContext context, int reservaId, PenaltyStatus penalty, DebitNoteStatus debitNote,
        decimal? penaltyAmount = null, string? penaltyCurrencyAtEvent = null, int? debitNoteInvoiceId = null)
    {
        context.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reservaId,
            Reason = "Cliente anuló el viaje",
            DraftedByUserId = "vendedor-1",
            ConfirmedWithClientAt = DateTime.UtcNow.AddDays(-20),
            PenaltyStatus = penalty,
            DebitNoteStatus = debitNote,
            PenaltyAmountAtEvent = penaltyAmount,
            PenaltyCurrencyAtEvent = penaltyCurrencyAtEvent,
            DebitNoteInvoiceId = debitNoteInvoiceId,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task Annulled_reserva_shows_invoice_credit_note_and_penalty_in_the_statement()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Anulado Con Multa");
        var reserva = await AddReservaAsync(context, customer.Id, "R-1050", EstadoReserva.Cancelled);

        // Factura original de la venta.
        await AddInvoiceAsync(context, reserva.Id, tipoComprobante: 11, importe: 90000m,
            issuedAt: new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc));
        // Nota de credito de la anulacion: contra-asiento que cancela la factura.
        await AddInvoiceAsync(context, reserva.Id, tipoComprobante: 13, importe: 90000m,
            issuedAt: new DateTime(2026, 5, 21, 0, 0, 0, DateTimeKind.Utc));
        // Nota de debito de la multa por anulacion.
        var ndId = await AddInvoiceAsync(context, reserva.Id, tipoComprobante: 12, importe: 20000m,
            issuedAt: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc));
        await AddCancellationRawAsync(
            context, reserva.Id, penalty: PenaltyStatus.Estimated, debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 20000m, penaltyCurrencyAtEvent: "PES", debitNoteInvoiceId: ndId);

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        Assert.Equal(3, ars.Lines.Count);

        // La reserva anulada NO desaparece: sus 3 renglones estan, en orden cronologico.
        Assert.Equal(TravelApi.Domain.Reservations.CustomerAccountStatementLineKinds.Invoice, ars.Lines[0].Kind);
        Assert.Equal(90000m, ars.Lines[0].Charge);
        Assert.Equal(reserva.PublicId, ars.Lines[0].ReservaPublicId);
        Assert.Equal("R-1050", ars.Lines[0].NumeroReserva);

        Assert.Equal(TravelApi.Domain.Reservations.CustomerAccountStatementLineKinds.CreditNote, ars.Lines[1].Kind);
        Assert.Equal(90000m, ars.Lines[1].Credit);
        Assert.Equal(0m, ars.Lines[1].RunningBalance); // el contra-asiento cancela la factura: el saldo vuelve solo.

        Assert.Equal(TravelApi.Domain.Reservations.CustomerAccountStatementLineKinds.DebitNote, ars.Lines[2].Kind);
        Assert.Equal(20000m, ars.Lines[2].Charge);
        Assert.Equal(20000m, ars.Lines[2].RunningBalance);

        // El cierre de la moneda ya incluye la multa (decision del dueño 2026-07-16, deroga 2026-07-15).
        Assert.Equal(20000m, ars.ClosingBalance);
    }

    [Fact]
    public async Task Credit_applied_to_a_penalty_shows_as_a_haber_line_and_reduces_the_balance()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Con Saldo Aplicado A Multa");
        var reserva = await AddReservaAsync(context, customer.Id, "R-2000", EstadoReserva.Cancelled);

        var ndId = await AddInvoiceAsync(context, reserva.Id, tipoComprobante: 12, importe: 20000m,
            issuedAt: new DateTime(2026, 5, 22, 0, 0, 0, DateTimeKind.Utc));
        await AddCancellationRawAsync(
            context, reserva.Id, penalty: PenaltyStatus.Estimated, debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 20000m, penaltyCurrencyAtEvent: "PES", debitNoteInvoiceId: ndId);

        // Puente REAL del sistema (ClientCreditService.HandleAppliedToNewBookingAsync, rama multa): Payment
        // positivo, no mueve caja, atado a la ND por LinkedInvoiceId, no infla el saldo operativo (ya anulado).
        context.Payments.Add(new Payment
        {
            ReservaId = reserva.Id,
            LinkedInvoiceId = ndId,
            Amount = 5000m,
            Currency = "ARS",
            Method = TravelApi.Infrastructure.Reservations.AppliedCreditBridge.PenaltyBridgeMethod,
            AffectsCash = false,
            AffectsReservaBalance = false,
            Status = "Paid",
            PaidAt = new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        Assert.Equal(2, ars.Lines.Count); // ND + aplicacion de saldo (sin factura/NC en este escenario acotado).

        var applied = ars.Lines[1];
        Assert.Equal(TravelApi.Domain.Reservations.CustomerAccountStatementLineKinds.CreditApplication, applied.Kind);
        Assert.Equal(5000m, applied.Credit);
        Assert.Equal("Saldo a favor aplicado", applied.Description);
        Assert.Equal(15000m, applied.RunningBalance); // 20.000 de multa - 5.000 aplicados.
        Assert.Equal(15000m, ars.ClosingBalance);
    }

    [Fact]
    public async Task Balance_composition_matches_the_statement_closing_and_separates_penalties_from_sales()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Composicion Completa");

        // (1) Reserva VIVA con deuda de venta (facturado sin cobrar): 400 ARS.
        var reservaViva = await AddReservaAsync(context, customer.Id, "R-3000", EstadoReserva.Confirmed);
        await AddInvoiceAsync(context, reservaViva.Id, tipoComprobante: 11, importe: 400m,
            issuedAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc));

        // (2) Reserva ANULADA con multa FIRME (ND emitida con CAE): 20.000 ARS.
        var reservaConMultaFirme = await AddReservaAsync(context, customer.Id, "R-3001", EstadoReserva.Cancelled);
        var ndFirmeId = await AddInvoiceAsync(context, reservaConMultaFirme.Id, tipoComprobante: 12, importe: 20000m,
            issuedAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        await AddCancellationRawAsync(
            context, reservaConMultaFirme.Id, penalty: PenaltyStatus.Estimated, debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 20000m, penaltyCurrencyAtEvent: "PES", debitNoteInvoiceId: ndFirmeId);

        // (3) Reserva ANULADA con multa EN TRAMITE (todavia sin comprobante): 5.000 ARS.
        var reservaConMultaEnTramite = await AddReservaAsync(context, customer.Id, "R-3002", EstadoReserva.Cancelled);
        await AddCancellationRawAsync(
            context, reservaConMultaEnTramite.Id, penalty: PenaltyStatus.Confirmed, debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 5000m, penaltyCurrencyAtEvent: "PES");

        // (4) Saldo a favor disponible del cliente: 1.000 ARS.
        context.ClientCreditEntries.Add(new ClientCreditEntry
        {
            CustomerId = customer.Id, Currency = "ARS", CreditedAmount = 1000m, RemainingBalance = 1000m,
        });
        await context.SaveChangesAsync();

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);
        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var composition = Assert.Single(overview.Summary.BalanceCompositionByCurrency);
        Assert.Equal("ARS", composition.Currency);

        // facturadoSinCobrar = SOLO la venta viva (400): la multa firme queda afuera de esta linea.
        Assert.Equal(400m, composition.FacturadoSinCobrar);
        // multasAbiertas = firme (20.000) + en tramite (5.000).
        Assert.Equal(25000m, composition.MultasAbiertas);
        Assert.Equal(5000m, composition.MultasEnTramite);
        Assert.Equal(1000m, composition.CreditoAFavor);
        // saldo = 400 + 25.000 - 1.000 = 24.400.
        Assert.Equal(24400m, composition.Saldo);

        // Identidad de construccion (spec §7.2): la parte YA documentada de la composicion (facturado + multa
        // firme) coincide con el cierre del extracto de la MISMA moneda.
        var ars = Assert.Single(statement.Currencies);
        Assert.Equal(composition.FacturadoSinCobrar + (composition.MultasAbiertas - composition.MultasEnTramite), ars.ClosingBalance);
    }

    [Fact]
    public async Task Live_reserva_without_penalties_keeps_previous_shape()
    {
        // Regresion: un cliente sin ninguna reserva anulada ni multa no debe ver la linea de multas ni el
        // credito (ambos en 0), y el saldo de la composicion coincide 1 a 1 con el receivable de siempre.
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Sin Multas");
        var reserva = await AddReservaAsync(context, customer.Id, "R-4000", EstadoReserva.Confirmed);
        await AddInvoiceAsync(context, reserva.Id, tipoComprobante: 11, importe: 1500m,
            issuedAt: new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc));

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        var composition = Assert.Single(overview.Summary.BalanceCompositionByCurrency);
        Assert.Equal(1500m, composition.FacturadoSinCobrar);
        Assert.Equal(0m, composition.MultasAbiertas);
        Assert.Equal(0m, composition.MultasEnTramite);
        Assert.Equal(0m, composition.CreditoAFavor);
        Assert.Equal(1500m, composition.Saldo);
        Assert.Equal(1500m, Assert.Single(overview.Summary.ReceivableByCurrency).Amount);
        Assert.Empty(overview.PendingPenalties.Items);
    }

    [Fact]
    public async Task Balance_composition_never_shows_a_negative_facturado_when_penalty_outstanding_exceeds_reserva_net()
    {
        // Caso de borde M1 (review Tanda D2, 2026-07-17): DebitNoteOutstandingRules calcula el pendiente de la
        // ND mirando SOLO los pagos con LinkedInvoiceId == esa ND (DebitNoteOutstandingLookup.LoadCollectedAmountsAsync).
        // Si la MISMA reserva anulada tiene ADEMAS un cobro grande NO atado a esa ND, el open item de la
        // reserva en el extracto puede cerrar en saldo NEGATIVO (aporta 0 al ClosingBalance; el sobrante queda
        // en UnappliedCredit) mientras la ND sigue mostrando un outstanding > 0. Antes del fix,
        // "closingBalance - multasFirmes" daba negativo; ahora se frena en 0 y el residuo se descuenta de
        // MultasAbiertas, sin mover el Saldo final.
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Borde M1");
        var reserva = await AddReservaAsync(context, customer.Id, "R-5000", EstadoReserva.Cancelled);

        var ndId = await AddInvoiceAsync(context, reserva.Id, tipoComprobante: 12, importe: 5000m,
            issuedAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        await AddCancellationRawAsync(
            context, reserva.Id, penalty: PenaltyStatus.Estimated, debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 5000m, penaltyCurrencyAtEvent: "PES", debitNoteInvoiceId: ndId);

        // Cobro grande de la MISMA reserva, a proposito SIN LinkedInvoiceId: entra al extracto (open item por
        // reserva) pero DebitNoteOutstandingRules jamas lo ve (solo mira pagos atados a la ND puntual).
        context.Payments.Add(new Payment
        {
            ReservaId = reserva.Id,
            Amount = 8000m,
            Currency = "ARS",
            Method = "Transfer",
            AffectsCash = true,
            AffectsReservaBalance = false,
            Status = "Paid",
            PaidAt = new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);
        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        var ars = Assert.Single(statement.Currencies);
        // La reserva neta en -3.000 (5.000 de ND - 8.000 de cobro): aporta 0 al cierre, el negativo queda
        // como credito no aplicado.
        Assert.Equal(0m, ars.ClosingBalance);
        Assert.Equal(3000m, ars.UnappliedCredit);

        var composition = Assert.Single(overview.Summary.BalanceCompositionByCurrency);
        Assert.Equal(0m, composition.FacturadoSinCobrar); // nunca negativo.
        Assert.True(composition.MultasAbiertas >= 0m); // tampoco negativo.
        Assert.Equal(0m, composition.MultasAbiertas); // se absorbe el residuo completo (5.000 de sobrecierre).
        // El SALDO (fuente de verdad) es siempre closingBalance + multasEnTramite - creditoAFavor, INTACTO
        // pese al ajuste del split: 0 + 0 - 0 = 0.
        Assert.Equal(0m, composition.Saldo);
    }

    [Fact]
    public async Task Balance_composition_keeps_ars_and_usd_fully_separated()
    {
        // Multimoneda (regla dura 2026-06-09): un cliente con deuda/multa/credito en ARS Y en USD debe recibir
        // DOS entradas de composicion, cada una coincidiendo con el cierre de SU propio bloque del extracto.
        // Nunca se suman ni se mezclan los montos entre monedas.
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente Multimoneda Composicion");

        // ARS: venta viva de 1.000 + multa firme de 300.
        var reservaArsViva = await AddReservaAsync(context, customer.Id, "R-6000", EstadoReserva.Confirmed);
        await AddInvoiceAsync(context, reservaArsViva.Id, tipoComprobante: 11, importe: 1000m,
            issuedAt: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), monId: "PES");

        var reservaArsAnulada = await AddReservaAsync(context, customer.Id, "R-6001", EstadoReserva.Cancelled);
        var ndArsId = await AddInvoiceAsync(context, reservaArsAnulada.Id, tipoComprobante: 12, importe: 300m,
            issuedAt: new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc), monId: "PES");
        await AddCancellationRawAsync(
            context, reservaArsAnulada.Id, penalty: PenaltyStatus.Estimated, debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 300m, penaltyCurrencyAtEvent: "PES", debitNoteInvoiceId: ndArsId);

        context.ClientCreditEntries.Add(new ClientCreditEntry
        {
            CustomerId = customer.Id, Currency = "ARS", CreditedAmount = 100m, RemainingBalance = 100m,
        });

        // USD: venta viva de 500 + multa en tramite de 80 (sin comprobante todavia).
        var reservaUsdViva = await AddReservaAsync(context, customer.Id, "R-6002", EstadoReserva.Confirmed);
        await AddInvoiceAsync(context, reservaUsdViva.Id, tipoComprobante: 11, importe: 500m,
            issuedAt: new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc), monId: "DOL");

        var reservaUsdAnulada = await AddReservaAsync(context, customer.Id, "R-6003", EstadoReserva.PendingOperatorRefund);
        await AddCancellationRawAsync(
            context, reservaUsdAnulada.Id, penalty: PenaltyStatus.Confirmed, debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 80m, penaltyCurrencyAtEvent: "DOL");

        context.ClientCreditEntries.Add(new ClientCreditEntry
        {
            CustomerId = customer.Id, Currency = "USD", CreditedAmount = 20m, RemainingBalance = 20m,
        });
        await context.SaveChangesAsync();

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);
        var statement = await CreateService(context).GetCustomerAccountStatementAsync(customer.Id, CancellationToken.None);

        Assert.Equal(2, overview.Summary.BalanceCompositionByCurrency.Count);
        Assert.Equal(2, statement.Currencies.Count);

        var ars = overview.Summary.BalanceCompositionByCurrency.Single(c => c.Currency == "ARS");
        Assert.Equal(1000m, ars.FacturadoSinCobrar);
        Assert.Equal(300m, ars.MultasAbiertas);
        Assert.Equal(0m, ars.MultasEnTramite);
        Assert.Equal(100m, ars.CreditoAFavor);
        Assert.Equal(1200m, ars.Saldo); // 1000 + 300 - 100
        var arsBlock = statement.Currencies.Single(b => b.Currency == "ARS");
        Assert.Equal(ars.FacturadoSinCobrar + ars.MultasAbiertas, arsBlock.ClosingBalance); // sin parte "en tramite" que restar.

        var usd = overview.Summary.BalanceCompositionByCurrency.Single(c => c.Currency == "USD");
        Assert.Equal(500m, usd.FacturadoSinCobrar);
        Assert.Equal(80m, usd.MultasAbiertas);
        Assert.Equal(80m, usd.MultasEnTramite); // toda la multa USD esta en tramite (sin comprobante).
        Assert.Equal(20m, usd.CreditoAFavor);
        Assert.Equal(560m, usd.Saldo); // 500 + 80 - 20
        var usdBlock = statement.Currencies.Single(b => b.Currency == "USD");
        // La parte YA documentada (facturado, sin la multa en tramite que no tiene comprobante) coincide con
        // el cierre del bloque USD.
        Assert.Equal(usd.FacturadoSinCobrar + (usd.MultasAbiertas - usd.MultasEnTramite), usdBlock.ClosingBalance);

        // Nunca se mezclan: ni un peso de ARS aparece sumado al total de USD ni viceversa.
        Assert.NotEqual(ars.Saldo, usd.Saldo);
    }
}
