using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-023 Tanda 1: el saldo a cobrar del cliente se calcula en UN solo lugar (FinancePositionService),
/// con UN solo predicado de estado (reservas en firme), derivado de ReservaMoneyByCurrency. Estos tests
/// cubren los invariantes INV-T1-1..4 y los bugs A1/A3/A5 que la tanda viene a corregir.
///
/// <para><b>Nota InMemory</b>: se valida el COMPORTAMIENTO de lectura (que numero da cada pantalla). El join
/// explicito de las queries corre igual en Postgres e InMemory.</para>
/// </summary>
public class Adr023Tanda1SingleBalanceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static CustomerService BuildCustomers(AppDbContext context)
        => new(context, new FinancePositionService(context));

    private static void AddApprovedInvoice(AppDbContext context, int reservaId, decimal amount, string currency = "ARS")
        => context.Invoices.Add(new Invoice
        {
            ReservaId = reservaId,
            TipoComprobante = 11,
            PuntoDeVenta = 1,
            NumeroComprobante = reservaId,
            Resultado = "A",
            ImporteTotal = amount,
            MonId = currency == "USD" ? "DOL" : "PES",
            IssuedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

    private static ReportService BuildReports(AppDbContext context)
    {
        var bna = new Mock<IBnaExchangeRateService>();
        bna.Setup(b => b.GetUsdSellerRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync((BnaUsdSellerRateDto?)null);
        return new ReportService(context, bna.Object);
    }

    // ============================================================================
    // FinancePositionService: el componente canonico
    // ============================================================================

    [Fact]
    public async Task CustomerReceivable_OnlyFirmStatusesCount()
    {
        // Cliente con reservas en varios estados: solo las EN FIRME suman al saldo a cobrar.
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana", IsActive = true });
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", PayerId = 1, Status = EstadoReserva.Quotation },
            new Reserva { Id = 3, NumeroReserva = "F-3", Name = "R3", PayerId = 1, Status = EstadoReserva.Cancelled },
            new Reserva { Id = 4, NumeroReserva = "F-4", Name = "R4", PayerId = 1, Status = EstadoReserva.Lost });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m },  // en firme: cuenta
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", Balance = 999m },  // cotizacion: NO
            new ReservaMoneyByCurrency { ReservaId = 3, Currency = "ARS", Balance = 999m },  // cancelada: NO
            new ReservaMoneyByCurrency { ReservaId = 4, Currency = "ARS", Balance = 999m }); // perdida: NO
        await context.SaveChangesAsync();

        var service = new FinancePositionService(context);
        var byCurrency = await service.GetCustomerReceivableByCurrencyAsync(1, CancellationToken.None);
        var scalar = await service.GetCustomerReceivableScalarAsync(1, CancellationToken.None);

        Assert.Equal(500m, byCurrency.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(500m, scalar);
    }

    [Fact]
    public async Task CustomerReceivable_MultiCurrency_TwoLines_ScalarIsSum()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana", IsActive = true });
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m },
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "USD", Balance = 80m });
        await context.SaveChangesAsync();

        var service = new FinancePositionService(context);
        var byCurrency = await service.GetCustomerReceivableByCurrencyAsync(1, CancellationToken.None);
        var scalar = await service.GetCustomerReceivableScalarAsync(1, CancellationToken.None);

        Assert.Equal(2, byCurrency.Count);
        Assert.Equal(500m, byCurrency.Single(x => x.Currency == "ARS").Amount);
        Assert.Equal(80m, byCurrency.Single(x => x.Currency == "USD").Amount);
        Assert.Equal(580m, scalar); // escalar de compat = suma cross-moneda
    }

    [Fact]
    public async Task CustomerReceivable_NoFirmReservations_IsZero()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana", IsActive = true });
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Quotation });
        context.ReservaMoneyByCurrency.Add(new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 999m });
        await context.SaveChangesAsync();

        var service = new FinancePositionService(context);
        var byCurrency = await service.GetCustomerReceivableByCurrencyAsync(1, CancellationToken.None);
        var scalar = await service.GetCustomerReceivableScalarAsync(1, CancellationToken.None);

        Assert.Empty(byCurrency);
        Assert.Equal(0m, scalar);
    }

    // ============================================================================
    // CustomerService: lista, detalle y overview
    // ============================================================================

    [Fact]
    public async Task GetCustomersList_CurrentBalance_FromCanonicalSource()
    {
        // INV-T1-1: el CurrentBalance de la lista sale de las reservas en firme, NO del subquery viejo que
        // incluia canceladas/cotizaciones.
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana", IsActive = true });
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", PayerId = 1, Status = EstadoReserva.Cancelled });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 300m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", Balance = 1000m });
        AddApprovedInvoice(context, 1, 300m);
        await context.SaveChangesAsync();

        var page = await BuildCustomers(context).GetCustomersAsync(new CustomerListQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(300m, item.CurrentBalance); // solo la Confirmed; la cancelada NO suma
    }

    [Fact]
    public async Task GetCustomersList_SortByBalance_OrdersByCanonicalBalance()
    {
        // T1.4: el orden por saldo ya no usa la columna zombie; ordena por el saldo canonico enriquecido.
        await using var context = CreateContext();
        context.Customers.AddRange(
            new Customer { Id = 1, FullName = "Ana", IsActive = true },
            new Customer { Id = 2, FullName = "Beto", IsActive = true });
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", PayerId = 2, Status = EstadoReserva.Confirmed });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 100m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", Balance = 900m });
        AddApprovedInvoice(context, 1, 100m);
        AddApprovedInvoice(context, 2, 900m);
        await context.SaveChangesAsync();

        var query = new CustomerListQuery { SortBy = "currentbalance", SortDir = "desc" };
        var page = await BuildCustomers(context).GetCustomersAsync(query, CancellationToken.None);

        Assert.Equal("Beto", page.Items[0].FullName); // mayor saldo arriba
        Assert.Equal(900m, page.Items[0].CurrentBalance);
        Assert.Equal("Ana", page.Items[1].FullName);
    }

    [Fact]
    public async Task GetCustomerDetail_CurrentBalance_FromCanonicalSource()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana", IsActive = true });
        // ADR-036 (2026-06-21): la reserva firme con deuda es Confirmed (Traveling ya no es firme cobrable).
        // El presupuesto (Budget) sigue sin contar.
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", PayerId = 1, Status = EstadoReserva.Budget });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 250m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", Balance = 700m });
        AddApprovedInvoice(context, 1, 250m);
        await context.SaveChangesAsync();

        var detail = await BuildCustomers(context).GetCustomerAsync(1, CancellationToken.None);

        Assert.Equal(250m, detail.CurrentBalance); // solo la firme (Confirmed); el presupuesto NO
    }

    [Fact]
    public async Task GetCustomerOverview_Summary_OnlyFirmReservationsCount()
    {
        // Regresion del bug A3: el "Saldo Actual" grande (TotalSales/TotalPaid/TotalBalance) contaba TODAS
        // las reservas (incluidas canceladas). Ahora cuenta SOLO las en firme; el trio queda coherente.
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana", IsActive = true });
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.InManagement,
                TotalSale = 1000m, TotalPaid = 400m, Balance = 600m },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", PayerId = 1, Status = EstadoReserva.Cancelled,
                TotalSale = 5000m, TotalPaid = 0m, Balance = 5000m });
        context.ReservaMoneyByCurrency.Add(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 600m });
        AddApprovedInvoice(context, 1, 1000m);
        context.Payments.Add(new Payment
        {
            ReservaId = 1,
            Amount = 400m,
            Currency = "ARS",
            Status = "Paid",
            AffectsCash = true,
            PaidAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)
        });
        await context.SaveChangesAsync();

        var overview = await BuildCustomers(context).GetCustomerAccountOverviewAsync(1, CancellationToken.None);

        // Solo la InManagement entra en los tres importes; la cancelada de 5000 queda fuera.
        Assert.Equal(1000m, overview.Summary.TotalSales);
        Assert.Equal(400m, overview.Summary.TotalPaid);
        Assert.Equal(600m, overview.Summary.TotalBalance);
        // INV-T1-2: TotalSales - TotalPaid coincide con TotalBalance.
        Assert.Equal(overview.Summary.TotalBalance, overview.Summary.TotalSales - overview.Summary.TotalPaid);
        // El "Saldo Actual" del cliente tambien sale de la fuente canonica.
        Assert.Equal(600m, overview.Customer.CurrentBalance);
        // El contador de reservas SI cuenta todas (badge de la pestaña, no plata).
        Assert.Equal(2, overview.Summary.ReservaCount);
    }

    // ============================================================================
    // UpdateCustomerAsync: fix clobber documento (INV-T1-4)
    // ============================================================================

    [Fact]
    public async Task UpdateCustomer_PutWithoutDocumentType_DoesNotEraseStoredDocument()
    {
        // INV-T1-4: un PUT que OMITE documentType/documentNumber (llegan null) NO borra el documento guardado.
        await using var context = CreateContext();
        context.Customers.Add(new Customer
        {
            Id = 1, FullName = "Ana", IsActive = true,
            DocumentType = "DNI", DocumentNumber = "30111222"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        // El "request mapeado" llega con FullName nuevo pero documento en null (el form lo omitio).
        var incoming = new Customer { Id = 1, FullName = "Ana Maria", IsActive = true, DocumentType = null, DocumentNumber = null };
        var result = await BuildCustomers(context).UpdateCustomerAsync(1, incoming, CancellationToken.None);

        Assert.Equal("Ana Maria", result.FullName);
        Assert.Equal("DNI", result.DocumentType);          // preservado
        Assert.Equal("30111222", result.DocumentNumber);   // preservado
    }

    [Fact]
    public async Task UpdateCustomer_PutWithDocumentType_UpdatesIt()
    {
        // Contraparte: si el PUT SI manda documento, se actualiza (no es read-only para siempre).
        await using var context = CreateContext();
        context.Customers.Add(new Customer
        {
            Id = 1, FullName = "Ana", IsActive = true,
            DocumentType = "DNI", DocumentNumber = "30111222"
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var incoming = new Customer { Id = 1, FullName = "Ana", IsActive = true, DocumentType = "Pasaporte", DocumentNumber = "AA999" };
        var result = await BuildCustomers(context).UpdateCustomerAsync(1, incoming, CancellationToken.None);

        Assert.Equal("Pasaporte", result.DocumentType);
        Assert.Equal("AA999", result.DocumentNumber);
    }

    // ============================================================================
    // ReportService: detailed receivables (INV-T1-1 / regresion A5)
    // ============================================================================

    [Fact]
    public async Task DetailedReceivables_LostAndPendingOperatorRefund_DoNotAppear()
    {
        // Regresion A5: el dashboard detallado incluia Lost/PendingOperatorRefund (no son deuda exigible).
        await using var context = CreateContext();
        context.Customers.AddRange(
            new Customer { Id = 1, FullName = "Firme", DocumentNumber = "1", IsActive = true },
            new Customer { Id = 2, FullName = "Perdido", DocumentNumber = "2", IsActive = true },
            new Customer { Id = 3, FullName = "Refund", DocumentNumber = "3", IsActive = true });
        context.Reservas.AddRange(
            new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R1", PayerId = 1, Status = EstadoReserva.Confirmed, CreatedAt = DateTime.UtcNow },
            new Reserva { Id = 2, NumeroReserva = "F-2", Name = "R2", PayerId = 2, Status = EstadoReserva.Lost, CreatedAt = DateTime.UtcNow },
            new Reserva { Id = 3, NumeroReserva = "F-3", Name = "R3", PayerId = 3, Status = EstadoReserva.PendingOperatorRefund, CreatedAt = DateTime.UtcNow });
        context.ReservaMoneyByCurrency.AddRange(
            new ReservaMoneyByCurrency { ReservaId = 1, Currency = "ARS", Balance = 500m },
            new ReservaMoneyByCurrency { ReservaId = 2, Currency = "ARS", Balance = 700m },
            new ReservaMoneyByCurrency { ReservaId = 3, Currency = "ARS", Balance = 900m });
        await context.SaveChangesAsync();

        var receivables = (await BuildReports(context).GetDetailedReceivablesAsync(CancellationToken.None))
            .Cast<object>()
            .ToList();

        var names = receivables
            .Select(x => (string)x.GetType().GetProperty("FullName")!.GetValue(x)!)
            .ToList();

        Assert.Single(receivables);
        Assert.Contains("Firme", names);
        Assert.DoesNotContain("Perdido", names);
        Assert.DoesNotContain("Refund", names);
    }
}
