using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// B1.15 Fase 0' (CODE-01..06, 13, 14): tests del helper estatico que centraliza
/// los guards de mutacion. Cubre:
///  - GetPaymentMutationBlockReasonAsync: receipt Issued, receipt Voided,
///    factura CAE viva, sin guards (allow).
///  - GetServiceMutationBlockReasonAsync: reserva con CAE viva, voucher Issued,
///    sin guards.
///  - GetReservaDatesMutationBlockReasonAsync: simetrico al de servicio.
///  - GetBookingMutationBlockReasonAsync: simetrico, mensajes por tipo.
///  - GetCustomerTaxIdMutationBlockReasonAsync: factura CAE viva del payer.
///  - GetSupplierTaxIdMutationBlockReasonAsync: hotel/transfer/package/flight
///    referenciando al supplier en reserva con CAE viva.
///  - GetPassengerMutationBlockReasonAsync: voucher Issued con assignment, o
///    reserva con CAE viva.
///
/// Patron de tests calcado de DeleteGuards (PaymentServiceDeleteTests.cs).
/// </summary>
public class MutationGuardsTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public MutationGuardsTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    private async Task<AppDbContext> SeedReservaAsync(int reservaId = 1, int payerId = 0)
    {
        var ctx = new AppDbContext(_dbOptions);
        var reserva = new Reserva
        {
            Id = reservaId,
            NumeroReserva = $"F-2026-{reservaId:D4}",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            PayerId = payerId == 0 ? null : payerId
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return ctx;
    }

    private static Invoice MakeInvoice(int id, int reservaId, string? cae = "012345", AnnulmentStatus status = AnnulmentStatus.None)
        => new()
        {
            Id = id,
            ReservaId = reservaId,
            CAE = cae,
            AnnulmentStatus = status,
            ImporteTotal = 100m,
            ImporteNeto = 82.64m,
            ImporteIva = 17.36m
        };

    // ============= GetPaymentMutationBlockReasonAsync =============

    [Fact]
    public async Task PaymentMutation_NoReceiptNoInvoice_AllowsEdit()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Payments.Add(new Payment { Id = 10, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 10);
        Assert.Null(reason);
    }

    [Fact]
    public async Task PaymentMutation_ReceiptIssued_BlocksWithEmittedMessage()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Payments.Add(new Payment { Id = 11, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid" });
        ctx.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 50, PaymentId = 11, ReservaId = 1,
            ReceiptNumber = "REC-001", Amount = 100m,
            Status = PaymentReceiptStatuses.Issued, IssuedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 11);
        Assert.NotNull(reason);
        Assert.Contains("recibo emitido", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentMutation_ReceiptVoided_BlocksWithAuditMessage()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Payments.Add(new Payment { Id = 12, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid" });
        ctx.PaymentReceipts.Add(new PaymentReceipt
        {
            Id = 51, PaymentId = 12, ReservaId = 1,
            ReceiptNumber = "REC-002", Amount = 100m,
            Status = PaymentReceiptStatuses.Voided,
            IssuedAt = DateTime.UtcNow.AddDays(-1), VoidedAt = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 12);
        Assert.NotNull(reason);
        Assert.Contains("anulado", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("auditoria", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentMutation_RelatedInvoiceCaeAlive_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(60, 1));
        ctx.Payments.Add(new Payment
        {
            Id = 13, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid",
            RelatedInvoiceId = 60
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 13);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaymentMutation_RelatedInvoiceAnnulled_Allows()
    {
        // Si la NC fue aprobada (Succeeded), el bloqueo se levanta — la factura
        // ya no esta viva fiscalmente.
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(61, 1, status: AnnulmentStatus.Succeeded));
        ctx.Payments.Add(new Payment
        {
            Id = 14, ReservaId = 1, Amount = 100m, Method = "Cash", Status = "Paid",
            RelatedInvoiceId = 61
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPaymentMutationBlockReasonAsync(ctx, 14);
        Assert.Null(reason);
    }

    // ============= GetServiceMutationBlockReasonAsync =============

    [Fact]
    public async Task ServiceMutation_ReservaWithLiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 100, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 1000m, NetCost = 0m
        });
        ctx.Invoices.Add(MakeInvoice(70, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 100);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CAE", reason);
    }

    [Fact]
    public async Task ServiceMutation_ReservaWithIssuedVoucher_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 101, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 1000m, NetCost = 0m
        });
        ctx.Vouchers.Add(new Voucher
        {
            Id = 80, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Issued
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 101);
        Assert.NotNull(reason);
        Assert.Contains("voucher", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServiceMutation_ReservaClean_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 102, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 1000m, NetCost = 0m
        });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetServiceMutationBlockReasonAsync(ctx, 102);
        Assert.Null(reason);
    }

    // ============= GetReservaDatesMutationBlockReasonAsync =============

    [Fact]
    public async Task DatesMutation_LiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(71, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaDatesMutationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("fechas", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatesMutation_AnnulledCae_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(72, 1, status: AnnulmentStatus.Succeeded));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetReservaDatesMutationBlockReasonAsync(ctx, 1);
        Assert.Null(reason);
    }

    // ============= GetBookingMutationBlockReasonAsync =============

    [Fact]
    public async Task BookingMutation_HotelLabel_LiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(73, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetBookingMutationBlockReasonAsync(ctx, 1, "Hotel");
        Assert.NotNull(reason);
        Assert.Contains("hotel", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BookingMutation_FlightLabel_LiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Invoices.Add(MakeInvoice(74, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetBookingMutationBlockReasonAsync(ctx, 1, "Flight");
        Assert.NotNull(reason);
        Assert.Contains("vuelo", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BookingMutation_NoCaeNoVoucher_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        var reason = await MutationGuards.GetBookingMutationBlockReasonAsync(ctx, 1, "Hotel");
        Assert.Null(reason);
    }

    // ============= GetCustomerTaxIdMutationBlockReasonAsync =============

    [Fact]
    public async Task CustomerTaxIdMutation_LiveCaeOnPayer_Blocks()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var customer = new Customer { Id = 1, FullName = "Cliente test" };
        ctx.Customers.Add(customer);
        ctx.Reservas.Add(new Reserva
        {
            Id = 5,
            NumeroReserva = "F-CLI-1",
            Name = "Reserva cli",
            Status = EstadoReserva.Confirmed,
            PayerId = 1
        });
        await ctx.SaveChangesAsync();
        ctx.Invoices.Add(MakeInvoice(80, 5));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("CUIT", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomerTaxIdMutation_NoInvoices_Allows()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        ctx.Customers.Add(new Customer { Id = 2, FullName = "Cliente sin facturas" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync(ctx, 2);
        Assert.Null(reason);
    }

    // ============= GetSupplierTaxIdMutationBlockReasonAsync =============

    [Fact]
    public async Task SupplierTaxIdMutation_HotelInLiveCaeReserva_Blocks()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        ctx.Suppliers.Add(new Supplier { Id = 1, Name = "Operador X" });
        ctx.Reservas.Add(new Reserva
        {
            Id = 7, NumeroReserva = "F-SUP-1", Name = "Res supl",
            Status = EstadoReserva.Confirmed
        });
        await ctx.SaveChangesAsync();
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 90, ReservaId = 7, SupplierId = 1,
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13),
            HotelName = "Hotel Y", SalePrice = 500m, NetCost = 300m
        });
        ctx.Invoices.Add(MakeInvoice(81, 7));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetSupplierTaxIdMutationBlockReasonAsync(ctx, 1);
        Assert.NotNull(reason);
        Assert.Contains("CUIT", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupplierTaxIdMutation_NoBookingInLiveCae_Allows()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        ctx.Suppliers.Add(new Supplier { Id = 2, Name = "Operador limpio" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetSupplierTaxIdMutationBlockReasonAsync(ctx, 2);
        Assert.Null(reason);
    }

    // ============= GetPassengerMutationBlockReasonAsync =============

    [Fact]
    public async Task PassengerMutation_AssignedToIssuedVoucher_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 30, ReservaId = 1, FullName = "Pax test" });
        ctx.Vouchers.Add(new Voucher { Id = 90, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Issued });
        ctx.VoucherPassengerAssignments.Add(new VoucherPassengerAssignment { Id = 1, VoucherId = 90, PassengerId = 30 });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 30);
        Assert.NotNull(reason);
        Assert.Contains("voucher", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PassengerMutation_ReservaWithLiveCae_Blocks()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 31, ReservaId = 1, FullName = "Pax CAE" });
        ctx.Invoices.Add(MakeInvoice(82, 1));
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 31);
        Assert.NotNull(reason);
        Assert.Contains("factura", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PassengerMutation_NoConstraints_Allows()
    {
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 32, ReservaId = 1, FullName = "Pax libre" });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 32);
        Assert.Null(reason);
    }

    [Fact]
    public async Task PassengerMutation_VoucherDraft_Allows()
    {
        // Voucher en estado Draft (no Issued) NO bloquea — solo Issued importa.
        await using var ctx = await SeedReservaAsync();
        ctx.Passengers.Add(new Passenger { Id = 33, ReservaId = 1, FullName = "Pax draft" });
        ctx.Vouchers.Add(new Voucher { Id = 91, ReservaId = 1, FileName = "v.pdf", Status = VoucherStatuses.Draft });
        ctx.VoucherPassengerAssignments.Add(new VoucherPassengerAssignment { Id = 2, VoucherId = 91, PassengerId = 33 });
        await ctx.SaveChangesAsync();

        var reason = await MutationGuards.GetPassengerMutationBlockReasonAsync(ctx, 33);
        Assert.Null(reason);
    }
}
