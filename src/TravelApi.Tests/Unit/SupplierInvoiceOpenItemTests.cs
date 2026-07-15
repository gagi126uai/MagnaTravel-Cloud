using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Authorization;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class SupplierInvoiceOpenItemTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Supplier Supplier, Reserva Reserva, HotelBooking Service)> SeedAsync(AppDbContext db, string currency = "ARS")
    {
        var supplier = new Supplier { Name = "Operador", TaxId = "30-12345678-9", TaxCondition = "IVA_RESP_INSCRIPTO" };
        var reserva = new Reserva { NumeroReserva = "R-BILL-1", Name = "Reserva factura", Status = EstadoReserva.Confirmed };
        db.AddRange(supplier, reserva);
        await db.SaveChangesAsync();
        var service = new HotelBooking
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, HotelName = "Hotel",
            City = "Posadas", CheckIn = DateTime.UtcNow.AddDays(1), CheckOut = DateTime.UtcNow.AddDays(2),
            Nights = 1, Status = "Confirmado", Currency = currency, NetCost = 100m, SalePrice = 120m
        };
        db.HotelBookings.Add(service);
        db.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency
        {
            SupplierId = supplier.Id, Currency = currency, ConfirmedPurchases = 100m, Balance = 100m
        });
        await db.SaveChangesAsync();
        return (supplier, reserva, service);
    }

    private static SupplierInvoiceCreateRequest Bill(HotelBooking service, string number = " fac-001 ", string currency = "ARS") =>
        new(number, currency, DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(15),
            new[] { new SupplierInvoiceLineRequest("hotel", service.PublicId, 100m) });

    [Fact]
    public async Task Create_ReclassifiesServiceWithoutDuplicatingSupplierDebt_AndNormalizesNumber()
    {
        await using var db = CreateContext();
        var seeded = await SeedAsync(db);
        var service = new SupplierService(db);

        var dto = await service.CreateSupplierInvoiceAsync(seeded.Supplier.Id, Bill(seeded.Service), default);

        Assert.Equal("pendiente", dto.Status);
        Assert.Equal("FAC-001", await db.SupplierInvoices.Select(x => x.Number).SingleAsync());
        Assert.Equal(100m, await db.SupplierBalanceByCurrency.Select(x => x.Balance).SingleAsync());
        Assert.Single(await db.SupplierInvoiceLines.ToListAsync());
    }

    [Fact]
    public async Task Create_RejectsCurrencyDifferentFromService()
    {
        await using var db = CreateContext();
        var seeded = await SeedAsync(db, "USD");
        var service = new SupplierService(db);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateSupplierInvoiceAsync(seeded.Supplier.Id, Bill(seeded.Service, currency: "ARS"), default));
        Assert.Contains("misma moneda", error.Message);
    }

    [Fact]
    public async Task Apply_IsExplicit_Idempotent_AndCannotOverapplyPaymentOrInvoice()
    {
        await using var db = CreateContext();
        var seeded = await SeedAsync(db);
        var payment = new SupplierPayment { SupplierId = seeded.Supplier.Id, Amount = 60m, Currency = "ARS", Method = "Transfer" };
        db.SupplierPayments.Add(payment);
        await db.SaveChangesAsync();
        var service = new SupplierService(db);
        var invoice = await service.CreateSupplierInvoiceAsync(seeded.Supplier.Id, Bill(seeded.Service), default);

        var request = new SupplierInvoicePaymentApplicationRequest(payment.PublicId, 60m);
        var first = await service.ApplySupplierPaymentToInvoiceAsync(seeded.Supplier.Id, invoice.PublicId, request, default);
        var repeated = await service.ApplySupplierPaymentToInvoiceAsync(seeded.Supplier.Id, invoice.PublicId, request, default);

        Assert.Equal("pago_parcial", first.Status);
        Assert.Equal("pago_parcial", repeated.Status);
        Assert.Single(await db.SupplierInvoicePaymentApplications.ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySupplierPaymentToInvoiceAsync(
            seeded.Supplier.Id, invoice.PublicId, new SupplierInvoicePaymentApplicationRequest(payment.PublicId, 61m), default));

        var secondPayment = new SupplierPayment { SupplierId = seeded.Supplier.Id, Amount = 50m, Currency = "ARS", Method = "Transfer" };
        db.SupplierPayments.Add(secondPayment);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySupplierPaymentToInvoiceAsync(
            seeded.Supplier.Id, invoice.PublicId, new SupplierInvoicePaymentApplicationRequest(secondPayment.PublicId, 50m), default));
    }

    [Fact]
    public async Task Reverse_KeepsImmutableAudit_IsIdempotent_AndReopensInvoice()
    {
        await using var db = CreateContext();
        var seeded = await SeedAsync(db);
        var payment = new SupplierPayment { SupplierId = seeded.Supplier.Id, Amount = 100m, Currency = "ARS", Method = "Transfer" };
        db.SupplierPayments.Add(payment);
        await db.SaveChangesAsync();
        var service = new SupplierService(db);
        var invoice = await service.CreateSupplierInvoiceAsync(seeded.Supplier.Id, Bill(seeded.Service), default);
        var paid = await service.ApplySupplierPaymentToInvoiceAsync(seeded.Supplier.Id, invoice.PublicId,
            new SupplierInvoicePaymentApplicationRequest(payment.PublicId, 100m), default);
        var application = Assert.Single(paid.Applications);

        var reversed = await service.ReverseSupplierInvoicePaymentApplicationAsync(
            seeded.Supplier.Id, invoice.PublicId, application.PublicId, "Pago imputado al documento equivocado", default);
        var repeated = await service.ReverseSupplierInvoicePaymentApplicationAsync(
            seeded.Supplier.Id, invoice.PublicId, application.PublicId, "La segunda llamada debe ser idempotente", default);

        Assert.Equal("pendiente", reversed.Status);
        Assert.Equal(0m, reversed.Applied);
        Assert.Equal("pendiente", repeated.Status);
        Assert.True(Assert.Single(reversed.Applications).IsReversed);
        Assert.Single(await db.SupplierInvoicePaymentApplicationReversals.ToListAsync());
        Assert.Equal("Pago imputado al documento equivocado",
            (await db.SupplierInvoicePaymentApplicationReversals.SingleAsync()).Reason);

        payment.IsDeleted = true;
        await db.SaveChangesAsync();
        var afterPaymentSoftDelete = Assert.Single(await service.GetSupplierInvoicesAsync(seeded.Supplier.Id, default));
        Assert.True(Assert.Single(afterPaymentSoftDelete.Applications).IsReversed);
    }

    [Fact]
    public async Task PaymentSelector_PagesAllMatchesAndFiltersByEffectiveCurrency()
    {
        await using var db = CreateContext();
        var seeded = await SeedAsync(db);
        for (var i = 0; i < 30; i++)
            db.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = seeded.Supplier.Id, Amount = i + 1, Currency = "ARS", Method = "Transfer",
                Reference = $"ARS-{i:D2}", PaidAt = DateTime.UtcNow.AddMinutes(i)
            });
        for (var i = 0; i < 5; i++)
            db.SupplierPayments.Add(new SupplierPayment
            {
                SupplierId = seeded.Supplier.Id, Amount = i + 1, Currency = "USD", Method = "Transfer",
                Reference = $"USD-{i:D2}", PaidAt = DateTime.UtcNow.AddMinutes(i)
            });
        await db.SaveChangesAsync();
        var service = new SupplierService(db);

        var first = await service.GetSupplierAccountPaymentsAsync(seeded.Supplier.Id,
            new SupplierAccountPaymentsQuery { Currency = "ARS", Page = 1, PageSize = 25 }, default);
        var second = await service.GetSupplierAccountPaymentsAsync(seeded.Supplier.Id,
            new SupplierAccountPaymentsQuery { Currency = "ARS", Page = 2, PageSize = 25 }, default);

        Assert.Equal(30, first.TotalCount);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Equal(30, first.Items.Concat(second.Items).Select(x => x.PublicId).Distinct().Count());
        Assert.All(first.Items.Concat(second.Items), x => Assert.Equal("ARS", x.Currency));
    }

    [Fact]
    public async Task ServiceSelector_PagesAllMatchesAndFiltersByCurrency()
    {
        await using var db = CreateContext();
        var seeded = await SeedAsync(db);
        for (var i = 1; i < 30; i++)
            db.HotelBookings.Add(new HotelBooking
            {
                SupplierId = seeded.Supplier.Id, ReservaId = seeded.Reserva.Id, HotelName = $"Hotel {i:D2}",
                City = "Posadas", CheckIn = DateTime.UtcNow.AddDays(i), CheckOut = DateTime.UtcNow.AddDays(i + 1),
                Nights = 1, Status = "Confirmado", Currency = "ARS", NetCost = 100m, SalePrice = 120m
            });
        db.HotelBookings.Add(new HotelBooking
        {
            SupplierId = seeded.Supplier.Id, ReservaId = seeded.Reserva.Id, HotelName = "Hotel USD",
            City = "Posadas", CheckIn = DateTime.UtcNow.AddDays(40), CheckOut = DateTime.UtcNow.AddDays(41),
            Nights = 1, Status = "Confirmado", Currency = "USD", NetCost = 100m, SalePrice = 120m
        });
        await db.SaveChangesAsync();
        var service = new SupplierService(db);

        var first = await service.GetSupplierAccountServicesAsync(seeded.Supplier.Id,
            new SupplierAccountServicesQuery { Currency = "ARS", Page = 1, PageSize = 25 }, default);
        var second = await service.GetSupplierAccountServicesAsync(seeded.Supplier.Id,
            new SupplierAccountServicesQuery { Currency = "ARS", Page = 2, PageSize = 25 }, default);

        Assert.Equal(30, first.TotalCount);
        Assert.Equal(2, first.TotalPages);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Equal(30, first.Items.Concat(second.Items).Select(x => x.PublicId).Distinct().Count());
        Assert.All(first.Items.Concat(second.Items), x => Assert.Equal("ARS", x.Currency));
    }

    [Theory]
    [InlineData(nameof(SuppliersController.CreateSupplierInvoice), Permissions.TesoreriaSupplierPayments)]
    [InlineData(nameof(SuppliersController.ApplySupplierPaymentToInvoice), Permissions.TesoreriaSupplierPayments)]
    [InlineData(nameof(SuppliersController.ReverseSupplierInvoicePaymentApplication), Permissions.TesoreriaSupplierPayments)]
    [InlineData(nameof(SuppliersController.VoidSupplierInvoice), Permissions.TesoreriaSupplierPayments)]
    public void MutationEndpoints_RequireBusinessPermissionAndSeeCost(string methodName, string businessPermission)
    {
        var policies = typeof(SuppliersController).GetMethod(methodName)!
            .GetCustomAttributes<RequirePermissionAttribute>()
            .SelectMany(x => RequirePermissionAttribute.TryParsePolicyName(x.Policy!)!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(businessPermission, policies);
        Assert.Contains(Permissions.CobranzasSeeCost, policies);
    }

    [Fact]
    public void Model_ClosesDuplicateNumberAndDuplicatePaymentApplicationRaces()
    {
        using var db = CreateContext();
        var invoiceIndexes = db.Model.FindEntityType(typeof(SupplierInvoice))!.GetIndexes();
        var applicationIndexes = db.Model.FindEntityType(typeof(SupplierInvoicePaymentApplication))!.GetIndexes();
        Assert.Contains(invoiceIndexes, x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "SupplierId", "Number" }));
        Assert.Contains(applicationIndexes, x => x.IsUnique
            && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "SupplierInvoiceId", "SupplierPaymentId" })
            && x.GetFilter() == "\"IsReversed\" = FALSE");
        var reversalIndexes = db.Model.FindEntityType(typeof(SupplierInvoicePaymentApplicationReversal))!.GetIndexes();
        Assert.Contains(reversalIndexes, x => x.IsUnique
            && x.Properties.Select(p => p.Name).SequenceEqual(new[] { "SupplierInvoicePaymentApplicationId" }));
    }
}
