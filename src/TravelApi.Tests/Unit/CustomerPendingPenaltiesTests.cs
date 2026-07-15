using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bloque "Multa pendiente de cobro" de la cuenta del cliente (UX 2026-07-15, GET /api/customers/{id}/account
/// -> CustomerAccountOverviewDto.PendingPenalties): junta las multas de anulación de TODAS las reservas
/// anuladas del cliente. Antes esta plata era invisible (la cuenta del cliente filtraba afuera las reservas
/// anuladas, único lugar donde vive la multa). Contrato verificado:
///   - ND emitida con CAE viva -> chip "pendingCollection";
///   - multa confirmada con ND todavía emitiéndose -> chip "issuing";
///   - ND fallida o en revisión manual -> chip "underReview";
///   - una multa cerrada SIN cobro (PenaltyStatus.Waived) NUNCA aparece;
///   - una reserva VIVA (no anulada) nunca aporta una fila, aunque tenga un BC con predicado "vivo";
///   - dos multas en monedas distintas llevan totales SEPARADOS (nunca se suman ARS + USD);
///   - la solapa "Reservas" de la cuenta (CustomerAccountReservaListItemDto) recibe el mismo contexto que ya
///     expone el listado general de reservas (CancelledMoneyContext/CancelledPenaltyAmount/CancelledPenaltyCurrency).
/// </summary>
public class CustomerPendingPenaltiesTests
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
        AppDbContext context, int payerId, string numero, string status, decimal balance)
    {
        var reserva = new Reserva
        {
            NumeroReserva = numero,
            Name = "Reserva " + numero,
            Status = status,
            PayerId = payerId,
            Balance = balance,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    // Misma variante "cruda" que ReservaServiceCancelledMoneyContextTests: fija el estado exacto de la
    // penalidad, de la ND, el monto congelado y (opcional) la factura de ND vinculada.
    private static async Task AddCancellationRawAsync(
        AppDbContext context, int reservaId, PenaltyStatus penalty, DebitNoteStatus debitNote,
        decimal? penaltyAmount = null, string? penaltyCurrencyAtEvent = null, int? debitNoteInvoiceId = null)
    {
        var bc = new BookingCancellation
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
        };
        context.BookingCancellations.Add(bc);
        await context.SaveChangesAsync();
    }

    private static async Task<int> SeedDebitNoteInvoiceAsync(AppDbContext context, AnnulmentStatus annulmentStatus)
    {
        var nd = new Invoice
        {
            TipoComprobante = 12, // ND C
            PuntoDeVenta = 1,
            NumeroComprobante = 500,
            Resultado = "A",
            CAE = "77777777",
            AnnulmentStatus = annulmentStatus,
        };
        context.Invoices.Add(nd);
        await context.SaveChangesAsync();
        return nd.Id;
    }

    private static async Task SeedMoneyByCurrencyAsync(
        AppDbContext context, int reservaId, string currency, decimal balance)
    {
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency
        {
            ReservaId = reservaId,
            Currency = currency,
            ConfirmedSale = balance,
            Balance = balance,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task IssuedDebitNote_WithLinkedInvoice_ShowsPendingCollection()
    {
        // (a) ND ya con CAE (Issued) y factura vinculada no anulada: es deuda firme, chip "pendingCollection".
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con ND emitida");
        var reserva = await AddReservaAsync(context, customer.Id, "F-2026-1001", EstadoReserva.Cancelled, balance: 5000m);
        var ndInvoiceId = await SeedDebitNoteInvoiceAsync(context, AnnulmentStatus.None);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Estimated, // rama 1 pura: ND Issued alcanza, no hace falta Confirmed acá.
            debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 5000m,
            debitNoteInvoiceId: ndInvoiceId);

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        var item = Assert.Single(overview.PendingPenalties.Items);
        Assert.Equal(reserva.PublicId, item.ReservaPublicId);
        Assert.Equal("F-2026-1001", item.NumeroReserva);
        Assert.Equal(5000m, item.Amount);
        Assert.Equal("ARS", item.Currency);
        Assert.Equal(CustomerPendingPenaltyStatus.PendingCollection, item.Status);

        var total = Assert.Single(overview.PendingPenalties.TotalsByCurrency);
        Assert.Equal("ARS", total.Currency);
        Assert.Equal(5000m, total.FirmAmount);
        Assert.Equal(0m, total.NotYetIssuedAmount);
    }

    [Fact]
    public async Task ConfirmedPenalty_DebitNoteStillPending_ShowsIssuing()
    {
        // (b) La multa está confirmada pero la ND todavía no salió (ventana de emisión diferida, ADR-014):
        // ya cuenta como deuda, pero distinguida con el chip "issuing" (comprobante en camino).
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con ND emitiéndose");
        var reserva = await AddReservaAsync(context, customer.Id, "F-2026-1002", EstadoReserva.Cancelled, balance: 3000m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 3000m);

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        var item = Assert.Single(overview.PendingPenalties.Items);
        Assert.Equal(CustomerPendingPenaltyStatus.Issuing, item.Status);
        Assert.Equal(3000m, item.Amount);

        var total = Assert.Single(overview.PendingPenalties.TotalsByCurrency);
        Assert.Equal(0m, total.FirmAmount);
        Assert.Equal(3000m, total.NotYetIssuedAmount);
    }

    [Fact]
    public async Task ConfirmedPenalty_DebitNoteInManualReview_ShowsUnderReview()
    {
        // (c) El comprobante quedó trabado (ManualReview): sigue contando como deuda, con chip "underReview".
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con ND en revisión");
        var reserva = await AddReservaAsync(context, customer.Id, "F-2026-1003", EstadoReserva.Cancelled, balance: 1200m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.ManualReview,
            penaltyAmount: 1200m);

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        var item = Assert.Single(overview.PendingPenalties.Items);
        Assert.Equal(CustomerPendingPenaltyStatus.UnderReview, item.Status);
        Assert.Equal(1200m, item.Amount);

        var total = Assert.Single(overview.PendingPenalties.TotalsByCurrency);
        Assert.Equal(0m, total.FirmAmount);
        Assert.Equal(1200m, total.NotYetIssuedAmount);
    }

    [Fact]
    public async Task WaivedPenalty_NeverAppears()
    {
        // (d) El operador no cobró multa (cierre sin multa, Fase A): NUNCA es una cuenta por cobrar, aunque la
        // reserva esté anulada. Ni siquiera con un DebitNoteStatus "vivo" heredado por error de datos.
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente sin multa (waived)");
        var reserva = await AddReservaAsync(context, customer.Id, "F-2026-1004", EstadoReserva.Cancelled, balance: 0m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Waived,
            debitNote: DebitNoteStatus.NotApplicable);

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.Empty(overview.PendingPenalties.Items);
        Assert.Empty(overview.PendingPenalties.TotalsByCurrency);
    }

    [Fact]
    public async Task LiveReservationNotCancelled_NeverAppears()
    {
        // (e) Una reserva VIVA (no anulada) nunca aporta una fila al bloque, aunque tuviera un BC que cumpliera
        // el predicado "vivo": el universo se filtra por reserva ANULADA primero.
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con reserva viva");
        var reserva = await AddReservaAsync(context, customer.Id, "F-2026-1005", EstadoReserva.Confirmed, balance: 1000m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 999m);

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.Empty(overview.PendingPenalties.Items);
    }

    [Fact]
    public async Task NoCancelledReservas_ReturnsEmptyBlock()
    {
        // Cliente sin ninguna reserva anulada: el bloque queda vacío (el front lo esconde entero).
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente sin anuladas");
        await AddReservaAsync(context, customer.Id, "F-2026-1006", EstadoReserva.Confirmed, balance: 500m);

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.Empty(overview.PendingPenalties.Items);
        Assert.Empty(overview.PendingPenalties.TotalsByCurrency);
    }

    [Fact]
    public async Task TwoCurrencies_TotalsAreSeparated_NeverSummed()
    {
        // (f) Una multa en ARS (con ND emitida) y otra en USD (emitiéndose): dos totales SEPARADOS, nunca un
        // combinado. Multimoneda: pesos y dólares siempre separados (guía UX).
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente multimoneda");

        var reservaArs = await AddReservaAsync(context, customer.Id, "F-2026-1007", EstadoReserva.Cancelled, balance: 2000m);
        var ndInvoiceId = await SeedDebitNoteInvoiceAsync(context, AnnulmentStatus.None);
        await AddCancellationRawAsync(
            context, reservaArs.Id,
            penalty: PenaltyStatus.Estimated,
            debitNote: DebitNoteStatus.Issued,
            penaltyAmount: 2000m,
            penaltyCurrencyAtEvent: "PES", // espacio ARCA -> se normaliza a ARS.
            debitNoteInvoiceId: ndInvoiceId);

        var reservaUsd = await AddReservaAsync(context, customer.Id, "F-2026-1008", EstadoReserva.PendingOperatorRefund, balance: 150m);
        await AddCancellationRawAsync(
            context, reservaUsd.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 150m,
            penaltyCurrencyAtEvent: "DOL"); // espacio ARCA -> se normaliza a USD.

        var overview = await CreateService(context).GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.Equal(2, overview.PendingPenalties.Items.Count);
        Assert.Equal(2, overview.PendingPenalties.TotalsByCurrency.Count);

        var totalArs = overview.PendingPenalties.TotalsByCurrency.Single(t => t.Currency == "ARS");
        Assert.Equal(2000m, totalArs.FirmAmount);
        Assert.Equal(0m, totalArs.NotYetIssuedAmount);

        var totalUsd = overview.PendingPenalties.TotalsByCurrency.Single(t => t.Currency == "USD");
        Assert.Equal(0m, totalUsd.FirmAmount);
        Assert.Equal(150m, totalUsd.NotYetIssuedAmount);
    }

    [Fact]
    public async Task ReservasTab_ExposesCancelledMoneyContextForAnnulledRow()
    {
        // (g) La solapa "Reservas" de la cuenta del cliente (GetCustomerAccountReservasAsync) recibe el MISMO
        // contexto de plata que el listado general de reservas: antes el DTO no lo mandaba (comentado como
        // "listo pero mudo" en el front).
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente solapa reservas");
        var reserva = await AddReservaAsync(context, customer.Id, "F-2026-1009", EstadoReserva.Cancelled, balance: 4000m);
        await SeedMoneyByCurrencyAsync(context, reserva.Id, "ARS", balance: 4000m);
        await AddCancellationRawAsync(
            context, reserva.Id,
            penalty: PenaltyStatus.Confirmed,
            debitNote: DebitNoteStatus.Pending,
            penaltyAmount: 4000m,
            penaltyCurrencyAtEvent: "PES");

        var page = await CreateService(context).GetCustomerAccountReservasAsync(
            customer.Id, new PagedQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Equal("MultaPorCobrar", row.CancelledMoneyContext);
        Assert.Equal(4000m, row.CancelledPenaltyAmount);
        Assert.Equal("ARS", row.CancelledPenaltyCurrency);
    }

    [Fact]
    public async Task ReservasTab_LiveReservationRow_HasNullCancelledMoneyContext()
    {
        // Espejo negativo de (g): una fila VIVA (no anulada) de la misma solapa nunca lleva contexto de anulación.
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente solapa reservas viva");
        await AddReservaAsync(context, customer.Id, "F-2026-1010", EstadoReserva.Confirmed, balance: 800m);

        var page = await CreateService(context).GetCustomerAccountReservasAsync(
            customer.Id, new PagedQuery(), CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Null(row.CancelledMoneyContext);
        Assert.Null(row.CancelledPenaltyAmount);
        Assert.Null(row.CancelledPenaltyCurrency);
    }
}
