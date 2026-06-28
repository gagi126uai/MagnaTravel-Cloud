using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-041 TANDA 5 (2026-06-27): plazo de pago por defecto del operador (Supplier.DefaultPaymentTermDays),
/// opcional, del que se DERIVA un vencimiento sugerido por compra/servicio. Cubre: la derivacion pura
/// (null = sin vencimiento = comportamiento actual; con plazo = fecha + dias), la validacion >= 0 al
/// persistir, el round-trip del campo en alta/edicion, y el vencimiento sugerido por linea en la lista de
/// servicios de la cuenta del proveedor.
/// </summary>
public class SupplierDefaultPaymentTermDaysTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // ---------------------------------------------------------------------------------------------------
    // Derivacion pura del vencimiento sugerido (SupplierDebtCalculator.DeriveSuggestedDueDate)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DeriveSuggestedDueDate_WithoutTerm_ReturnsNull()
    {
        // Sin plazo configurado: el comportamiento actual = no hay vencimiento sugerido.
        var purchaseDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = SupplierDebtCalculator.DeriveSuggestedDueDate(purchaseDate, defaultPaymentTermDays: null);

        Assert.Null(result);
    }

    [Fact]
    public void DeriveSuggestedDueDate_WithTerm_ReturnsPurchaseDatePlusDays()
    {
        var purchaseDate = new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc);

        var result = SupplierDebtCalculator.DeriveSuggestedDueDate(purchaseDate, defaultPaymentTermDays: 30);

        Assert.Equal(new DateTime(2026, 7, 1, 10, 30, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void DeriveSuggestedDueDate_WithZeroTerm_ReturnsSameDate()
    {
        // Plazo 0 = "pago contra entrega": vencimiento = la propia fecha de compra (NO es lo mismo que null).
        var purchaseDate = new DateTime(2026, 6, 1, 10, 30, 0, DateTimeKind.Utc);

        var result = SupplierDebtCalculator.DeriveSuggestedDueDate(purchaseDate, defaultPaymentTermDays: 0);

        Assert.Equal(purchaseDate, result);
    }

    // ---------------------------------------------------------------------------------------------------
    // Validacion >= 0 al persistir (alta y edicion)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateSupplierAsync_WithNegativeTerm_Throws()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateSupplierAsync(
                new Supplier { Name = "Operador", DefaultPaymentTermDays = -1 },
                CancellationToken.None));

        Assert.Contains("plazo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task UpdateSupplierAsync_WithNegativeTerm_Throws()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);
        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSupplierAsync(
                created.Id,
                new Supplier { Name = "Operador", DefaultPaymentTermDays = -5 },
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateSupplierAsync_WithZeroTerm_IsAllowed()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador", DefaultPaymentTermDays = 0 },
            CancellationToken.None);

        Assert.Equal(0, created.DefaultPaymentTermDays);
    }

    // ---------------------------------------------------------------------------------------------------
    // Round-trip del campo en alta / edicion / lectura
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateThenGet_RoundTripsTerm()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador", DefaultPaymentTermDays = 45 },
            CancellationToken.None);

        var read = await service.GetSupplierAsync(created.Id, CancellationToken.None);
        Assert.Equal(45, read.DefaultPaymentTermDays);
    }

    [Fact]
    public async Task CreateWithoutTerm_DefaultsToNull()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        var read = await service.GetSupplierAsync(created.Id, CancellationToken.None);
        Assert.Null(read.DefaultPaymentTermDays);
    }

    [Fact]
    public async Task UpdateSupplierAsync_CanSetAndClearTerm()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);
        var created = await service.CreateSupplierAsync(
            new Supplier { Name = "Operador" }, CancellationToken.None);

        // Setear el plazo.
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", DefaultPaymentTermDays = 30 },
            CancellationToken.None);
        Assert.Equal(30, (await service.GetSupplierAsync(created.Id, CancellationToken.None)).DefaultPaymentTermDays);

        // Borrar el plazo (null) -> vuelve a "sin vencimiento".
        await service.UpdateSupplierAsync(
            created.Id,
            new Supplier { Name = "Operador", DefaultPaymentTermDays = null },
            CancellationToken.None);
        Assert.Null((await service.GetSupplierAsync(created.Id, CancellationToken.None)).DefaultPaymentTermDays);
    }

    // ---------------------------------------------------------------------------------------------------
    // Vencimiento sugerido por linea en la lista de servicios de la cuenta del proveedor
    // ---------------------------------------------------------------------------------------------------

    private static async Task<(Supplier supplier, Reserva reserva)> SeedSupplierAndConfirmedReservaAsync(
        AppDbContext context, int? defaultPaymentTermDays)
    {
        var supplier = new Supplier { Name = "Operador", DefaultPaymentTermDays = defaultPaymentTermDays };
        var reserva = new Reserva
        {
            NumeroReserva = "F-2026-T5",
            Name = "Reserva T5",
            Status = EstadoReserva.Confirmed
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (supplier, reserva);
    }

    private static async Task SeedHotelBookingAsync(
        AppDbContext context, int supplierId, int reservaId, DateTime createdAt)
    {
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            HotelName = "Hotel T5",
            City = "Bariloche",
            CheckIn = createdAt.AddDays(20),
            CheckOut = createdAt.AddDays(22),
            Nights = 2,
            CreatedAt = createdAt
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetSupplierAccountServices_WithTerm_SetsSuggestedDueDatePerLine()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndConfirmedReservaAsync(context, defaultPaymentTermDays: 30);
        var purchaseDate = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        await SeedHotelBookingAsync(context, supplier.Id, reserva.Id, purchaseDate);

        var service = new SupplierService(context);
        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var line = Assert.Single(page.Items);
        Assert.Equal(purchaseDate, line.Date);
        Assert.Equal(purchaseDate.AddDays(30), line.SuggestedDueDate);
    }

    [Fact]
    public async Task GetSupplierAccountServices_WithoutTerm_LeavesSuggestedDueDateNull()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndConfirmedReservaAsync(context, defaultPaymentTermDays: null);
        var purchaseDate = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        await SeedHotelBookingAsync(context, supplier.Id, reserva.Id, purchaseDate);

        var service = new SupplierService(context);
        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var line = Assert.Single(page.Items);
        Assert.Null(line.SuggestedDueDate);
    }
}
