using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Authorization;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Pieza de lectura para la pantalla "Saldo a favor del cliente" (botón "usar saldo a favor"):
/// el endpoint GET /api/customers/{id}/available-credit lista los ClientCreditEntry con saldo &gt; 0
/// del cliente, en orden FIFO, con su moneda y la reserva de origen.
///
/// <para><b>Nota InMemory</b>: el provider InMemory no aplica CHECK constraints ni índices únicos; estos
/// tests verifican el comportamiento de LECTURA (qué filas devuelve, en qué orden, con qué origen). La
/// validación de schema contra Postgres es de la tanda de integración.</para>
/// </summary>
public class CustomerAvailableCreditTests
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

    [Fact]
    public async Task AvailableCredit_ListsOnlyEntriesWithRemainingBalance_FifoOrder()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana Gomez" });

        // Dos entries con saldo (uno viejo, uno nuevo) + uno ya consumido (Remaining 0) que NO debe aparecer.
        var older = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        context.ClientCreditEntries.AddRange(
            new ClientCreditEntry { Id = 1, CustomerId = 1, Currency = "ARS", CreditedAmount = 200m, RemainingBalance = 150m, CreatedAt = newer, SourcePaymentId = 5 },
            new ClientCreditEntry { Id = 2, CustomerId = 1, Currency = "ARS", CreditedAmount = 100m, RemainingBalance = 100m, CreatedAt = older, SourcePaymentId = 6 },
            new ClientCreditEntry { Id = 3, CustomerId = 1, Currency = "ARS", CreditedAmount = 70m, RemainingBalance = 0m, CreatedAt = older, IsFullyConsumed = true, SourcePaymentId = 7 });
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerAvailableCreditAsync(1, CancellationToken.None);

        Assert.Equal(2, result.Count);
        // FIFO: el entry más viejo (saldo 100) viene primero; el más nuevo (saldo 150) segundo.
        // El entry consumido (saldo 0) no aparece.
        Assert.Equal(100m, result.ElementAt(0).RemainingBalance);
        Assert.Equal(150m, result.ElementAt(1).RemainingBalance);
    }

    [Fact]
    public async Task AvailableCredit_KeepsCurrencyPerEntry_AndNormalizesLegacyToArs()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana Gomez" });

        // Entry en USD + entry con moneda legacy vacía (debe normalizar a ARS, no romper).
        context.ClientCreditEntries.AddRange(
            new ClientCreditEntry { Id = 1, CustomerId = 1, Currency = "USD", CreditedAmount = 40m, RemainingBalance = 40m, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), SourcePaymentId = 5 },
            new ClientCreditEntry { Id = 2, CustomerId = 1, Currency = "", CreditedAmount = 30m, RemainingBalance = 30m, CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), SourcePaymentId = 6 });
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerAvailableCreditAsync(1, CancellationToken.None);

        Assert.Equal("USD", result.ElementAt(0).Currency);
        Assert.Equal("ARS", result.ElementAt(1).Currency); // legacy vacío -> ARS
    }

    [Fact]
    public async Task AvailableCredit_ResolvesOrigin_FromCancellationAndFromOverpayment()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana Gomez" });

        var cancellationReservaPublicId = Guid.NewGuid();
        var overpaymentReservaPublicId = Guid.NewGuid();

        context.Reservas.AddRange(
            new Reserva { Id = 10, PublicId = cancellationReservaPublicId, NumeroReserva = "F-CANC", Name = "R cancelada", PayerId = 1 },
            new Reserva { Id = 11, PublicId = overpaymentReservaPublicId, NumeroReserva = "F-OVER", Name = "R sobrepagada", PayerId = 1 });
        context.BookingCancellations.Add(new BookingCancellation { Id = 99, ReservaId = 10 });
        await context.SaveChangesAsync();

        context.ClientCreditEntries.AddRange(
            // Crédito de CANCELACIÓN: el origen sale de BookingCancellation.Reserva.
            new ClientCreditEntry { Id = 1, CustomerId = 1, Currency = "ARS", CreditedAmount = 200m, RemainingBalance = 200m, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), BookingCancellationId = 99 },
            // Crédito de SOBREPAGO: el origen sale de SourceReserva.
            new ClientCreditEntry { Id = 2, CustomerId = 1, Currency = "ARS", CreditedAmount = 50m, RemainingBalance = 50m, CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), SourceReservaId = 11, SourcePaymentId = 7 });
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerAvailableCreditAsync(1, CancellationToken.None);

        var fromCancellation = result.Single(e => e.RemainingBalance == 200m);
        Assert.Equal("F-CANC", fromCancellation.OriginReservaNumber);
        Assert.Equal(cancellationReservaPublicId, fromCancellation.OriginReservaPublicId);

        var fromOverpayment = result.Single(e => e.RemainingBalance == 50m);
        Assert.Equal("F-OVER", fromOverpayment.OriginReservaNumber);
        Assert.Equal(overpaymentReservaPublicId, fromOverpayment.OriginReservaPublicId);
    }

    [Fact]
    public async Task AvailableCredit_CustomerWithoutCredit_ReturnsEmpty()
    {
        await using var context = CreateContext();
        context.Customers.Add(new Customer { Id = 1, FullName = "Ana Gomez" });
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerAvailableCreditAsync(1, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AvailableCredit_DoesNotLeakOtherCustomersCredit()
    {
        await using var context = CreateContext();
        context.Customers.AddRange(
            new Customer { Id = 1, FullName = "Ana Gomez" },
            new Customer { Id = 2, FullName = "Beto Diaz" });
        context.ClientCreditEntries.AddRange(
            new ClientCreditEntry { Id = 1, CustomerId = 1, Currency = "ARS", CreditedAmount = 100m, RemainingBalance = 100m, CreatedAt = DateTime.UtcNow, SourcePaymentId = 5 },
            new ClientCreditEntry { Id = 2, CustomerId = 2, Currency = "ARS", CreditedAmount = 999m, RemainingBalance = 999m, CreatedAt = DateTime.UtcNow, SourcePaymentId = 6 });
        await context.SaveChangesAsync();

        var result = await CreateService(context).GetCustomerAvailableCreditAsync(1, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(100m, result.Single().RemainingBalance);
    }

    /// <summary>
    /// El endpoint expone montos a favor del cliente -> debe quedar gateado con clientes.view Y cobranzas.view
    /// (AND apilando atributos), igual que /account y /account/payments. Si alguien afloja el gate, esta
    /// reflexión lo detecta. Mismo patrón que CancellationPendingCreditNoteReviewTests.
    /// </summary>
    [Fact]
    public void Endpoint_IsGatedByClientesView_And_CobranzasView()
    {
        var method = typeof(CustomersController).GetMethod(
            nameof(CustomersController.GetCustomerAvailableCredit));
        Assert.NotNull(method);

        var attributes = method!.GetCustomAttributes<RequirePermissionAttribute>().ToList();
        var grantedPermissions = attributes
            .SelectMany(attribute => RequirePermissionAttribute.TryParsePolicyName(attribute.Policy!) ?? Array.Empty<string>())
            .ToList();

        Assert.Contains(Permissions.ClientesView, grantedPermissions);
        Assert.Contains(Permissions.CobranzasView, grantedPermissions);
    }
}
